using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text;

namespace LocalRag.Infrastructure.Services;

public sealed class LocalChatService : IChatService
{
    private readonly RagOptions _opts;
    private readonly ILogger<LocalChatService> _logger;
    private readonly HttpClient _http;

    // HttpClient is injected directly — created by the factory in DI registration
    public LocalChatService(
        IOptions<RagOptions> opts,
        ILogger<LocalChatService> logger,
        HttpClient http)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = http;
    }

    public async Task<string> AnswerAsync(
        string question,
        IReadOnlyList<DocumentChunk> contextChunks,
        CancellationToken ct = default)
    {
        if (!_opts.OllamaMode)
            return BuildNoLlmAnswer(question, contextChunks);

        string prompt = BuildPrompt(question, contextChunks);
        return await CallOllamaAsync(prompt, ct);
    }

    private string BuildPrompt(string question, IReadOnlyList<DocumentChunk> chunks)
    {
        var mode = DetectQuestionMode(question);

        return mode switch
        {
            PromptMode.Factual => BuildFactualPrompt(question, chunks),
            PromptMode.Summarisation => BuildSummarisationPrompt(question, chunks),
            _ => BuildSummarisationPrompt(question, chunks)
        };
    }

    // ── Question mode detection ───────────────────────────────────────────────────
    private enum PromptMode { Factual, Summarisation }

    private static PromptMode DetectQuestionMode(string question)
    {
        var q = question.ToLowerInvariant();

        // Factual = short specific lookups
        var factualKeywords = new[]
        {
            "what is", "who is", "what's", "when", "where",
            "email", "phone", "contact", "name", "date", "address"
        };

        // Summarisation = open-ended career/skill questions
        var summaryKeywords = new[]
        {
            "responsibilit", "experience", "skill", "achiev",
            "contribution", "work", "career", "project", "role",
            "what did", "what have", "describe", "tell me", "explain",
            "summary", "background", "qualif", "capabilit"
        };

        foreach (var kw in summaryKeywords)
        {
            if (q.Contains(kw))
                return PromptMode.Summarisation;
        }

        foreach (var kw in factualKeywords)
        {
            if (q.Contains(kw))
                return PromptMode.Factual;
        }

        // Default to summarisation for open-ended questions
        return PromptMode.Summarisation;
    }

    // ── Factual prompt (strict, short) ────────────────────────────────────────────

    private string BuildFactualPrompt(
        string question, IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a precise document assistant.");
        sb.AppendLine("RULES:");
        sb.AppendLine("- Answer using ONLY the information from the CONTEXT below.");
        sb.AppendLine("- Copy names, emails, and technical terms EXACTLY as written.");
        sb.AppendLine("- Be direct and concise — one or two sentences maximum.");
        sb.AppendLine("- If the answer is not in the context, say: I don't know.");
        sb.AppendLine();
        sb.AppendLine("CONTEXT:");

        for (int i = 0; i < chunks.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] (Page {chunks[i].PageNumber})");
            sb.AppendLine(chunks[i].Content);
            sb.AppendLine();
        }

        sb.AppendLine($"QUESTION: {question}");
        sb.AppendLine("ANSWER:");

        return sb.ToString();
    }

    // ── Summarisation prompt (structured, detailed) ───────────────────────────────

    private string BuildSummarisationPrompt(
        string question, IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a professional career assistant helping analyse a resume.");
        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("- Read all the CONTEXT sections carefully.");
        sb.AppendLine("- Answer the QUESTION in a well-structured, professional manner.");
        sb.AppendLine("- Use bullet points or numbered lists where appropriate.");
        sb.AppendLine("- Group related responsibilities or skills together.");
        sb.AppendLine("- Write in third person (e.g. 'He led...', 'The candidate...').");
        sb.AppendLine("- Be comprehensive — include all relevant details from the context.");
        sb.AppendLine("- Do NOT make up information not present in the context.");
        sb.AppendLine("- Format your response clearly with headings if there are multiple categories.");
        sb.AppendLine();
        sb.AppendLine("CONTEXT (resume content):");

        for (int i = 0; i < chunks.Count; i++)
        {
            sb.AppendLine($"--- Section {i + 1} (Page {chunks[i].PageNumber}) ---");
            sb.AppendLine(chunks[i].Content);
            sb.AppendLine();
        }

        sb.AppendLine($"QUESTION: {question}");
        sb.AppendLine();
        sb.AppendLine("ANSWER (structured, professional, comprehensive):");

        return sb.ToString();
    }

    // HACK: Previously working.
    //private async Task<string> CallOllamaAsync(string prompt, CancellationToken ct)
    //{
    //    var request = new
    //    {
    //        model = _opts.OllamaModel,
    //        prompt = prompt,
    //        stream = false
    //    };

    //    _logger.LogDebug("Calling Ollama model: {Model}", _opts.OllamaModel);

    //    try
    //    {
    //        var response = await _http.PostAsJsonAsync(
    //            $"{_opts.OllamaBaseUrl}/api/generate", request, ct);

    //        response.EnsureSuccessStatusCode();

    //        var result = await response.Content
    //            .ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);

    //        return result?.Response?.Trim() ?? "No response from model.";
    //    }
    //    catch (HttpRequestException ex)
    //    {
    //        _logger.LogWarning(ex, "Could not reach Ollama at {Url}", _opts.OllamaBaseUrl);
    //        return "⚠️ Could not reach the local LLM. " +
    //               "Make sure Ollama is running: https://ollama.com";
    //    }
    //}

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken ct)
    {
        var request = new
        {
            model = _opts.OllamaModel,
            prompt = prompt,
            stream = false,
            options = new
            {
                num_predict = 500,    // ← limit max tokens so it doesn't run forever
                temperature = 0.3,    // ← lower = faster + more focused answers
                top_p = 0.9
            }
        };

        _logger.LogInformation(
            "Calling Ollama: {Url} model={Model}",
            $"{_opts.OllamaBaseUrl}/api/generate",
            _opts.OllamaModel);

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"{_opts.OllamaBaseUrl}/api/generate", request, ct);

            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);

            return result?.Response?.Trim() ?? "No response from model.";
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("Ollama timed out");
            return "⚠️ The model took too long to respond. " +
                   "Try a smaller model like llama3.2:3b or fix GPU drivers.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Cannot reach Ollama at {Url}", _opts.OllamaBaseUrl);
            return $"⚠️ Could not reach Ollama. Error: {ex.Message}";
        }
    }

    private static string BuildNoLlmAnswer(
        string question, IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Question:** {question}");
        sb.AppendLine();
        sb.AppendLine($"**Top {chunks.Count} retrieved chunks** (no LLM configured):");
        sb.AppendLine();

        for (int i = 0; i < chunks.Count; i++)
        {
            sb.AppendLine($"**[{i + 1}]** {chunks[i].SourceFile} — page {chunks[i].PageNumber}");
            sb.AppendLine(chunks[i].Content);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("*To enable AI answers: set `Rag:OllamaMode=true` in appsettings.json*");
        sb.AppendLine("*and install Ollama: https://ollama.com then run: `ollama pull mistral`*");
        return sb.ToString();
    }

    private record OllamaResponse(string Response);
}