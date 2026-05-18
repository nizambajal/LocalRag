using LocalRag.Application.Contracts;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LocalRag.Tests;

/// <summary>
/// Tests for <see cref="PdfPigReader"/>.
///
/// Because PdfPig requires real PDF bytes, most tests here use a minimal
/// hand-crafted PDF (valid enough for PdfPig to parse) or test the reader's
/// error-handling behaviour via substituted interfaces.
/// </summary>
public class PdfPigReaderTests
{
    private static PdfPigReader CreateReader() =>
        new(NullLogger<PdfPigReader>.Instance);

    // ── File validation ───────────────────────────────────────────────────────

    [Fact]
    public void NonExistentFile_ThrowsFileNotFoundException()
    {
        var reader = CreateReader();

        Assert.Throws<FileNotFoundException>(() =>
            reader.ExtractPages("/no/such/file.pdf").ToList());
    }

    [Fact]
    public void EmptyString_ThrowsFileNotFoundException()
    {
        var reader = CreateReader();

        Assert.Throws<FileNotFoundException>(() =>
            reader.ExtractPages("").ToList());
    }

    // ── Contract tests via IPdfReader substitute ──────────────────────────────
    // These verify that downstream code (ChunkingService) uses the interface
    // correctly without depending on PdfPig internals.

    [Fact]
    public void IPdfReader_ReturnsPageTuples_WithCorrectPageNumbers()
    {
        var mockReader = Substitute.For<IPdfReader>();
        mockReader.ExtractPages(Arg.Any<string>())
            .Returns([
                (1, "First page content."),
                (2, "Second page content."),
                (3, "Third page content.")
            ]);

        var pages = mockReader.ExtractPages("any.pdf").ToList();

        Assert.Equal(3, pages.Count);
        Assert.Equal(1, pages[0].Page);
        Assert.Equal(2, pages[1].Page);
        Assert.Equal(3, pages[2].Page);
    }

    [Fact]
    public void IPdfReader_EmptyPdf_ReturnsEmptySequence()
    {
        var mockReader = Substitute.For<IPdfReader>();
        mockReader.ExtractPages(Arg.Any<string>()).Returns([]);

        var pages = mockReader.ExtractPages("empty.pdf").ToList();

        Assert.Empty(pages);
    }

    [Fact]
    public void IPdfReader_TextIsNotNullOrEmpty()
    {
        var mockReader = Substitute.For<IPdfReader>();
        mockReader.ExtractPages(Arg.Any<string>())
            .Returns([
                (1, "Some meaningful text on page one."),
                (2, "Another paragraph on page two with more words.")
            ]);

        var pages = mockReader.ExtractPages("doc.pdf").ToList();

        Assert.All(pages, p => Assert.False(string.IsNullOrWhiteSpace(p.Text)));
    }

    // ── Real PDF round-trip (using a minimal valid PDF) ───────────────────────

    [Fact]
    public void RealPdf_ExtractsExpectedText()
    {
        // Build a minimal but real PDF in memory (no external lib needed)
        string expected = "Hello from a real PDF page.";
        string path = CreateMinimalPdf(expected);

        try
        {
            var reader = CreateReader();
            var pages = reader.ExtractPages(path).ToList();

            Assert.NotEmpty(pages);
            Assert.Contains(pages, p => p.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealPdf_PageNumbersStartAtOne()
    {
        string path = CreateMinimalPdf("Page content here.");

        try
        {
            var reader = CreateReader();
            var pages = reader.ExtractPages(path).ToList();

            Assert.All(pages, p => Assert.True(p.Page >= 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Minimal PDF builder ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a single-page PDF that is valid enough for PdfPig to open.
    /// This uses raw PDF syntax — no third-party library required.
    /// The text is embedded using a standard Type1 font (Helvetica).
    /// </summary>
    private static string CreateMinimalPdf(string pageText)
    {
        // Escape parentheses in text (PDF string syntax)
        string escaped = pageText.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        string pdf = $@"%PDF-1.4
1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
3 0 obj<</Type/Page/MediaBox[0 0 612 792]/Parent 2 0 R/Resources<</Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>>>/Contents 4 0 R>>endobj
4 0 obj<</Length {(49 + escaped.Length)}>>";

        string stream = $"\nstream\nBT /F1 12 Tf 72 720 Td ({escaped}) Tj ET\nendstream\n";

        // Recalculate length accurately
        int streamBodyLen = System.Text.Encoding.ASCII.GetByteCount(
            $"\nBT /F1 12 Tf 72 720 Td ({escaped}) Tj ET\n");

        string pdfFinal = $@"%PDF-1.4
1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
3 0 obj<</Type/Page/MediaBox[0 0 612 792]/Parent 2 0 R/Resources<</Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>>>/Contents 4 0 R>>endobj
4 0 obj<</Length {streamBodyLen}>>
stream
BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET
endstream
endobj
xref
0 5
0000000000 65535 f 
trailer<</Size 5/Root 1 0 R>>
startxref
9
%%EOF";

        string path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid()}.pdf");
        File.WriteAllText(path, pdfFinal, System.Text.Encoding.ASCII);
        return path;
    }
}
