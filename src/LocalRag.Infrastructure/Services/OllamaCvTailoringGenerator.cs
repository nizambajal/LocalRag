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
/// Generates tailored CV sections from a skill-gap report using Ollama.
/// Only Strong/Partial Match skills are given to the model as material to
/// write from — Weak/Missing skills are never mentioned, so the LLM has no
/// way to accidentally claim them. Any bullet without a cited evidence
/// line is dropped rather than trusted.
/// </summary>
public sealed class OllamaCvTailoringGenerator : ICvTailoringGenerator
{
    private static readonly HashSet<string> AllowedSourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Existing Experience", "Suggested Wording"
    };

    private readonly RagOptions _opts;
    private readonly ILogger<OllamaCvTailoringGenerator> _logger;
    private readonly HttpClient _http;

    public OllamaCvTailoringGenerator(
        IOptions<RagOptions> opts,
        ILogger<OllamaCvTailoringGenerator> logger,
        HttpClient http)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = http;
    }

    public async Task<List<TailoredCvSection>> GenerateAsync(
        SkillGapReport skillGap, CancellationToken ct = default)
    {
        var usableAssessments = skillGap.Assessments
            .Where(a =>
                a.Classification.Equals("Strong Match", StringComparison.OrdinalIgnoreCase) ||
                a.Classification.Equals("Partial Match", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (usableAssessments.Count == 0)
        {
            _logger.LogWarning(
                "No Strong/Partial Match evidence available for job title '{Title}' — " +
                "returning an empty tailored CV rather than inventing content.",
                skillGap.JobTitle);
            return new List<TailoredCvSection>();
        }

        var prompt = BuildPrompt(skillGap.JobTitle, usableAssessments);

        var request = new
        {
            model = _opts.OllamaModel,
            prompt,
            stream = false,
            format = "json",
            options = new
            {
                num_predict = 1400,
                temperature = 0.3, // more constrained than interview questions — this is a factual document
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
            _logger.LogError(ex, "Could not reach Ollama for CV tailoring");
            throw new InvalidOperationException(
                "Could not reach the local LLM (Ollama) to tailor the CV. " +
                "Make sure Ollama is running.", ex);
        }

        var result = await response.Content
            .ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
        var rawJson = result?.Response?.Trim();

        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidOperationException(
                "Ollama returned an empty response while tailoring the CV.");

        GeneratedJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GeneratedJson>(
                rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse tailored CV JSON: {Raw}", rawJson);
            throw new InvalidOperationException(
                "The local LLM did not return valid structured JSON for the tailored CV.", ex);
        }

        var sections = new List<TailoredCvSection>();
        foreach (var s in parsed?.Sections ?? new())
        {
            if (string.IsNullOrWhiteSpace(s.Title))
                continue;

            var bullets = new List<TailoredCvBullet>();
            foreach (var b in s.Bullets ?? new())
            {
                if (string.IsNullOrWhiteSpace(b.Text) || string.IsNullOrWhiteSpace(b.BasedOnEvidence))
                    continue; // no evidence cited → dropped, not trusted

                var sourceType = AllowedSourceTypes.FirstOrDefault(t =>
                    t.Equals(b.SourceType, StringComparison.OrdinalIgnoreCase)) ?? "Suggested Wording";

                bullets.Add(new TailoredCvBullet
                {
                    Text = b.Text,
                    SourceType = sourceType,
                    BasedOnEvidence = b.BasedOnEvidence
                });
            }

            if (bullets.Count > 0)
                sections.Add(new TailoredCvSection { Title = s.Title, Bullets = bullets });
        }

        return sections;
    }

    private static string BuildPrompt(string jobTitle, List<SkillAssessment> usableAssessments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are tailoring a candidate's CV for a specific role. You may ONLY use the");
        sb.AppendLine("CV EVIDENCE given below — never invent skills, achievements, or experience.");
        sb.AppendLine($"TARGET ROLE: {jobTitle}");
        sb.AppendLine();
        sb.AppendLine("CV EVIDENCE (the only material you may draw from):");
        foreach (var a in usableAssessments)
        {
            sb.AppendLine($"- Skill: {a.Skill} ({a.Classification})");
            foreach (var e in a.Evidence.Take(2))
                sb.AppendLine($"  Evidence: {e.Text}");
        }
        sb.AppendLine();
        sb.AppendLine("Produce 2-4 CV sections (e.g. \"Professional Summary\", \"Relevant Experience\", " +
                       "\"Technical Skills\") with bullet points. For EVERY bullet:");
        sb.AppendLine("- sourceType MUST be \"Existing Experience\" (near-verbatim from the evidence) or " +
                       "\"Suggested Wording\" (reframed/reworded, but still fully truthful to the evidence).");
        sb.AppendLine("- basedOnEvidence MUST quote or closely paraphrase the specific evidence line the " +
                       "bullet is drawn from. A bullet with no traceable evidence must not be produced.");
        sb.AppendLine("- Do NOT mention any skill that is not in the CV EVIDENCE list above.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY this JSON shape, no markdown, no commentary:");
        sb.AppendLine("{ \"sections\": [ { \"title\": string, \"bullets\": [ " +
                       "{ \"text\": string, \"sourceType\": string, \"basedOnEvidence\": string } ] } ] }");

        return sb.ToString();
    }

    private sealed record OllamaResponse(string Response);

    private sealed class GeneratedJson
    {
        public List<GeneratedSection>? Sections { get; set; }
    }

    private sealed class GeneratedSection
    {
        public string? Title { get; set; }
        public List<GeneratedBullet>? Bullets { get; set; }
    }

    private sealed class GeneratedBullet
    {
        public string? Text { get; set; }
        public string? SourceType { get; set; }
        public string? BasedOnEvidence { get; set; }
    }
}