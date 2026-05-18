using LocalRag.Application.Contracts;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query + Response ──────────────────────────────────────────────────────────

public record HybridSearchQuery(
    string QueryText,
    int TopK = 5,
    float VectorWeight = 0.7f,
    float Bm25Weight = 0.3f
) : IRequest<HybridSearchResponse>;

public record HybridSearchResponse(
    IReadOnlyList<HybridSearchResultDto> Results,
    TimeSpan Elapsed);

public record HybridSearchResultDto(
    Guid ChunkId,
    string Content,
    string SourceFile,
    int PageNumber,
    int ChunkIndex,
    float VectorScore,
    float Bm25Score,
    float CombinedScore,
    int Rank);

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class HybridSearchQueryHandler
    : IRequestHandler<HybridSearchQuery, HybridSearchResponse>
{
    private readonly IHybridSearchService _hybrid;

    public HybridSearchQueryHandler(IHybridSearchService hybrid)
        => _hybrid = hybrid;

    public async Task<HybridSearchResponse> Handle(
        HybridSearchQuery request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var results = await _hybrid.SearchAsync(
            request.QueryText,
            request.TopK,
            request.VectorWeight,
            request.Bm25Weight,
            ct);

        sw.Stop();

        var dtos = results
            .Select(r => new HybridSearchResultDto(
                r.Chunk.Id,
                r.Chunk.Content,
                r.Chunk.SourceFile,
                r.Chunk.PageNumber,
                r.Chunk.ChunkIndex,
                r.VectorScore,
                r.Bm25Score,
                r.CombinedScore,
                r.Rank))
            .ToList();

        return new HybridSearchResponse(dtos, sw.Elapsed);
    }
}