using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Generates interview questions from a skill-gap report using Ollama.
/// Candidate-Specific questions are only allowed for Strong Match skills,
/// and any model answer without a cited evidence line is dropped rather
/// than trusted — the LLM is told the rule, but not trusted blindly.
/// </summary>
public sealed class OllamaInterviewQuestionGenerator : IInterviewQuestionGenerator
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Technical", "Scenario", "Architecture", "C#", ".NET", "SQL",
        "Azure", "AI/RAG", "Behavioral", "Candidate-Specific"
    };

    private readonly RagOptions _opts;
    private readonly ILogger<OllamaInterviewQuestionGenerator> _logger;
    private readonly HttpClient _http;

    public OllamaInterviewQuestionGenerator(
        IOptions<RagOptions> opts,
        ILogger<OllamaInterviewQuestionGenerator> logger,
        HttpClient http)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = http;
    }

    public async Task<List<InterviewQuestion>> GenerateAsync(
        SkillGapReport skillGap, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(skillGap);

        var request = new
        {
            model = _opts.OllamaModel,
            prompt,
            stream = false,
            format = "json",
            options = new
            {
                num_predict = 1200,
                temperature = 0.4, // a bit more room than extraction tasks — these are questions, not facts
                top_p = 0.9
            }
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                $"{_opts.OllamaBaseUrl}/api/generate", request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Could not reach Ollama for interview question generation");
            throw new InvalidOperationException(
                "Could not reach the local LLM (Ollama) to generate interview questions. " +
                "Make sure Ollama is running.", ex);
        }

        var result = await response.Content
            .ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
        var rawJson = result?.Response?.Trim();

        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidOperationException(
                "Ollama returned an empty response while generating interview questions.");

        GeneratedJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GeneratedJson>(
                rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse interview question JSON: {Raw}", rawJson);
            throw new InvalidOperationException(
                "The local LLM did not return valid structured JSON for interview questions.", ex);
        }

        var items = parsed?.Questions ?? new();
        var results = new List<InterviewQuestion>(items.Count);

        foreach (var q in items)
        {
            if (string.IsNullOrWhiteSpace(q.Category) || string.IsNullOrWhiteSpace(q.Question))
                continue;

            var category = AllowedCategories.FirstOrDefault(c =>
                c.Equals(q.Category, StringComparison.OrdinalIgnoreCase)) ?? "Technical";

            var isCandidateSpecific = category.Equals("Candidate-Specific", StringComparison.OrdinalIgnoreCase);
            var hasCitedEvidence = !string.IsNullOrWhiteSpace(q.BasedOnEvidence);

            results.Add(new InterviewQuestion
            {
                Category = category,
                Question = q.Question,
                // Defensive grounding check: only keep a model answer for
                // Candidate-Specific questions that actually cite evidence.
                // The prompt tells the model this rule, but we don't trust
                // it blindly — an answer with no cited evidence is dropped.
                ModelAnswer = isCandidateSpecific && hasCitedEvidence ? q.ModelAnswer : null,
                BasedOnEvidence = hasCitedEvidence ? q.BasedOnEvidence : null
            });
        }

        return results;
    }

    private static string BuildPrompt(SkillGapReport skillGap)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an interview preparation assistant helping a candidate prepare for a specific role.");
        sb.AppendLine($"ROLE: {skillGap.JobTitle}");
        sb.AppendLine();

        sb.AppendLine("SKILLS WITH STRONG CV EVIDENCE (safe to ask candidate-specific questions with model answers):");
        var strongMatches = skillGap.Assessments
            .Where(a => a.Classification.Equals("Strong Match", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var a in strongMatches)
        {
            sb.AppendLine($"- {a.Skill}");
            foreach (var e in a.Evidence.Take(1))
                sb.AppendLine($"  Evidence: {e.Text}");
        }
        if (strongMatches.Count == 0)
            sb.AppendLine("(none — do not generate any Candidate-Specific questions)");
        sb.AppendLine();

        sb.AppendLine("SKILLS WITH WEAK OR MISSING CV EVIDENCE (probe honestly — no assumed experience, no model answers):");
        var gaps = skillGap.Assessments
            .Where(a =>
                a.Classification.Equals("Weak Evidence", StringComparison.OrdinalIgnoreCase) ||
                a.Classification.Equals("Missing", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var a in gaps)
            sb.AppendLine($"- {a.Skill}");
        if (gaps.Count == 0)
            sb.AppendLine("(none)");
        sb.AppendLine();

        sb.AppendLine("Generate interview questions across these categories only:");
        sb.AppendLine("Technical, Scenario, Architecture, C#, .NET, SQL, Azure, AI/RAG, Behavioral, Candidate-Specific");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- \"Candidate-Specific\" questions MUST be about a skill from the STRONG CV EVIDENCE list above, " +
                       "and modelAnswer MUST be derived ONLY from the evidence text given — never invent details not " +
                       "present in the evidence. Set basedOnEvidence to a short quote/paraphrase of the evidence line used.");
        sb.AppendLine("- For weak/missing skills, write Scenario or Behavioral questions that probe the gap honestly " +
                       "(e.g. \"Have you worked with X? If not, how would you approach learning it quickly?\") — " +
                       "do NOT write a modelAnswer for these, and leave basedOnEvidence null.");
        sb.AppendLine("- Technical/Architecture/C#/.NET/SQL/Azure/AI-RAG questions can be generic professional-level " +
                       "questions relevant to this role — leave modelAnswer and basedOnEvidence null for these.");
        sb.AppendLine("- Produce 10-16 questions total, spread reasonably across categories relevant to this role.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY this JSON shape, no markdown, no commentary:");
        sb.AppendLine("{ \"questions\": [ { \"category\": string, \"question\": string, " +
                       "\"modelAnswer\": string|null, \"basedOnEvidence\": string|null } ] }");

        return sb.ToString();
    }

    private sealed record OllamaResponse(string Response);

    private sealed class GeneratedJson
    {
        public List<GeneratedQuestion>? Questions { get; set; }
    }

    private sealed class GeneratedQuestion
    {
        public string? Category { get; set; }
        public string? Question { get; set; }
        public string? ModelAnswer { get; set; }
        public string? BasedOnEvidence { get; set; }
    }
}