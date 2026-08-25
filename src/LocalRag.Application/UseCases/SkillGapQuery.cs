using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query ─────────────────────────────────────────────────────────────────────

public record SkillGapQuery(
    string JobDescriptionText,
    int EvidenceTopK = 3
) : IRequest<SkillGapReport>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates the workflow from Section 7 of the master prompt:
///   Analyze JD → extract skills → search CV for each skill →
///   classify each as Strong Match / Partial Match / Weak Evidence / Missing.
/// Every classification carries the evidence it was based on. No skill is
/// ever classified as anything other than "Missing" without CV evidence.
/// </summary>
public sealed class SkillGapQueryHandler : IRequestHandler<SkillGapQuery, SkillGapReport>
{
    private readonly IJobDescriptionExtractor _jdExtractor;
    private readonly IHybridSearchService _search;
    private readonly ISkillClassifier _classifier;

    public SkillGapQueryHandler(
        IJobDescriptionExtractor jdExtractor,
        IHybridSearchService search,
        ISkillClassifier classifier)
    {
        _jdExtractor = jdExtractor;
        _search = search;
        _classifier = classifier;
    }

    public async Task<SkillGapReport> Handle(SkillGapQuery request, CancellationToken ct)
    {
        var jd = await _jdExtractor.ExtractAsync(request.JobDescriptionText, ct);

        // Combine required + preferred skills, deduping case-insensitively.
        // A skill already counted as Required keeps that tag even if it also
        // shows up (e.g. reworded) in PreferredSkills.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taggedSkills = new List<(string Skill, SkillRequirementType Type)>();

        foreach (var skill in jd.RequiredSkills)
        {
            if (seen.Add(skill))
                taggedSkills.Add((skill, SkillRequirementType.Required));
        }

        foreach (var skill in jd.PreferredSkills)
        {
            if (seen.Add(skill))
                taggedSkills.Add((skill, SkillRequirementType.Preferred));
        }

        var assessments = new List<SkillAssessment>(taggedSkills.Count);

        foreach (var (skill, requirementType) in taggedSkills)
        {
            ct.ThrowIfCancellationRequested();

            var results = await _search.SearchAsync(
                skill, topK: request.EvidenceTopK, ct: ct);

            if (results.Count == 0)
            {
                assessments.Add(new SkillAssessment
                {
                    Skill = skill,
                    RequirementType = requirementType,
                    Classification = "Missing",
                    Reasoning = "No CV evidence was retrieved for this skill.",
                    Evidence = new List<SkillEvidence>()
                });
                continue;
            }

            var (classification, reasoning) = await _classifier.ClassifyAsync(skill, results, ct);

            assessments.Add(new SkillAssessment
            {
                Skill = skill,
                RequirementType = requirementType,
                Classification = classification,
                Reasoning = reasoning,
                Evidence = results.Select(r => new SkillEvidence
                {
                    Text = r.Chunk.Content,
                    Source = r.Chunk.SourceFile,
                    Page = r.Chunk.PageNumber,
                    Score = r.CombinedScore
                }).ToList()
            });
        }

        return new SkillGapReport
        {
            JobTitle = jd.Title,
            Assessments = assessments
        };
    }
}