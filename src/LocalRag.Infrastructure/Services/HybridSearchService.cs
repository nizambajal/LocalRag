using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Fuses BM25 keyword results and cosine vector results using
/// Reciprocal Rank Fusion (RRF).
///
/// RRF formula per chunk:  score = Σ 1 / (k + rank_i)
///   where k = 60 (standard constant) and rank_i is the rank
///   of the chunk in each result list (1-based).
///
/// Why RRF?
///   - BM25 and vector scores are on different scales — can't add directly.
///   - RRF is rank-based so it's immune to score magnitude differences.
///   - Simple, parameter-light, consistently outperforms weighted sum fusion.
/// </summary>
public sealed class HybridSearchService : IHybridSearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IBm25Index _bm25Index;
    private readonly IEmbeddingService _embedder;
    private readonly ILogger<HybridSearchService> _logger;

    // RRF constant — 60 is the standard value from the original paper
    private const int RrfK = 60;

    public HybridSearchService(
        IVectorStore vectorStore,
        IBm25Index bm25Index,
        IEmbeddingService embedder,
        ILogger<HybridSearchService> logger)
    {
        _vectorStore = vectorStore;
        _bm25Index = bm25Index;
        _embedder = embedder;
        _logger = logger;
    }

    /// <summary>
    /// Run both searches in parallel, fuse via RRF, return topK results.
    /// </summary>
    public async Task<IReadOnlyList<HybridSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        float vectorWeight = 0.7f,
        float bm25Weight = 0.3f,
        CancellationToken ct = default)
    {
        // Fetch more candidates than topK from each source
        // so RRF has enough material to re-rank from
        int fetchK = Math.Max(topK * 3, 20);

        // Run both searches in parallel
        var vectorTask = SearchVectorAsync(query, fetchK, ct);
        var bm25Task = _bm25Index.SearchAsync(query, fetchK, ct);

        await Task.WhenAll(vectorTask, bm25Task);

        var vectorResults = vectorTask.Result;
        var bm25Results = bm25Task.Result;

        _logger.LogDebug(
            "Hybrid search: {V} vector + {B} BM25 candidates for query '{Q}'",
            vectorResults.Count, bm25Results.Count, query);

        // Build rank maps: chunkId → rank (1-based)
        var vectorRanks = vectorResults
            .Select((r, i) => (r.Chunk.Id, Rank: i + 1))
            .ToDictionary(x => x.Id, x => x.Rank);

        var bm25Ranks = bm25Results
            .Select((r, i) => (r.Chunk.Id, Rank: i + 1))
            .ToDictionary(x => x.Id, x => x.Rank);

        // Build score lookup maps for reporting
        var vectorScores = vectorResults.ToDictionary(r => r.Chunk.Id, r => r.Score);
        var bm25Scores = bm25Results.ToDictionary(r => r.Chunk.Id, r => r.Score);

        // Union of all candidate chunk IDs
        var allIds = vectorRanks.Keys
            .Union(bm25Ranks.Keys)
            .ToHashSet();

        // Build chunk lookup from both result sets
        var chunkLookup = vectorResults
            .Concat(bm25Results)
            .GroupBy(r => r.Chunk.Id)
            .ToDictionary(g => g.Key, g => g.First().Chunk);

        // Compute RRF score for each candidate
        var scored = allIds
            .Select(id =>
            {
                float rrfVector = vectorRanks.TryGetValue(id, out int vr)
                    ? vectorWeight * (1f / (RrfK + vr))
                    : 0f;

                float rrfBm25 = bm25Ranks.TryGetValue(id, out int br)
                    ? bm25Weight * (1f / (RrfK + br))
                    : 0f;

                return new HybridSearchResult
                {
                    Chunk = chunkLookup[id],
                    VectorScore = vectorScores.GetValueOrDefault(id, 0f),
                    Bm25Score = bm25Scores.GetValueOrDefault(id, 0f),
                    CombinedScore = rrfVector + rrfBm25,
                };
            })
            .OrderByDescending(x => x.CombinedScore)
            .Take(topK)
            .Select((x, i) => x with { Rank = i + 1 })
            .ToList();

        return scored;
    }

    private async Task<IReadOnlyList<SearchResult>> SearchVectorAsync(
        string query, int topK, CancellationToken ct)
    {
        var vector = await _embedder.EmbedAsync(query, ct);
        return await _vectorStore.SearchAsync(vector, topK, ct);
    }
}