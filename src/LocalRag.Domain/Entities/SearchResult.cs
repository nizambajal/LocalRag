namespace LocalRag.Domain.Entities;

/// <summary>
/// A retrieved chunk paired with its cosine-similarity score.
/// Returned by the vector store after a nearest-neighbour query.
/// </summary>
public class SearchResult
{
    public required DocumentChunk Chunk { get; init; }

    /// <summary>Cosine similarity in [0, 1]. Higher = more relevant.</summary>
    public float Score { get; init; }

    /// <summary>Rank among results (1 = most relevant).</summary>
    public int Rank { get; init; }
}
