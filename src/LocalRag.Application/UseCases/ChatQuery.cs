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
    float VectorWeight = 0.4f,
    float Bm25Weight = 0.6f
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

    //public async Task<ChatResponse> Handle(ChatQuery request, CancellationToken ct)
    //{
    //    var sw = System.Diagnostics.Stopwatch.StartNew();

    //    // 1. Retrieve relevant chunks via hybrid search
    //    var results = await _hybrid.SearchAsync(
    //        request.Query,
    //        request.TopK,
    //        request.VectorWeight,
    //        request.Bm25Weight,
    //        ct);

    //    var chunks = results.Select(r => r.Chunk).ToList();

    //    // Always inject page 1 chunk (contains name/header) if not already present
    //    // This ensures identity questions always have the name in context
    //    var hasPageOne = chunks.Any(c => c.PageNumber == 1 && c.ChunkIndex == 0);

    //    if (!hasPageOne) {
    //        var headerChunk = results
    //            .Select(r => r.Chunk)
    //            .OrderBy(c => c.ChunkIndex)
    //            .FirstOrDefault(c => c.PageNumber == 1);

    //        if (headerChunk is not null)
    //        {
    //            chunks.Insert(0, headerChunk);
    //        }
    //    }

    //    // 2. Generate grounded answer from LLM (or fallback)
    //    var answer = await _chat.AnswerAsync(request.Query, chunks, ct);

    //    sw.Stop();

    //    // 3. Build source citations
    //    var sources = results.Select(r => new ContextChunkDto(
    //        r.Chunk.SourceFile,
    //        r.Chunk.PageNumber,
    //        r.Chunk.ChunkIndex,
    //        r.Chunk.Content[..Math.Min(200, r.Chunk.Content.Length)] + "...",
    //        r.CombinedScore)).ToList();

    //    return new ChatResponse(answer, sources, sw.Elapsed);
    //}

    public async Task<ChatResponse> Handle(ChatQuery request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool isIdentity = IsIdentityQuestion(request.Query);

        // Choose search query
        string searchQuery = isIdentity
            ? "name developer contact email phone location"
            : request.Query;

        float vectorW = isIdentity ? 0.2f : request.VectorWeight;
        float bm25W = isIdentity ? 0.8f : request.Bm25Weight;

        var results = await _hybrid.SearchAsync(
            searchQuery,
            request.TopK,
            vectorW,
            bm25W,
            ct);

        var chunks = results.Select(r => r.Chunk).ToList();

        // ── Always inject Page 1 Chunk 0 (name/header) ────────────────────────
        // This guarantees identity info is always available to the model
        bool hasHeaderChunk = chunks.Any(c => c.PageNumber == 1 && c.ChunkIndex == 0);

        if (!hasHeaderChunk)
        {
            // Find chunk 0 from any result already in memory
            var headerFromResults = results
                .Select(r => r.Chunk)
                .FirstOrDefault(c => c.ChunkIndex == 0);

            if (headerFromResults is not null)
            {
                chunks.Insert(0, headerFromResults);
            }
            else
            {
                // If not in results, search for it directly
                var headerSearch = await _hybrid.SearchAsync(
                    "NIZAMUDDIEN senior NET nopCommerce developer Mangalore",
                    topK: 3,
                    vectorWeight: 0.1f,
                    bm25Weight: 0.9f,
                    ct);

                var header = headerSearch
                    .Select(r => r.Chunk)
                    .OrderBy(c => c.ChunkIndex)
                    .FirstOrDefault();

                if (header is not null)
                    chunks.Insert(0, header);
            }
        }

        // Remove duplicate chunks (same ChunkIndex)
        chunks = chunks
            .GroupBy(c => c.ChunkIndex)
            .Select(g => g.First())
            .ToList();

        var answer = await _chat.AnswerAsync(request.Query, chunks, ct);
        sw.Stop();

        var sources = chunks.Select(c => new ContextChunkDto(
            c.SourceFile,
            c.PageNumber,
            c.ChunkIndex,
            c.Content[..Math.Min(200, c.Content.Length)] + "...",
            0f)).ToList();

        return new ChatResponse(answer, sources, sw.Elapsed);
    }

    private static bool IsIdentityQuestion(string query)
    {
        var q = query.ToLowerInvariant().Trim();

        var identityPhrases = new[]
        {
        // Original
        "who is this",
        "who is he",
        "who is she",
        "whose resume",
        "whose cv",
        "what is this",
        "who are you",
        "introduce",
        "tell me about this person",
        "about this person",
        "this document",
        "this resume",
        "this cv",
        "who is the person",
        "what is the name",
        "who is it",

        // ── New additions ──────────────────────────────────────────
        "your name",
        "his name",
        "her name",
        "the name",
        "full name",
        "candidate name",
        "person name",
        "what is your",
        "who owns",
        "who wrote",
        "who made",
        "who created",
        "who sent",
        "about you",
        "about him",
        "about her",
        "tell me about",
        "describe yourself",
        "describe this",
        "what do you do",
        "your role",
        "your title",
        "your position",
        "your email",
        "your phone",
        "your contact",
        "your location",
        "where are you",
        "where do you",
        "how can i contact",
        "contact details",
        "contact info"
    };

        return identityPhrases.Any(p => q.Contains(p));
    }
}