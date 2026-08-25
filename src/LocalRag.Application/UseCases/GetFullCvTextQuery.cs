using LocalRag.Application.Contracts;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query ─────────────────────────────────────────────────────────────────────

public record GetFullCvTextQuery : IRequest<IReadOnlyDictionary<string, string>>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Reconstructs full document text per source file from indexed chunks.
/// Exists for §12's sandboxed CV quality-check tool — that script needs
/// the actual CV text (for required-section, duplicate-content, and
/// formatting checks), not top-K relevance snippets.
/// </summary>
public sealed class GetFullCvTextQueryHandler
    : IRequestHandler<GetFullCvTextQuery, IReadOnlyDictionary<string, string>>
{
    private readonly IVectorStore _vectorStore;

    public GetFullCvTextQueryHandler(IVectorStore vectorStore) => _vectorStore = vectorStore;

    public async Task<IReadOnlyDictionary<string, string>> Handle(
        GetFullCvTextQuery request, CancellationToken ct)
    {
        var chunks = await _vectorStore.GetAllChunksAsync(ct);

        return chunks
            .GroupBy(c => c.SourceFile)
            .ToDictionary(
                g => g.Key,
                g => string.Join("\n\n", g.OrderBy(c => c.ChunkIndex).Select(c => c.Content)));
    }
}