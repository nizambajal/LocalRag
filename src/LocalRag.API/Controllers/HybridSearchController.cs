using LocalRag.Application.UseCases;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace LocalRag.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class HybridSearchController : ControllerBase
{
    private readonly IMediator _mediator;
    public HybridSearchController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Hybrid BM25 + vector search fused via Reciprocal Rank Fusion.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(HybridSearchResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Search(
        [FromBody] HybridSearchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query must not be empty.");

        var result = await _mediator.Send(new HybridSearchQuery(
            request.Query,
            request.TopK ?? 5,
            request.VectorWeight ?? 0.7f,
            request.Bm25Weight ?? 0.3f), ct);

        return Ok(result);
    }
}

public record HybridSearchRequest(
    string Query,
    int? TopK,
    float? VectorWeight,
    float? Bm25Weight);