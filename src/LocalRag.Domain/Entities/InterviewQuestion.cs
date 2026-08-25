namespace LocalRag.Domain.Entities;

/// <summary>
/// One interview question. <see cref="ModelAnswer"/> is only ever populated
/// for Candidate-Specific questions grounded in Strong Match CV evidence —
/// never fabricated, never attached to a gap the CV doesn't cover.
/// </summary>
public class InterviewQuestion
{
    public required string Category { get; init; }
    public required string Question { get; init; }
    public string? ModelAnswer { get; init; }
    public string? BasedOnEvidence { get; init; }
}

/// <summary>
/// Full interview-prep package for one role: categorized questions plus a
/// deterministic (not LLM-decided) list of areas where CV evidence is
/// weak or missing, so the candidate knows what to actually study.
/// </summary>
public class InterviewPrepPackage
{
    public string JobTitle { get; init; } = string.Empty;
    public List<InterviewQuestion> Questions { get; init; } = new();
    public List<string> PotentialWeakAreas { get; init; } = new();
}