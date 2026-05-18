using System.Text.Json;
using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Pure C# vector store using cosine similarity over an in-memory list.
/// No native dependencies — works on any platform.
///
/// Suitable for up to ~50,000 chunks. For larger datasets swap this
/// for FaissVectorStore (Phase 4b) which uses native FAISS.
///
/// Persistence: serialises the entire store to a JSON file on disk.
/// On startup the API calls LoadAsync() to restore state.
/// </summary>
public sealed class FlatVectorStore : IVectorStore
{
    private readonly RagOptions _opts;
    private readonly ILogger<FlatVectorStore> _logger;

    // Thread-safe collections
    private readonly List<DocumentChunk> _chunks = [];
    private readonly ReaderWriterLockSlim _lock = new();

    public FlatVectorStore(IOptions<RagOptions> opts, ILogger<FlatVectorStore> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    // ── IVectorStore ──────────────────────────────────────────────────────────

    public long Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _chunks.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public Task AddAsync(DocumentChunk chunk, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try { _chunks.Add(chunk); }
        finally { _lock.ExitWriteLock(); }
        return Task.CompletedTask;
    }

    public Task AddBatchAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        var list = chunks.ToList();
        _lock.EnterWriteLock();
        try { _chunks.AddRange(list); }
        finally { _lock.ExitWriteLock(); }

        _logger.LogDebug("Added {Count} chunks. Total: {Total}", list.Count, _chunks.Count);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK = 5,
        CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        List<DocumentChunk> snapshot;
        try { snapshot = [.. _chunks]; }
        finally { _lock.ExitReadLock(); }

        if (snapshot.Count == 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        // Score every chunk — O(n * d) where d = embedding dimensions
        var scored = snapshot
            .Select(c => (Chunk: c, Score: CosineSimilarity(queryVector, c.Vector)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select((x, rank) => new SearchResult
            {
                Chunk = x.Chunk,
                Score = x.Score,
                Rank = rank + 1
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchResult>>(scored);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        List<DocumentChunk> snapshot;
        try { snapshot = [.. _chunks]; }
        finally { _lock.ExitReadLock(); }

        string path = _opts.FaissMetadataPath; // reuse same config key
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var options = new JsonSerializerOptions { WriteIndented = false };
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, snapshot, options, ct);

        _logger.LogInformation(
            "Vector store saved: {Count} chunks → {Path}", snapshot.Count, path);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        string path = _opts.FaissMetadataPath;

        if (!File.Exists(path))
        {
            _logger.LogInformation("No existing vector store found at {Path} — starting empty", path);
            return;
        }

        await using var fs = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync<List<DocumentChunk>>(fs, cancellationToken: ct)
                     ?? [];

        _lock.EnterWriteLock();
        try
        {
            _chunks.Clear();
            _chunks.AddRange(loaded);
        }
        finally { _lock.ExitWriteLock(); }

        _logger.LogInformation(
            "Vector store loaded: {Count} chunks from {Path}", loaded.Count, path);
    }

    // ── Math ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cosine similarity between two vectors.
    /// Because all vectors are L2-normalised on insert (by OnnxEmbeddingService),
    /// this reduces to a simple dot product — fast and numerically stable.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0f;

        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];

        return dot; // both are unit vectors so ||a||*||b|| = 1
    }
}