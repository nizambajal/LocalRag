namespace LocalRag.Domain.Entities;

public enum IndexingStatus { Pending, Processing, Completed, Failed }

/// <summary>
/// Tracks the lifecycle of a PDF indexing run.
/// Stored in memory (or a lightweight DB in future) so the API can report progress.
/// </summary>
public class IndexingJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FileName { get; init; }
    public IndexingStatus Status { get; set; } = IndexingStatus.Pending;
    public int TotalChunks { get; set; }
    public int ProcessedChunks { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
