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
/// Classifies a skill against retrieved CV evidence into one of:
/// Strong Match, Partial Match, Weak Evidence, Missing.
/// Always grounded in the evidence text passed in — never invents.
/// </summary>
public sealed class OllamaSkillClassifier : ISkillClassifier
{
    private static readonly HashSet<string> AllowedClassifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "Strong Match", "Partial Match", "Weak Evidence", "Missing"
    };

    private readonly RagOptions _opts;
    private readonly ILogger<OllamaSkillClassifier> _logger;
    private readonly HttpClient _http;

    public OllamaSkillClassifier(
        IOptions<RagOptions> opts,
        ILogger<OllamaSkillClassifier> logger,
        HttpClient http)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = http;
    }

    public async Task<(string Classification, string Reasoning)> ClassifyAsync(
        string skill,
        IReadOnlyList<HybridSearchResult> evidence,
        CancellationToken ct = default)
    {
        // Caller should short-circuit empty evidence to "Missing" without
        // calling this — but defend anyway.
        if (evidence.Count == 0)
            return ("Missing", "No CV evidence was retrieved for this skill.");

        var prompt = BuildClassificationPrompt(skill, evidence);

        var request = new
        {
            model = _opts.OllamaModel,
            prompt,
            stream = false,
            format = "json",
            options = new
            {
                num_predict = 200,
                temperature = 0.1,
                top_p = 0.9
            }
        };

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"{_opts.OllamaBaseUrl}/api/generate", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
            var rawJson = result?.Response?.Trim();

            if (string.IsNullOrWhiteSpace(rawJson))
                throw new InvalidOperationException("Empty classification response.");

            var parsed = JsonSerializer.Deserialize<ClassificationJson>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var classification = parsed?.Classification?.Trim();
            var reasoning = parsed?.Reasoning?.Trim() ?? string.Empty;

            if (classification is null || !AllowedClassifications.Contains(classification))
            {
                _logger.LogWarning(
                    "Skill classifier returned unexpected value '{Value}' for skill '{Skill}' — " +
                    "falling back to score-based classification.", classification, skill);
                return FallbackClassify(evidence);
            }

            // Normalize casing to the canonical form
            var canonical = AllowedClassifications.First(c =>
                c.Equals(classification, StringComparison.OrdinalIgnoreCase));

            return (canonical, reasoning);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex,
                "Could not get LLM classification for skill '{Skill}' — falling back to score-based classification.",
                skill);
            return FallbackClassify(evidence);
        }
    }

    /// <summary>
    /// Deterministic fallback if Ollama is unreachable or returns garbage —
    /// keeps the pipeline usable rather than failing the whole report.
    /// Based on rank position, not absolute RRF score (which is not
    /// meaningfully comparable across queries).
    /// </summary>
    private static (string, string) FallbackClassify(IReadOnlyList<HybridSearchResult> evidence)
    {
        var topRank = evidence.Min(e => e.Rank);
        var classification = topRank switch
        {
            1 => "Strong Match",
            2 or 3 => "Partial Match",
            _ => "Weak Evidence"
        };

        return (classification,
            "Score-based fallback classification (LLM unavailable) — verify manually.");
    }

    private static string BuildClassificationPrompt(
        string skill, IReadOnlyList<HybridSearchResult> evidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are assessing whether a candidate's CV supports a specific skill.");
        sb.AppendLine("Use ONLY the CV EVIDENCE below. Do not assume anything not stated in it.");
        sb.AppendLine();
        sb.AppendLine($"SKILL TO ASSESS: {skill}");
        sb.AppendLine();
        sb.AppendLine("CV EVIDENCE:");

        for (int i = 0; i < evidence.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] (source: {evidence[i].Chunk.SourceFile}, page {evidence[i].Chunk.PageNumber})");
            sb.AppendLine(evidence[i].Chunk.Content);
            sb.AppendLine();
        }

        sb.AppendLine("Classify the strength of evidence for this skill into EXACTLY one of:");
        sb.AppendLine("- \"Strong Match\": the evidence directly and clearly demonstrates this skill.");
        sb.AppendLine("- \"Partial Match\": the evidence suggests related/adjacent experience, not a direct match.");
        sb.AppendLine("- \"Weak Evidence\": the evidence only tangentially mentions this or something similar.");
        sb.AppendLine("- \"Missing\": the evidence does not support this skill at all.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY this JSON shape, no markdown, no commentary:");
        sb.AppendLine("{ \"classification\": string, \"reasoning\": string (one sentence) }");

        return sb.ToString();
    }

    private sealed record OllamaResponse(string Response);

    private sealed class ClassificationJson
    {
        public string? Classification { get; set; }
        public string? Reasoning { get; set; }
    }
}