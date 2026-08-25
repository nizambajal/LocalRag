using LocalRag.Domain.Entities;

namespace LocalRag.Application.Contracts;

// ── PDF Reader ────────────────────────────────────────────────────────────────
public interface IPdfReader
{
    IEnumerable<(int Page, string Text)> ExtractPages(string filePath);
}

// ── Chunking ──────────────────────────────────────────────────────────────────
public interface IChunkingService
{
    IEnumerable<DocumentChunk> Chunk(
        IEnumerable<(int Page, string Text)> pages,
        string sourceFile);
}

// ── Embedding ─────────────────────────────────────────────────────────────────
public interface IEmbeddingService
{
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default);
}

// ── Vector Store ──────────────────────────────────────────────────────────────
public interface IVectorStore
{
    long Count { get; }
    Task AddAsync(DocumentChunk chunk, CancellationToken ct = default);
    Task AddBatchAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryVector, int topK = 5, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Every indexed chunk, unfiltered. Used to reconstruct full document
    /// text (e.g. for the sandboxed CV quality-check tool) — not for
    /// relevance search, which stays on <see cref="SearchAsync"/>.
    /// </summary>
    Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(CancellationToken ct = default);
}

// ── BM25 Keyword Index ────────────────────────────────────────────────────────
/// <summary>
/// Lucene-backed BM25 keyword index.
/// Complements vector search — strong on exact terms, acronyms, proper nouns.
/// </summary>
public interface IBm25Index
{
    /// <summary>Add or update a chunk in the keyword index.</summary>
    Task AddBatchAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);

    /// <summary>BM25 keyword search — returns topK chunks with BM25 scores.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int topK = 5, CancellationToken ct = default);

    /// <summary>Persist the Lucene index to disk.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Open an existing index from disk (no-op if missing).</summary>
    Task LoadAsync(CancellationToken ct = default);
}

// ── Chat (RAG) ────────────────────────────────────────────────────────────────
/// <summary>
/// Generates an answer grounded in retrieved document chunks.
/// Runs fully locally using an ONNX or llama.cpp language model.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Given a user question and the retrieved context chunks,
    /// produce a grounded answer string.
    /// </summary>
    Task<string> AnswerAsync(
        string question,
        IReadOnlyList<DocumentChunk> contextChunks,
        CancellationToken ct = default);
}

// ── Indexing Tracker ──────────────────────────────────────────────────────────
public interface IIndexingTracker
{
    LocalRag.Domain.Entities.IndexingJob Start(string fileName);
    void Complete(Guid jobId, int totalChunks);
    void Fail(Guid jobId, string errorMessage);
    LocalRag.Domain.Entities.IndexingJob? Get(Guid jobId);
    IReadOnlyList<LocalRag.Domain.Entities.IndexingJob> GetAll();
}


// ── Hybrid Search ──────────────────────────────────────────────────────────
public interface IHybridSearchService
{
    Task<IReadOnlyList<HybridSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        float vectorWeight = 0.7f,
        float bm25Weight = 0.3f,
        CancellationToken ct = default);
}

// ── Job Description Extraction ────────────────────────────────────────────────
/// <summary>
/// Extracts structured requirements from a free-text job description.
/// Extraction only — never judges candidate fit; that's the agent's job.
/// </summary>
public interface IJobDescriptionExtractor
{
    Task<JobDescriptionAnalysis> ExtractAsync(
        string jobDescriptionText, CancellationToken ct = default);
}

// ── Skill Classification ───────────────────────────────────────────────────────
/// <summary>
/// Classifies a single skill against CV evidence already retrieved via hybrid
/// search. Never called with zero evidence — the caller short-circuits that
/// case to "Missing" deterministically instead of asking the LLM.
/// </summary>
public interface ISkillClassifier
{
    Task<(string Classification, string Reasoning)> ClassifyAsync(
        string skill,
        IReadOnlyList<HybridSearchResult> evidence,
        CancellationToken ct = default);
}

// ── Interview Question Generation ─────────────────────────────────────────────
/// <summary>
/// Generates categorized interview questions from a skill-gap report. Model
/// answers are only ever attached to Candidate-Specific questions grounded
/// in Strong Match CV evidence — never fabricated.
/// </summary>
public interface IInterviewQuestionGenerator
{
    Task<List<InterviewQuestion>> GenerateAsync(
        SkillGapReport skillGap, CancellationToken ct = default);
}

// ── CV Tailoring ───────────────────────────────────────────────────────────────
/// <summary>
/// Generates tailored CV sections from a skill-gap report. Every bullet must
/// be grounded in CV evidence and labeled Existing Experience or Suggested
/// Wording — never fabricated experience.
/// </summary>
public interface ICvTailoringGenerator
{
    Task<List<TailoredCvSection>> GenerateAsync(
        SkillGapReport skillGap, CancellationToken ct = default);
}