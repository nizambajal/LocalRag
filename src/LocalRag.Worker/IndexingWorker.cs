using LocalRag.Application.Contracts;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Worker;

public sealed class IndexingWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly RagOptions _opts;
    private readonly ILogger<IndexingWorker> _logger;

    public IndexingWorker(
        IServiceProvider services,
        IOptions<RagOptions> opts,
        ILogger<IndexingWorker> logger)
    {
        _services = services;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "IndexingWorker started. Watching: {Folder} every {Interval}s",
            _opts.PdfFolder, _opts.WorkerIntervalSeconds);

        Directory.CreateDirectory(_opts.PdfFolder);

        while (!ct.IsCancellationRequested)
        {
            try { await RunIndexingPassAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in indexing pass — will retry");
            }

            await Task.Delay(TimeSpan.FromSeconds(_opts.WorkerIntervalSeconds), ct);
        }
    }

    private async Task RunIndexingPassAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var pdfReader = scope.ServiceProvider.GetRequiredService<IPdfReader>();
        var chunker = scope.ServiceProvider.GetRequiredService<IChunkingService>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var bm25Index = scope.ServiceProvider.GetRequiredService<IBm25Index>();
        var tracker = scope.ServiceProvider.GetRequiredService<IIndexingTracker>();

        var hashTrackerPath = Path.Combine(
            Path.GetDirectoryName(_opts.FaissMetadataPath)!, "hashes.json");

        var hashTracker = new FileHashTracker(
            hashTrackerPath,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileHashTracker>.Instance);

        await hashTracker.LoadAsync(ct);

        var pdfFiles = Directory
            .GetFiles(_opts.PdfFolder, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (pdfFiles.Count == 0) return;

        bool anyIndexed = false;

        foreach (var filePath in pdfFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (hashTracker.IsAlreadyIndexed(filePath))
            {
                _logger.LogDebug("Skipping unchanged: {File}", Path.GetFileName(filePath));
                continue;
            }

            var job = tracker.Start(Path.GetFileName(filePath));
            _logger.LogInformation("Indexing: {File}", Path.GetFileName(filePath));

            try
            {
                int count = await IndexFileAsync(
                    filePath, pdfReader, chunker, embedder, vectorStore, bm25Index, ct);

                tracker.Complete(job.Id, count);
                hashTracker.MarkIndexed(filePath);
                anyIndexed = true;

                _logger.LogInformation(
                    "Indexed {File}: {N} chunks", Path.GetFileName(filePath), count);
            }
            catch (Exception ex)
            {
                tracker.Fail(job.Id, ex.Message);
                _logger.LogError(ex, "Failed: {File}", Path.GetFileName(filePath));
            }
        }

        if (anyIndexed)
        {
            await vectorStore.SaveAsync(ct);
            await bm25Index.SaveAsync(ct);
            await hashTracker.SaveAsync(ct);
            _logger.LogInformation(
                "Saved. Vectors: {V}", vectorStore.Count);
        }
    }

    private async Task<int> IndexFileAsync(
        string filePath,
        IPdfReader pdfReader,
        IChunkingService chunker,
        IEmbeddingService embedder,
        IVectorStore vectorStore,
        IBm25Index bm25Index,
        CancellationToken ct)
    {
        var pages = pdfReader.ExtractPages(filePath).ToList();
        if (pages.Count == 0) return 0;

        var chunks = chunker.Chunk(pages, filePath).ToList();
        if (chunks.Count == 0) return 0;

        // Index into BM25 immediately (no embeddings needed)
        await bm25Index.AddBatchAsync(chunks, ct);

        // Embed and index into vector store in batches
        var texts = chunks.Select(c => c.Content).ToList();
        int totalBatches = (int)Math.Ceiling(texts.Count / (double)_opts.BatchSize);

        for (int b = 0; b < totalBatches; b++)
        {
            ct.ThrowIfCancellationRequested();

            var batchTexts = texts.Skip(b * _opts.BatchSize).Take(_opts.BatchSize).ToList();
            var batchChunks = chunks.Skip(b * _opts.BatchSize).Take(_opts.BatchSize).ToList();
            var vectors = await embedder.EmbedBatchAsync(batchTexts, ct);

            for (int i = 0; i < batchChunks.Count; i++)
                batchChunks[i].Vector = vectors[i];

            await vectorStore.AddBatchAsync(batchChunks, ct);
        }

        return chunks.Count;
    }
}