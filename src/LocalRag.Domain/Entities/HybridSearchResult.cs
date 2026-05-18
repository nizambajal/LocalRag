namespace LocalRag.Domain.Entities;

/// <summary>
/// A chunk scored by both BM25 (keyword) and cosine (vector) search,
/// fused via Reciprocal Rank Fusion into a single combined score.
/// </summary>
public sealed record HybridSearchResult
{
    public required DocumentChunk Chunk { get; init; }
    public float VectorScore { get; init; }
    public float Bm25Score { get; init; }
    public float CombinedScore { get; init; }
    public int Rank { get; init; }
}