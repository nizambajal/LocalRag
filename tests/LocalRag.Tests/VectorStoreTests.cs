using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Tests;

public class VectorStoreTests : IDisposable
{
    private readonly string _tempFile;
    private readonly FlatVectorStore _store;

    public VectorStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid()}.json");

        var opts = Options.Create(new RagOptions
        {
            EmbeddingDimensions = 4,
            FaissMetadataPath = _tempFile
        });

        _store = new FlatVectorStore(opts, NullLogger<FlatVectorStore>.Instance);
    }

    public void Dispose() => File.Delete(_tempFile);

    // ── Add & Count ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_IncreasesCount()
    {
        await _store.AddAsync(MakeChunk([1f, 0f, 0f, 0f]));
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public async Task AddBatchAsync_AddsAllChunks()
    {
        var chunks = Enumerable.Range(0, 5)
            .Select(i => MakeChunk([i, 0f, 0f, 0f]))
            .ToList();

        await _store.AddBatchAsync(chunks);
        Assert.Equal(5, _store.Count);
    }

    [Fact]
    public async Task Count_StartsAtZero()
    {
        Assert.Equal(0, _store.Count);
        await Task.CompletedTask;
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_ReturnsTopKResults()
    {
        await _store.AddBatchAsync([
            MakeChunk([1f, 0f, 0f, 0f], "doc1"),
            MakeChunk([0f, 1f, 0f, 0f], "doc2"),
            MakeChunk([0f, 0f, 1f, 0f], "doc3"),
        ]);

        var results = await _store.SearchAsync([1f, 0f, 0f, 0f], topK: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_MostSimilarChunkRanksFirst()
    {
        await _store.AddBatchAsync([
            MakeChunk(Normalise([1f, 0f, 0f, 0f]), "best-match"),
            MakeChunk(Normalise([0f, 1f, 0f, 0f]), "poor-match"),
            MakeChunk(Normalise([0f, 0f, 1f, 0f]), "poor-match-2"),
        ]);

        var query = Normalise([1f, 0.1f, 0f, 0f]);
        var results = await _store.SearchAsync(query, topK: 3);

        Assert.Equal("best-match", results[0].Chunk.SourceFile);
    }

    [Fact]
    public async Task SearchAsync_ScoresAreDescending()
    {
        await _store.AddBatchAsync([
            MakeChunk(Normalise([1f, 0f, 0f, 0f])),
            MakeChunk(Normalise([0.5f, 0.5f, 0f, 0f])),
            MakeChunk(Normalise([0f, 0f, 1f, 0f])),
        ]);

        var results = await _store.SearchAsync(Normalise([1f, 0f, 0f, 0f]), topK: 3);

        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score,
                "Results should be ordered highest score first");
    }

    [Fact]
    public async Task SearchAsync_EmptyStore_ReturnsEmpty()
    {
        var results = await _store.SearchAsync([1f, 0f, 0f, 0f], topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_RankStartsAtOne()
    {
        await _store.AddAsync(MakeChunk(Normalise([1f, 0f, 0f, 0f])));
        var results = await _store.SearchAsync(Normalise([1f, 0f, 0f, 0f]), topK: 1);
        Assert.Equal(1, results[0].Rank);
    }

    [Fact]
    public async Task SearchAsync_TopKGreaterThanCount_ReturnsAll()
    {
        await _store.AddBatchAsync([
            MakeChunk(Normalise([1f, 0f, 0f, 0f])),
            MakeChunk(Normalise([0f, 1f, 0f, 0f])),
        ]);

        var results = await _store.SearchAsync(Normalise([1f, 0f, 0f, 0f]), topK: 10);

        Assert.Equal(2, results.Count);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_RestoresChunks()
    {
        await _store.AddBatchAsync([
            MakeChunk(Normalise([1f, 0f, 0f, 0f]), "file-a.pdf"),
            MakeChunk(Normalise([0f, 1f, 0f, 0f]), "file-b.pdf"),
        ]);

        await _store.SaveAsync();

        // Load into a fresh store instance pointing at the same file
        var opts2 = Options.Create(new RagOptions
        {
            EmbeddingDimensions = 4,
            FaissMetadataPath = _tempFile
        });
        var store2 = new FlatVectorStore(opts2, NullLogger<FlatVectorStore>.Instance);
        await store2.LoadAsync();

        Assert.Equal(2, store2.Count);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_StartsEmpty()
    {
        // Point at a path that doesn't exist
        var opts = Options.Create(new RagOptions
        {
            EmbeddingDimensions = 4,
            FaissMetadataPath = Path.Combine(Path.GetTempPath(), "nonexistent.json")
        });
        var store = new FlatVectorStore(opts, NullLogger<FlatVectorStore>.Instance);

        await store.LoadAsync(); // should not throw

        Assert.Equal(0, store.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DocumentChunk MakeChunk(float[] vector, string source = "test.pdf") =>
        new()
        {
            Content = "Test content",
            Vector = vector,
            SourceFile = source,
            ChunkIndex = 0,
            PageNumber = 1
        };

    private static float[] Normalise(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm < 1e-10f) return v;
        return v.Select(x => x / norm).ToArray();
    }
}