using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace LocalRag.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class IndexController : ControllerBase
{
    private readonly IIndexingTracker _tracker;
    private readonly IVectorStore _vectorStore;

    public IndexController(IIndexingTracker tracker, IVectorStore vectorStore)
    {
        _tracker = tracker;
        _vectorStore = vectorStore;
    }

    /// <summary>Returns summary stats about the current FAISS index.</summary>
    [HttpGet("stats")]
    public IActionResult Stats() =>
        Ok(new { VectorCount = _vectorStore.Count });

    /// <summary>Returns all indexing jobs (active + historical).</summary>
    [HttpGet("jobs")]
    public IActionResult Jobs() =>
        Ok(_tracker.GetAll());

    /// <summary>Returns a specific indexing job by ID.</summary>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType(typeof(IndexingJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Job(Guid id)
    {
        var job = _tracker.Get(id);
        return job is null ? NotFound() : Ok(job);
    }
}
