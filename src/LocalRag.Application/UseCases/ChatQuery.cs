using LocalRag.Application.Contracts;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query + Response ──────────────────────────────────────────────────────────

//public record ChatQuery(
//    string Query,
//    int TopK = 5,    // ← keep at 5 for summarisation. Career/responsibility questions need more context
//    float VectorWeight = 0.6f, // ← balanced weights
//    float Bm25Weight = 0.4f  // ← BM25 better for name lookup
//) : IRequest<ChatResponse>;

public record ChatQuery(
    string Query,
    int TopK = 3,     // ← reduced from 5 to 3
    float VectorWeight = 0.6f,
    float Bm25Weight = 0.4f
) : IRequest<ChatResponse>;

public record ChatResponse(
    string Answer,
    IReadOnlyList<ContextChunkDto> SourceChunks,
    TimeSpan Elapsed);

public record ContextChunkDto(
    string SourceFile,
    int PageNumber,
    int ChunkIndex,
    string Content,
    float Score);

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class ChatQueryHandler : IRequestHandler<ChatQuery, ChatResponse>
{
    private readonly IHybridSearchService _hybrid;
    private readonly IChatService _chat;

    public ChatQueryHandler(IHybridSearchService hybrid, IChatService chat)
    {
        _hybrid = hybrid;
        _chat = chat;
    }

    public async Task<ChatResponse> Handle(ChatQuery request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Retrieve relevant chunks via hybrid search
        var results = await _hybrid.SearchAsync(
            request.Query,
            request.TopK,
            request.VectorWeight,
            request.Bm25Weight,
            ct);

        var chunks = results.Select(r => r.Chunk).ToList();

        // 2. Generate grounded answer from LLM (or fallback)
        var answer = await _chat.AnswerAsync(request.Query, chunks, ct);

        sw.Stop();

        // 3. Build source citations
        var sources = results.Select(r => new ContextChunkDto(
            r.Chunk.SourceFile,
            r.Chunk.PageNumber,
            r.Chunk.ChunkIndex,
            r.Chunk.Content[..Math.Min(200, r.Chunk.Content.Length)] + "...",
            r.CombinedScore)).ToList();

        return new ChatResponse(answer, sources, sw.Elapsed);
    }
}