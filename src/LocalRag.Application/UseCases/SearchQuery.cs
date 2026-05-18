using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using MediatR;

namespace LocalRag.Application.UseCases;

// ─────────────────────────────────────────────────────────────────────────────
// Query + Response DTOs
// ─────────────────────────────────────────────────────────────────────────────

public record SearchQuery(string QueryText, int TopK = 5) : IRequest<SearchResponse>;

public record SearchResponse(
    IReadOnlyList<SearchResultDto> Results,
    TimeSpan Elapsed);

public record SearchResultDto(
    Guid ChunkId,
    string Content,
    string SourceFile,
    int PageNumber,
    int ChunkIndex,
    float Score,
    int Rank);

// ─────────────────────────────────────────────────────────────────────────────
// Handler
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SearchQueryHandler : IRequestHandler<SearchQuery, SearchResponse>
{
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;

    public SearchQueryHandler(IEmbeddingService embeddings, IVectorStore vectorStore)
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
    }

    public async Task<SearchResponse> Handle(SearchQuery request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Embed the user's query using the same model as ingestion
        var queryVector = await _embeddings.EmbedAsync(request.QueryText, ct);

        // 2. Nearest-neighbour search in FAISS
        var results = await _vectorStore.SearchAsync(queryVector, request.TopK, ct);

        sw.Stop();

        // 3. Map to response DTOs
        var dtos = results.Select(r => new SearchResultDto(
            r.Chunk.Id,
            r.Chunk.Content,
            r.Chunk.SourceFile,
            r.Chunk.PageNumber,
            r.Chunk.ChunkIndex,
            r.Score,
            r.Rank)).ToList();

        return new SearchResponse(dtos, sw.Elapsed);
    }
}
