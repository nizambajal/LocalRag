using System.Net.Http.Json;
using System.Text.Json;
using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Extracts structured requirements from a job description using the local
/// Ollama model in JSON mode. Extraction only — never judges candidate fit.
/// </summary>
public sealed class OllamaJobDescriptionExtractor : IJobDescriptionExtractor
{
    private readonly RagOptions _opts;
    private readonly ILogger<OllamaJobDescriptionExtractor> _logger;
    private readonly HttpClient _http;

    public OllamaJobDescriptionExtractor(
        IOptions<RagOptions> opts,
        ILogger<OllamaJobDescriptionExtractor> logger,
        HttpClient http)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = http;
    }

    public async Task<JobDescriptionAnalysis> ExtractAsync(
        string jobDescriptionText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobDescriptionText))
            throw new ArgumentException(
                "Job description text must not be empty.", nameof(jobDescriptionText));

        var prompt = BuildExtractionPrompt(jobDescriptionText);

        _logger.LogInformation(
            "Extracting structured requirements from job description ({Len} chars)",
            jobDescriptionText.Length);

        var request = new
        {
            model = _opts.OllamaModel,
            prompt,
            stream = false,
            format = "json", // Ask Ollama to constrain output to valid JSON
            options = new
            {
                num_predict = 800,
                temperature = 0.1, // deterministic extraction, not creative writing
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
            _logger.LogError(ex, "Could not reach Ollama for JD extraction at {Url}", _opts.OllamaBaseUrl);
            throw new InvalidOperationException(
                "Could not reach the local LLM (Ollama) to analyze the job description. " +
                "Make sure Ollama is running and the model is pulled.", ex);
        }

        var result = await response.Content
            .ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
        var rawJson = result?.Response?.Trim();

        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidOperationException(
                "Ollama returned an empty response while extracting job requirements.");

        try
        {
            var parsed = JsonSerializer.Deserialize<ExtractedJson>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is null)
                throw new JsonException("Deserialized to null.");

            return new JobDescriptionAnalysis
            {
                Title = parsed.Title ?? string.Empty,
                RequiredSkills = parsed.RequiredSkills ?? new(),
                PreferredSkills = parsed.PreferredSkills ?? new(),
                YearsOfExperience = parsed.YearsOfExperience,
                Responsibilities = parsed.Responsibilities ?? new(),
                Technologies = parsed.Technologies ?? new(),
                DomainKnowledge = parsed.DomainKnowledge ?? new(),
                Certifications = parsed.Certifications ?? new()
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JD extraction JSON: {Raw}", rawJson);
            throw new InvalidOperationException(
                "The local LLM did not return valid structured JSON for the job " +
                "description. Raw response has been logged.", ex);
        }
    }

    private static string BuildExtractionPrompt(string jobDescriptionText) => $$"""
        You are a job description parser. Extract structured requirements from the
        JOB DESCRIPTION below. Do NOT judge whether any candidate is qualified —
        only extract and structure what the job description states.

        Respond with ONLY a single JSON object, no markdown, no commentary, using
        exactly this shape:

        {
          "title": string,
          "requiredSkills": string[],
          "preferredSkills": string[],
          "yearsOfExperience": number or null,
          "responsibilities": string[],
          "technologies": string[],
          "domainKnowledge": string[],
          "certifications": string[]
        }

        Rules:
        - "requiredSkills" = explicitly mandatory skills.
        - "preferredSkills" = "nice to have" / "preferred" / "bonus" skills.
        - "yearsOfExperience" = the minimum years stated, or null if not stated.
        - Keep entries short (skill/technology names), except "responsibilities".
        - If a category has no items, return an empty array (not null).

        JOB DESCRIPTION:
        {{jobDescriptionText}}
        """;

    private sealed record OllamaResponse(string Response);

    private sealed class ExtractedJson
    {
        public string? Title { get; set; }
        public List<string>? RequiredSkills { get; set; }
        public List<string>? PreferredSkills { get; set; }
        public int? YearsOfExperience { get; set; }
        public List<string>? Responsibilities { get; set; }
        public List<string>? Technologies { get; set; }
        public List<string>? DomainKnowledge { get; set; }
        public List<string>? Certifications { get; set; }
    }
}