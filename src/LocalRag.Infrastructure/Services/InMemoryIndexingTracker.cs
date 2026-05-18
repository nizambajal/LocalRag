using System.Collections.Concurrent;
using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Thread-safe in-memory store for indexing job state.
/// Replace with a database-backed implementation for multi-instance deployments.
/// </summary>
public sealed class InMemoryIndexingTracker : IIndexingTracker
{
    private readonly ConcurrentDictionary<Guid, IndexingJob> _jobs = new();

    public IndexingJob Start(string fileName)
    {
        var job = new IndexingJob { FileName = fileName, Status = IndexingStatus.Processing };
        _jobs[job.Id] = job;
        return job;
    }

    public void Complete(Guid jobId, int totalChunks)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = IndexingStatus.Completed;
            job.TotalChunks = totalChunks;
            job.ProcessedChunks = totalChunks;
            job.FinishedAt = DateTime.UtcNow;
        }
    }

    public void Fail(Guid jobId, string errorMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = IndexingStatus.Failed;
            job.ErrorMessage = errorMessage;
            job.FinishedAt = DateTime.UtcNow;
        }
    }

    public IndexingJob? Get(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<IndexingJob> GetAll() =>
        _jobs.Values.OrderByDescending(j => j.StartedAt).ToList();
}
