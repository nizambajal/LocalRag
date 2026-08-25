namespace LocalRag.Domain.Entities;

/// <summary>
/// One bullet in a tailored CV section. Every bullet must trace to CV
/// evidence — this is not a place for invented experience.
/// </summary>
public class TailoredCvBullet
{
    public required string Text { get; init; }

    /// <summary>"Existing Experience" (near-verbatim from the CV) or
    /// "Suggested Wording" (rephrased/reframed, but still truthful and
    /// traceable to evidence).</summary>
    public required string SourceType { get; init; }

    public string? BasedOnEvidence { get; init; }
}

public class TailoredCvSection
{
    public required string Title { get; init; }
    public List<TailoredCvBullet> Bullets { get; init; } = new();
}

/// <summary>
/// Master prompt §10 output: a tailored CV that stays truthful to the
/// source CV, with missing skills listed separately rather than implied.
/// </summary>
public class TailoredCvResult
{
    public string JobTitle { get; init; } = string.Empty;
    public List<TailoredCvSection> Sections { get; init; } = new();

    /// <summary>Deterministic — computed from the skill-gap report, never
    /// left to the LLM. These must NOT appear anywhere in the tailored CV
    /// sections above.</summary>
    public List<string> MissingSkills { get; init; } = new();
}