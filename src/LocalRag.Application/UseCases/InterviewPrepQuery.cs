using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query ─────────────────────────────────────────────────────────────────────

public record InterviewPrepQuery(string JobDescriptionText) : IRequest<InterviewPrepPackage>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Implements Section 8 of the master prompt ("Interview Preparation Agent").
/// Reuses <see cref="SkillGapQuery"/> via MediatR rather than re-deriving
/// skill evidence — same classifications the candidate already saw in
/// compare_skills feed the question generator.
/// </summary>
public sealed class InterviewPrepQueryHandler
    : IRequestHandler<InterviewPrepQuery, InterviewPrepPackage>
{
    private readonly IMediator _mediator;
    private readonly IInterviewQuestionGenerator _generator;

    public InterviewPrepQueryHandler(IMediator mediator, IInterviewQuestionGenerator generator)
    {
        _mediator = mediator;
        _generator = generator;
    }

    public async Task<InterviewPrepPackage> Handle(InterviewPrepQuery request, CancellationToken ct)
    {
        var skillGap = await _mediator.Send(new SkillGapQuery(request.JobDescriptionText), ct);

        var questions = await _generator.GenerateAsync(skillGap, ct);

        // Weak areas are computed deterministically from the skill gap
        // report, never left to the LLM — this list must stay accurate
        // regardless of how well the question-generation prompt performs.
        var weakAreas = skillGap.Assessments
            .Where(a =>
                a.RequirementType == SkillRequirementType.Required &&
                (a.Classification.Equals("Weak Evidence", StringComparison.OrdinalIgnoreCase) ||
                 a.Classification.Equals("Missing", StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Skill)
            .ToList();

        return new InterviewPrepPackage
        {
            JobTitle = skillGap.JobTitle,
            Questions = questions,
            PotentialWeakAreas = weakAreas
        };
    }
}