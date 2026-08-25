namespace LocalRag.Domain.Entities;

/// <summary>
/// Structured requirements extracted from a free-text job description.
/// This is extraction only — it never judges whether a candidate is
/// qualified; that reasoning belongs to the agent, using this alongside
/// CV evidence from hybrid search.
/// </summary>
public class JobDescriptionAnalysis
{
    public string Title { get; init; } = string.Empty;
    public List<string> RequiredSkills { get; init; } = new();
    public List<string> PreferredSkills { get; init; } = new();
    public int? YearsOfExperience { get; init; }
    public List<string> Responsibilities { get; init; } = new();
    public List<string> Technologies { get; init; } = new();
    public List<string> DomainKnowledge { get; init; } = new();
    public List<string> Certifications { get; init; } = new();
}