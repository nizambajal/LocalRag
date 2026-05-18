using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Index.Extensions;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Lucene.NET BM25 keyword index.
///
/// Lucene uses BM25 scoring by default (since 4.x).
/// Each DocumentChunk is stored as a Lucene Document with:
///   - "id"      → chunk GUID (stored, not analysed)
///   - "content" → chunk text (analysed with StandardAnalyzer)
///   - "source"  → source file name
///   - "page"    → page number
///   - "chunk"   → chunk index
///
/// The full DocumentChunk is NOT stored in Lucene — we map back via
/// an in-memory dictionary (id → DocumentChunk) that stays in sync.
/// </summary>
public sealed class LuceneBm25Index : IBm25Index, IDisposable
{
    private readonly RagOptions _opts;
    private readonly ILogger<LuceneBm25Index> _logger;

    private FSDirectory? _directory;
    private StandardAnalyzer? _analyzer;
    private IndexWriter? _writer;
    private DirectoryReader? _reader;
    private IndexSearcher? _searcher;

    // id → chunk, for result hydration after Lucene search
    private readonly Dictionary<string, DocumentChunk> _chunkMap = [];
    private readonly ReaderWriterLockSlim _lock = new();

    private const LuceneVersion LV = LuceneVersion.LUCENE_48;

    public LuceneBm25Index(IOptions<RagOptions> opts, ILogger<LuceneBm25Index> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    // ── IBm25Index ────────────────────────────────────────────────────────────

    public Task AddBatchAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        EnsureWriter();

        var list = chunks.ToList();
        _lock.EnterWriteLock();
        try
        {
            foreach (var chunk in list)
            {
                // Delete any existing doc with same ID (upsert behaviour)
                _writer!.DeleteDocuments(new Term("id", chunk.Id.ToString()));

                var doc = new Document
                {
                    new StringField("id",      chunk.Id.ToString(),      Field.Store.YES),
                    new TextField ("content",  chunk.Content,            Field.Store.NO),
                    new StringField("source",  chunk.SourceFile,         Field.Store.YES),
                    new Int32Field ("page",    chunk.PageNumber,          Field.Store.YES),
                    new Int32Field ("chunk",   chunk.ChunkIndex,          Field.Store.YES),
                };

                _writer.AddDocument(doc);
                _chunkMap[chunk.Id.ToString()] = chunk;
            }

            _writer.Flush(triggerMerge: false, applyAllDeletes: true);
            RefreshSearcher();
        }
        finally { _lock.ExitWriteLock(); }

        _logger.LogDebug("BM25: indexed {Count} chunks", list.Count);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int topK = 5, CancellationToken ct = default)
    {
        EnsureWriter();

        _lock.EnterReadLock();
        try
        {
            if (_searcher is null || string.IsNullOrWhiteSpace(query))
                return Task.FromResult<IReadOnlyList<SearchResult>>([]);

            // Parse the query against the "content" field
            var parser = new QueryParser(LV, "content", _analyzer!);
            Query parsed;
            try
            {
                // Escape special Lucene characters from raw user input
                var escaped = QueryParserBase.Escape(query);
                parsed = parser.Parse(escaped);
            }
            catch (ParseException)
            {
                // Fall back to a simple term query if parsing fails
                parsed = new TermQuery(new Term("content", query.ToLowerInvariant()));
            }

            var hits = _searcher.Search(parsed, topK);
            var results = new List<SearchResult>();

            foreach (var hit in hits.ScoreDocs)
            {
                var doc = _searcher.Doc(hit.Doc);
                var id = doc.Get("id");

                if (_chunkMap.TryGetValue(id, out var chunk))
                {
                    results.Add(new SearchResult
                    {
                        Chunk = chunk,
                        Score = hit.Score,      // raw BM25 score
                        Rank = results.Count + 1
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }
        finally { _lock.ExitReadLock(); }
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            _writer?.Commit();
            _logger.LogInformation("Lucene BM25 index committed to disk");
        }
        finally { _lock.ExitWriteLock(); }

        return Task.CompletedTask;
    }

    public Task LoadAsync(CancellationToken ct = default)
    {
        // EnsureWriter opens the existing directory if it exists
        EnsureWriter();
        _logger.LogInformation(
            "Lucene BM25 index opened. Docs: {N}", _writer?.MaxDoc ?? 0);

        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureWriter()
    {
        if (_writer is not null) return;

        string path = Path.Combine(
            Path.GetDirectoryName(_opts.FaissIndexPath)!, "lucene");

        System.IO.Directory.CreateDirectory(path);

        _directory = FSDirectory.Open(path);
        _analyzer = new StandardAnalyzer(LV);

        var config = new IndexWriterConfig(LV, _analyzer);
        config.SetOpenMode(OpenMode.CREATE_OR_APPEND);

        _writer = new IndexWriter(_directory, config);
        RefreshSearcher();
    }

    private void RefreshSearcher()
    {
        _reader?.Dispose();
        _reader = _writer!.GetReader(applyAllDeletes: true);
        _searcher = new IndexSearcher(_reader);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _analyzer?.Dispose();
        _directory?.Dispose();
        _lock.Dispose();
    }
}