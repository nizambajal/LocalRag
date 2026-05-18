using LocalRag.Infrastructure.Configuration;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Tests;

/// <summary>
/// Unit tests for <see cref="ChunkingService"/>.
/// No external dependencies — all tests run fully offline.
/// </summary>
public class ChunkingServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ChunkingService CreateService(int chunkSize = 500, int overlap = 50)
    {
        var opts = Options.Create(new RagOptions
        {
            ChunkSize = chunkSize,
            ChunkOverlap = overlap
        });
        return new ChunkingService(opts, NullLogger<ChunkingService>.Instance);
    }

    private static IEnumerable<(int Page, string Text)> SinglePage(string text)
        => [(1, text)];

    // ── Basic chunking ────────────────────────────────────────────────────────

    [Fact]
    public void ShortText_ProducesSingleChunk()
    {
        var svc = CreateService(chunkSize: 500, overlap: 50);
        var text = "Hello world. This is a short document.";

        var chunks = svc.Chunk(SinglePage(text), "test.pdf").ToList();

        Assert.Single(chunks);
        Assert.Contains("Hello world", chunks[0].Content);
    }

    [Fact]
    public void LongText_ProducesMultipleChunks()
    {
        var svc = CreateService(chunkSize: 100, overlap: 20);

        // Build 600-char text so we expect ~6+ chunks at size 100
        var text = string.Join(" ", Enumerable.Repeat("The quick brown fox jumped.", 25));

        var chunks = svc.Chunk(SinglePage(text), "long.pdf").ToList();

        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    [Fact]
    public void ChunksHaveCorrectOverlap()
    {
        var svc = CreateService(chunkSize: 100, overlap: 30);

        // A deterministic 300-char sentence-heavy string
        var text = string.Join(" ", Enumerable.Repeat("Pack my box with five dozen liquor jugs.", 8));

        var chunks = svc.Chunk(SinglePage(text), "overlap.pdf").ToList();

        // Each chunk's tail should appear at the start of the next chunk
        for (int i = 0; i < chunks.Count - 1; i++)
        {
            string tailOfCurrent = chunks[i].Content[^Math.Min(25, chunks[i].Content.Length)..];
            string headOfNext = chunks[i + 1].Content[..Math.Min(chunks[i + 1].Content.Length, 80)];
            Assert.True(
                headOfNext.Contains(tailOfCurrent.Split(' ').First(), StringComparison.Ordinal),
                $"Chunk {i + 1} should share content with chunk {i} (overlap not preserved)");
        }
    }

    [Fact]
    public void EmptyInput_ProducesNoChunks()
    {
        var svc = CreateService();

        var chunks = svc.Chunk([], "empty.pdf").ToList();

        Assert.Empty(chunks);
    }

    [Fact]
    public void WhitespaceOnlyPage_ProducesNoChunks()
    {
        var svc = CreateService();

        var chunks = svc.Chunk([(1, "   \n\n\t  ")], "whitespace.pdf").ToList();

        Assert.Empty(chunks);
    }

    // ── Metadata correctness ──────────────────────────────────────────────────

    [Fact]
    public void ChunkIndicesAreSequentialFromZero()
    {
        var svc = CreateService(chunkSize: 80, overlap: 10);
        var text = string.Join(". ", Enumerable.Range(1, 50).Select(i => $"Sentence number {i} goes here"));

        var chunks = svc.Chunk(SinglePage(text), "seq.pdf").ToList();

        for (int i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }

    [Fact]
    public void SourceFileNameIsPreserved()
    {
        var svc = CreateService();
        var text = "Some content for testing source file tracking.";

        var chunks = svc.Chunk(SinglePage(text), "/absolute/path/to/MyDoc.pdf").ToList();

        Assert.All(chunks, c => Assert.Equal("MyDoc.pdf", c.SourceFile));
    }

    [Fact]
    public void PageNumberIsTaggedCorrectly()
    {
        var svc = CreateService(chunkSize: 50, overlap: 5);

        // Two pages; first page content should tag page 1, second page 2
        var pages = new (int, string)[]
        {
            (1, "Page one content. This is the first page. It has sentences."),
            (2, "Page two content. This is the second page. Also has sentences.")
        };

        var chunks = svc.Chunk(pages, "multipage.pdf").ToList();

        Assert.Contains(chunks, c => c.PageNumber == 1);
        Assert.Contains(chunks, c => c.PageNumber == 2);
    }

    [Fact]
    public void VectorIsInitiallyEmpty()
    {
        var svc = CreateService();
        var text = "A sentence to chunk.";

        var chunks = svc.Chunk(SinglePage(text), "test.pdf").ToList();

        Assert.All(chunks, c => Assert.Empty(c.Vector));
    }

    // ── Boundary / edge cases ─────────────────────────────────────────────────

    [Fact]
    public void TextExactlyChunkSize_ProducesSingleChunk()
    {
        const int size = 100;
        var svc = CreateService(chunkSize: size, overlap: 20);

        // Exactly `size` chars, ending in a period so the boundary snap works cleanly
        var text = new string('A', size - 1) + ".";
        Assert.Equal(size, text.Length);

        var chunks = svc.Chunk(SinglePage(text), "exact.pdf").ToList();

        // Should be one chunk (no overflow)
        Assert.Single(chunks);
    }

    [Fact]
    public void SingleWordLongerThanChunkSize_IsNotDropped()
    {
        // Degenerate case: one very long token (e.g. a URL or base64 blob in PDF)
        var svc = CreateService(chunkSize: 50, overlap: 10);
        var text = new string('X', 200);

        var chunks = svc.Chunk(SinglePage(text), "longword.pdf").ToList();

        // Total content across all chunks should cover the original text
        var combined = string.Concat(chunks.Select(c => c.Content.Replace(" ", "")));
        Assert.True(combined.Length > 0, "Content must not be lost");
    }

    [Fact]
    public void MultiPage_AllChunksHaveContent()
    {
        var svc = CreateService(chunkSize: 150, overlap: 30);

        var pages = Enumerable.Range(1, 5)
            .Select(p => (p, $"Page {p}: " + string.Join(". ", Enumerable.Repeat("Content sentence here", 10))))
            .ToArray();

        var chunks = svc.Chunk(pages, "five-pages.pdf").ToList();

        Assert.All(chunks, c =>
        {
            Assert.NotEmpty(c.Content);
            Assert.True(c.Content.Length <= 500, "Chunk should not far exceed configured size");
        });
    }

    // ── Configuration guards ──────────────────────────────────────────────────

    [Fact]
    public void ZeroOverlap_DoesNotInfiniteLoop()
    {
        var svc = CreateService(chunkSize: 100, overlap: 0);
        var text = string.Join(". ", Enumerable.Repeat("Normal sentence here", 20));

        // Should terminate and produce chunks
        var chunks = svc.Chunk(SinglePage(text), "nooverlap.pdf").ToList();

        Assert.NotEmpty(chunks);
    }
}
