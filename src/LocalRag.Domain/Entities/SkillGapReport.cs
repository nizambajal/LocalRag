namespace LocalRag.Domain.Entities;

public enum SkillRequirementType
{
    Required,
    Preferred
}

/// <summary>
/// A single piece of CV evidence backing a skill classification.
/// </summary>
public class SkillEvidence
{
    public required string Text { get; init; }
    public required string Source { get; init; }
    public int Page { get; init; }
    public float Score { get; init; }
}

/// <summary>
/// Classification of one JD skill against CV evidence. Always evidence-backed —
/// classification without evidence is only ever "Missing".
/// </summary>
public class SkillAssessment
{
    public required string Skill { get; init; }
    public SkillRequirementType RequirementType { get; init; }

    /// <summary>One of: "Strong Match", "Partial Match", "Weak Evidence", "Missing".</summary>
    public required string Classification { get; init; }

    public string Reasoning { get; init; } = string.Empty;
    public List<SkillEvidence> Evidence { get; init; } = new();
}

public class SkillGapReport
{
    public string JobTitle { get; init; } = string.Empty;
    public List<SkillAssessment> Assessments { get; init; } = new();
}