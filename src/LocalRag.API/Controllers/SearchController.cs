using LocalRag.Application.UseCases;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace LocalRag.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class SearchController : ControllerBase
{
    private readonly IMediator _mediator;

    public SearchController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Semantic search over all indexed PDFs.
    /// </summary>
    /// <param name="request">Query text and optional top-K count.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(SearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromBody] SearchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query must not be empty.");

        var result = await _mediator.Send(
            new SearchQuery(request.Query, request.TopK ?? 5), ct);

        return Ok(result);
    }
}

/// <summary>Request body for the POST /api/search endpoint.</summary>
public record SearchRequest(string Query, int? TopK);
