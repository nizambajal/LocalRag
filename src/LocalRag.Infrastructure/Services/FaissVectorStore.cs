using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// FAISS-backed vector store using native interop (IndexFlatIP).
///
/// FAISS native setup:
///   Windows: install via conda  →  conda install -c conda-forge faiss-cpu
///   Linux:   apt install libfaiss-dev   OR   conda install faiss-cpu
///   The native faiss.dll / libfaiss.so must be on PATH or next to the executable.
///
/// IndexFlatIP = exact inner-product search.
/// Because OnnxEmbeddingService L2-normalises all vectors,
/// inner product == cosine similarity.
///
/// Metadata (Guid → DocumentChunk) is stored in a JSON sidecar
/// because FAISS only tracks int64 IDs internally.
/// </summary>
public sealed class FaissVectorStore : IVectorStore
{
    private readonly RagOptions _opts;
    private readonly ILogger<FaissVectorStore> _logger;

    // FAISS index pointer (unmanaged)
    private nint _index = nint.Zero;

    // Maps FAISS sequential int64 ID → DocumentChunk
    private readonly Dictionary<long, DocumentChunk> _metadata = [];
    private readonly ReaderWriterLockSlim _lock = new();
    private long _nextId = 0;

    public FaissVectorStore(IOptions<RagOptions> opts, ILogger<FaissVectorStore> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public long Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _metadata.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    // ── IVectorStore ──────────────────────────────────────────────────────────

    public Task AddAsync(DocumentChunk chunk, CancellationToken ct = default)
        => AddBatchAsync([chunk], ct);

    public Task AddBatchAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        var list = chunks.ToList();
        if (list.Count == 0) return Task.CompletedTask;

        EnsureIndex();

        int d = _opts.EmbeddingDimensions;
        int n = list.Count;
        var vectors = new float[n * d];
        var ids = new long[n];

        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < n; i++)
            {
                ids[i] = _nextId;
                _metadata[_nextId] = list[i];
                _nextId++;

                float[] v = list[i].Vector;
                Array.Copy(v, 0, vectors, i * d, Math.Min(v.Length, d));
            }

            // Add to FAISS index
            Faiss.IndexAdd(_index, n, vectors, ids);
        }
        finally { _lock.ExitWriteLock(); }

        _logger.LogDebug("FAISS: added {N} vectors. Total: {Total}", n, _nextId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK = 5,
        CancellationToken ct = default)
    {
        EnsureIndex();

        var distances = new float[topK];
        var labels = new long[topK];

        _lock.EnterReadLock();
        try
        {
            Faiss.IndexSearch(_index, 1, queryVector, topK, distances, labels);
        }
        finally { _lock.ExitReadLock(); }

        var results = new List<SearchResult>();
        for (int i = 0; i < topK; i++)
        {
            long id = labels[i];
            if (id < 0) continue; // FAISS returns -1 for unfilled slots

            if (_metadata.TryGetValue(id, out var chunk))
            {
                results.Add(new SearchResult
                {
                    Chunk = chunk,
                    Score = distances[i],
                    Rank = i + 1
                });
            }
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        // Save FAISS binary index
        string indexPath = _opts.FaissIndexPath;
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        _lock.EnterReadLock();
        try { Faiss.WriteIndex(_index, indexPath); }
        finally { _lock.ExitReadLock(); }

        // Save metadata sidecar
        string metaPath = _opts.FaissMetadataPath;
        await using var fs = File.Create(metaPath);
        await JsonSerializer.SerializeAsync(fs, _metadata, cancellationToken: ct);

        _logger.LogInformation(
            "FAISS index saved: {Count} vectors → {Path}", Count, indexPath);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        string indexPath = _opts.FaissIndexPath;
        string metaPath = _opts.FaissMetadataPath;

        if (!File.Exists(indexPath) || !File.Exists(metaPath))
        {
            _logger.LogInformation("No existing FAISS index found — starting empty");
            EnsureIndex(); // create a fresh empty index
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            _index = Faiss.ReadIndex(indexPath);

            await using var fs = File.OpenRead(metaPath);
            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<long, DocumentChunk>>(
                fs, cancellationToken: ct) ?? [];

            _metadata.Clear();
            foreach (var kv in loaded) _metadata[kv.Key] = kv.Value;
            _nextId = _metadata.Count > 0 ? _metadata.Keys.Max() + 1 : 0;
        }
        finally { _lock.ExitWriteLock(); }

        _logger.LogInformation(
            "FAISS index loaded: {Count} vectors from {Path}", Count, indexPath);
    }

    public Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try { return Task.FromResult<IReadOnlyList<DocumentChunk>>(_metadata.Values.ToList()); }
        finally { _lock.ExitReadLock(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureIndex()
    {
        if (_index != nint.Zero) return;

        _lock.EnterWriteLock();
        try
        {
            if (_index != nint.Zero) return;
            _index = Faiss.IndexFlatIPCreate(_opts.EmbeddingDimensions);
            _logger.LogInformation(
                "FAISS IndexFlatIP created. Dimensions: {D}", _opts.EmbeddingDimensions);
        }
        finally { _lock.ExitWriteLock(); }
    }
}

// ── Thin P/Invoke wrapper for libfaiss ────────────────────────────────────────

/// <summary>
/// Minimal P/Invoke bindings for the FAISS C API.
/// Targets faiss.dll (Windows) / libfaiss.so (Linux) / libfaiss.dylib (macOS).
/// </summary>
internal static class Faiss
{
    private const string LibName = "faiss";

    [DllImport(LibName, EntryPoint = "faiss_index_flat_ip_new")]
    public static extern nint IndexFlatIPCreate(int d);

    [DllImport(LibName, EntryPoint = "faiss_index_add_with_ids")]
    public static extern void IndexAdd(
        nint index, int n,
        [In] float[] vectors,
        [In] long[] ids);

    [DllImport(LibName, EntryPoint = "faiss_index_search")]
    public static extern void IndexSearch(
        nint index, int n,
        [In] float[] queryVectors,
        int k,
        [Out] float[] distances,
        [Out] long[] labels);

    [DllImport(LibName, EntryPoint = "faiss_write_index_fname")]
    public static extern void WriteIndex(nint index, string fileName);

    [DllImport(LibName, EntryPoint = "faiss_read_index_fname")]
    public static extern nint ReadIndex(string fileName);
}