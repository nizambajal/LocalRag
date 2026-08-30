using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query ─────────────────────────────────────────────────────────────────────

public record TailorCvQuery(string JobDescriptionText) : IRequest<TailoredCvResult>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Implements Section 10 of the master prompt ("CV Tailoring Agent"). Reuses
/// the skill-gap workflow for consistent evidence, then generates sections.
/// Missing skills are computed deterministically and never appear in the
/// generated CV content — the LLM only ever writes from Strong/Partial
/// Match evidence.
/// </summary>
public sealed class TailorCvQueryHandler : IRequestHandler<TailorCvQuery, TailoredCvResult>
{
    private readonly IMediator _mediator;
    private readonly ICvTailoringGenerator _generator;

    public TailorCvQueryHandler(IMediator mediator, ICvTailoringGenerator generator)
    {
        _mediator = mediator;
        _generator = generator;
    }

    public async Task<TailoredCvResult> Handle(TailorCvQuery request, CancellationToken ct)
    {
        var skillGap = await _mediator.Send(new SkillGapQuery(request.JobDescriptionText), ct);

        var sections = await _generator.GenerateAsync(skillGap, ct);

        // Deterministic, not LLM-decided — same reasoning as InterviewPrepQueryHandler.
        var missingSkills = skillGap.Assessments
            .Where(a =>
                a.RequirementType == SkillRequirementType.Required &&
                (a.Classification.Equals("Weak Evidence", StringComparison.OrdinalIgnoreCase) ||
                 a.Classification.Equals("Missing", StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Skill)
            .ToList();

        return new TailoredCvResult
        {
            JobTitle = skillGap.JobTitle,
            Sections = sections,
            MissingSkills = missingSkills
        };
    }
}