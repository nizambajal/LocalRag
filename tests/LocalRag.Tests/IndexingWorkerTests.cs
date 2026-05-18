using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace LocalRag.Tests;

/// <summary>
/// Tests for the indexing pipeline logic.
/// Uses mocked services so no real PDF, ONNX model, or disk I/O is needed.
/// </summary>
public class IndexingWorkerTests : IDisposable
{
    private readonly string _tempDir;

    public IndexingWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rag_worker_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── FileHashTracker ───────────────────────────────────────────────────────

    [Fact]
    public async Task HashTracker_NewFile_NotAlreadyIndexed()
    {
        var path = Path.Combine(_tempDir, "hashes.json");
        var tracker = new FileHashTracker(path, NullLogger<FileHashTracker>.Instance);
        await tracker.LoadAsync();

        var pdf = CreateTempPdf("test.pdf");

        Assert.False(tracker.IsAlreadyIndexed(pdf));
    }

    [Fact]
    public async Task HashTracker_AfterMarkAndSaveAndLoad_IsIndexed()
    {
        var hashPath = Path.Combine(_tempDir, "hashes.json");
        var tracker = new FileHashTracker(hashPath, NullLogger<FileHashTracker>.Instance);
        await tracker.LoadAsync();

        var pdf = CreateTempPdf("doc.pdf");
        tracker.MarkIndexed(pdf);
        await tracker.SaveAsync();

        // Load into a fresh tracker
        var tracker2 = new FileHashTracker(hashPath, NullLogger<FileHashTracker>.Instance);
        await tracker2.LoadAsync();

        Assert.True(tracker2.IsAlreadyIndexed(pdf));
    }

    [Fact]
    public async Task HashTracker_ChangedFile_NotIndexed()
    {
        var hashPath = Path.Combine(_tempDir, "hashes.json");
        var tracker = new FileHashTracker(hashPath, NullLogger<FileHashTracker>.Instance);
        await tracker.LoadAsync();

        var pdf = CreateTempPdf("changing.pdf");
        tracker.MarkIndexed(pdf);

        // Modify file content
        await File.AppendAllTextAsync(pdf, "extra content");

        Assert.False(tracker.IsAlreadyIndexed(pdf));
    }

    [Fact]
    public async Task HashTracker_MissingFile_LoadsEmpty()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var tracker = new FileHashTracker(path, NullLogger<FileHashTracker>.Instance);

        await tracker.LoadAsync(); // should not throw

        var pdf = CreateTempPdf("x.pdf");
        Assert.False(tracker.IsAlreadyIndexed(pdf));
    }

    // ── Pipeline contract tests (mock services) ───────────────────────────────

    [Fact]
    public async Task Pipeline_EmbedIsCalledOncePerBatch()
    {
        var embedder = Substitute.For<IEmbeddingService>();
        embedder.EmbedBatchAsync(
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>())
            .Returns(args =>
            {
                var texts = (IReadOnlyList<string>)args[0];
                IReadOnlyList<float[]> vecs = texts.Select(_ => new float[4]).ToList();
                return Task.FromResult(vecs);
            });

        var vectorStore = Substitute.For<IVectorStore>();

        // Simulate 3 chunks going through the embed → store pipeline
        var chunks = Enumerable.Range(0, 3).Select(i =>
            new DocumentChunk
            {
                Content = $"Chunk {i}",
                Vector = [],
                SourceFile = "test.pdf",
                ChunkIndex = i,
                PageNumber = 1
            }).ToList();

        var texts = chunks.Select(c => c.Content).ToList();
        var vectors = await embedder.EmbedBatchAsync(texts);

        for (int i = 0; i < chunks.Count; i++)
            chunks[i].Vector = vectors[i];

        await vectorStore.AddBatchAsync(chunks);

        await embedder.Received(1)
            .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        await vectorStore.Received(1).AddBatchAsync(Arg.Any<IEnumerable<DocumentChunk>>());
    }

    [Fact]
    public async Task Pipeline_VectorStore_SaveCalledAfterIndexing()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.Count.Returns(3);

        await vectorStore.SaveAsync();

        await vectorStore.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pipeline_ChunksHaveVectorsAfterEmbedding()
    {
        var embedder = Substitute.For<IEmbeddingService>();
        embedder.EmbedBatchAsync(
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>())
            .Returns(args =>
            {
                var texts = (IReadOnlyList<string>)args[0];
                IReadOnlyList<float[]> vecs = texts
                    .Select(_ => new float[] { 1f, 0f, 0f, 0f })
                    .ToList();
                return Task.FromResult(vecs);
            });

        var texts = new List<string> { "First chunk", "Second chunk" };
        var vectors = await embedder.EmbedBatchAsync(texts);

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(4, v.Length));
        Assert.All(vectors, v => Assert.Equal(1f, v[0]));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateTempPdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, $"%PDF-1.4 fake content for {name}");
        return path;
    }
}