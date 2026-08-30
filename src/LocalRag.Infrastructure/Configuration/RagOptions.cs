namespace LocalRag.Infrastructure.Configuration;

public class RagOptions
{
    public const string SectionName = "Rag";

    public string PdfFolder { get; set; } = "./pdfs";
    public string ModelPath { get; set; } = "./models/onnx/all-MiniLM-L6-v2.onnx";
    public string TokenizerPath { get; set; } = "./models/onnx/tokenizer.json";
    public string FaissIndexPath { get; set; } = "./data/faiss/index.bin";
    public string FaissMetadataPath { get; set; } = "./data/faiss/metadata.json";
    public int EmbeddingDimensions { get; set; } = 384;
    public int ChunkSize { get; set; } = 500;
    public int ChunkOverlap { get; set; } = 50;
    public int DefaultTopK { get; set; } = 5;
    public int BatchSize { get; set; } = 32;
    public int WorkerIntervalSeconds { get; set; } = 30;

    // ── Ollama (Phase 7 chat) ─────────────────────────────────────────────────
    /// <summary>Set true to enable LLM answers via Ollama.</summary>
    public bool OllamaMode { get; set; } = false;
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "mistral";

    // ── LocalRag.Mcp debug logging ───────────────────────────────────────────
    /// <summary>
    /// When true, tool audit logs write FULL request/response content instead of truncated summaries — including CV text. 
    /// Off by default. Turn on only for local debugging sessions on your own machine, and turn back off afterward.
    /// </summary>
    public bool VerboseToolLogging { get; set; } = false;
}