using LocalRag.Application.Contracts;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LocalRag.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RagOptions>(
            configuration.GetSection(RagOptions.SectionName));

        // ── PDF Reader ────────────────────────────────────────────────────────
        services.AddSingleton<IPdfReader, PdfPigReader>();

        // ── Chunking ──────────────────────────────────────────────────────────
        services.AddSingleton<IChunkingService, ChunkingService>();

        // ── Embedding ─────────────────────────────────────────────────────────
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();

        // ── Vector Store (swap comment to use FAISS) ──────────────────────────
        services.AddSingleton<IVectorStore, FlatVectorStore>();
        // services.AddSingleton<IVectorStore, FaissVectorStore>();

        // ── BM25 Keyword Index ────────────────────────────────────────────────
        services.AddSingleton<IBm25Index, LuceneBm25Index>();

        // ── Hybrid Search (fuses vector + BM25 via RRF) ───────────────────────
        services.AddSingleton<HybridSearchService>();

        // ── Chat (RAG answer generation) ──────────────────────────────────────
        // Register a named HttpClient for Ollama then resolve it manually
        // so IOptions<RagOptions> and ILogger are also injected correctly.
        services.AddHttpClient("ollama", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5); // LLM inference can be slow
        });

        services.AddSingleton<IChatService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("ollama");
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                            .IOptions<RagOptions>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<LocalChatService>>();
            return new LocalChatService(opts, logger, http);
        });

        // ── Job Description Extraction (structured extraction via Ollama) ──────
        services.AddSingleton<IJobDescriptionExtractor>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("ollama");
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                            .IOptions<RagOptions>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<OllamaJobDescriptionExtractor>>();
            return new OllamaJobDescriptionExtractor(opts, logger, http);
        });

        // ── Skill Classification (evidence-grounded, via Ollama) ───────────────
        services.AddSingleton<ISkillClassifier>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("ollama");
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                            .IOptions<RagOptions>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<OllamaSkillClassifier>>();
            return new OllamaSkillClassifier(opts, logger, http);
        });

        // ── Interview Question Generation (grounded via Ollama) ────────────────
        services.AddSingleton<IInterviewQuestionGenerator>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("ollama");
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                            .IOptions<RagOptions>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<OllamaInterviewQuestionGenerator>>();
            return new OllamaInterviewQuestionGenerator(opts, logger, http);
        });

        // ── CV Tailoring (grounded via Ollama) ─────────────────────────────────
        services.AddSingleton<ICvTailoringGenerator>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("ollama");
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                            .IOptions<RagOptions>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<OllamaCvTailoringGenerator>>();
            return new OllamaCvTailoringGenerator(opts, logger, http);
        });

        // ── Indexing Tracker ──────────────────────────────────────────────────
        services.AddSingleton<IIndexingTracker, InMemoryIndexingTracker>();
        services.AddSingleton<IHybridSearchService, HybridSearchService>();

        return services;
    }
}