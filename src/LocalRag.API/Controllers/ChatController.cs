using LocalRag.Application.UseCases;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace LocalRag.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ChatController : ControllerBase
{
    private readonly IMediator _mediator;
    public ChatController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// RAG chat — retrieves relevant chunks then generates a grounded answer.
    /// Requires Ollama running locally, or returns structured context if not configured.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ChatResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Chat(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query must not be empty.");

        var result = await _mediator.Send(new ChatQuery(
            request.Query,
            request.TopK ?? 5,
            request.VectorWeight ?? 0.7f,
            request.Bm25Weight ?? 0.3f), ct);

        return Ok(result);
    }
}

public record ChatRequest(
    string Query,
    int? TopK,
    float? VectorWeight,
    float? Bm25Weight);