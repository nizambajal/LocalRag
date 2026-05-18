using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Splits document text into overlapping chunks suitable for embedding.
///
/// Strategy:
///   1. Concatenate all pages into a single character stream, tagging each
///      character with its source page number.
///   2. Walk the stream with a sliding window (ChunkSize chars, Overlap step-back).
///   3. At each boundary, snap forward to the next sentence end ('. ', '! ', '? ',
///      '\n') so chunks never split mid-sentence.
///   4. Produce DocumentChunk records — ready to be embedded by Phase 3.
/// </summary>
public sealed class ChunkingService : IChunkingService
{
    private readonly RagOptions _opts;
    private readonly ILogger<ChunkingService> _logger;

    // Sentence-ending punctuation we prefer to split after
    private static readonly char[] SentenceEnders = ['.', '!', '?', '\n'];

    // How far ahead we scan for a sentence boundary before giving up
    private const int BoundaryLookAhead = 120;

    public ChunkingService(IOptions<RagOptions> opts, ILogger<ChunkingService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public IEnumerable<DocumentChunk> Chunk(
        IEnumerable<(int Page, string Text)> pages,
        string sourceFile)
    {
        // ── 1. Build tagged char stream ───────────────────────────────────
        var (fullText, pageMap) = BuildTaggedStream(pages);

        if (fullText.Length == 0)
        {
            _logger.LogWarning("No text extracted from {File}", sourceFile);
            yield break;
        }

        _logger.LogDebug(
            "Chunking {File}: {Chars} chars, size={Size}, overlap={Overlap}",
            Path.GetFileName(sourceFile), fullText.Length,
            _opts.ChunkSize, _opts.ChunkOverlap);

        // ── 2. Slide the window ───────────────────────────────────────────
        int position = 0;
        int chunkIndex = 0;

        while (position < fullText.Length)
        {
            int end = Math.Min(position + _opts.ChunkSize, fullText.Length);

            // Snap to sentence boundary unless we're at EOF
            if (end < fullText.Length)
                end = FindSentenceBoundary(fullText, end);

            string content = fullText[position..end].Trim();

            if (!string.IsNullOrWhiteSpace(content))
            {
                yield return new DocumentChunk
                {
                    Content = content,
                    Vector = [],           // filled in by Phase 3 (embedding service)
                    SourceFile = Path.GetFileName(sourceFile),
                    ChunkIndex = chunkIndex,
                    PageNumber = pageMap[position]
                };

                chunkIndex++;
            }

            // Step forward by (ChunkSize - Overlap) so consecutive chunks share
            // the tail of the previous one — preserving cross-boundary context.
            int step = _opts.ChunkSize - _opts.ChunkOverlap;
            if (step <= 0) step = _opts.ChunkSize;  // guard against bad config
            position += step;
        }

        _logger.LogInformation(
            "Chunked {File} into {N} chunks", Path.GetFileName(sourceFile), chunkIndex);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Concatenates all page texts into a single string.
    /// Returns the string and a parallel int[] mapping each character position
    /// to its originating page number — O(1) page lookup during chunking.
    /// </summary>
    private static (string FullText, int[] PageMap) BuildTaggedStream(
        IEnumerable<(int Page, string Text)> pages)
    {
        var sb = new System.Text.StringBuilder();
        var pagePositions = new List<(int Start, int Page)>();

        foreach (var (page, text) in pages)
        {
            if (string.IsNullOrEmpty(text)) continue;

            pagePositions.Add((sb.Length, page));
            sb.Append(text);

            // Paragraph separator between pages so chunking doesn't glue
            // the end of one page to the start of the next.
            if (!text.EndsWith('\n'))
                sb.Append('\n');
        }

        var fullText = sb.ToString();

        // Build dense page map (one int per character)
        var pageMap = new int[fullText.Length];
        for (int i = 0; i < pagePositions.Count; i++)
        {
            int start = pagePositions[i].Start;
            int end = i + 1 < pagePositions.Count
                ? pagePositions[i + 1].Start
                : fullText.Length;

            pagePositions[i].Page.CopyTo(pageMap, start, end - start);
        }

        return (fullText, pageMap);
    }

    /// <summary>
    /// Starting from <paramref name="rawEnd"/>, scan forward up to
    /// <see cref="BoundaryLookAhead"/> chars for a sentence-ending character.
    /// Returns the original position if no boundary is found in range.
    /// </summary>
    private static int FindSentenceBoundary(string text, int rawEnd)
    {
        int limit = Math.Min(rawEnd + BoundaryLookAhead, text.Length);

        for (int i = rawEnd; i < limit; i++)
        {
            if (Array.IndexOf(SentenceEnders, text[i]) >= 0)
            {
                // Include the punctuation char itself in this chunk, then +1
                return Math.Min(i + 1, text.Length);
            }
        }

        // No sentence boundary found — hard-cut at rawEnd
        return rawEnd;
    }
}

// ── Extension helpers ─────────────────────────────────────────────────────────

file static class Extensions
{
    /// <summary>
    /// Copies a single value into a range of an array.
    /// Avoids allocating a temporary span just to fill with one value.
    /// </summary>
    public static void CopyTo(this int value, int[] destination, int startIndex, int count)
    {
        for (int i = startIndex; i < startIndex + count && i < destination.Length; i++)
            destination[i] = value;
    }
}
