namespace LocalRag.Domain.Entities;

/// <summary>
/// Represents a single text chunk extracted from a PDF, together with its embedding vector.
/// This is the core unit of retrieval in the RAG system.
/// </summary>
public class DocumentChunk
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Raw text content of this chunk (≈500 chars, 50-char overlap with neighbours).</summary>
    public required string Content { get; init; }

    /// <summary>Embedding vector produced by the ONNX model (384 dims for MiniLM-L6-v2).</summary>
    public required float[] Vector { get; set; }

    /// <summary>Original PDF filename (relative path from the /pdfs folder).</summary>
    public required string SourceFile { get; init; }

    /// <summary>Zero-based chunk index within the source document, used for ordering results.</summary>
    public int ChunkIndex { get; init; }

    /// <summary>Page number in the PDF where this chunk begins (1-based, 0 if unknown).</summary>
    public int PageNumber { get; init; }

    public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
}
