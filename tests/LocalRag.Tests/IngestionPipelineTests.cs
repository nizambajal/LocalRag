using LocalRag.Application.Contracts;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace LocalRag.Tests;

/// <summary>
/// Integration tests that stitch together a mock <see cref="IPdfReader"/>
/// with the real <see cref="ChunkingService"/> to verify the ingestion
/// pipeline produces valid, well-formed <see cref="LocalRag.Domain.Entities.DocumentChunk"/> records.
/// </summary>
public class IngestionPipelineTests
{
    private static ChunkingService BuildChunker(int size = 200, int overlap = 40)
    {
        var opts = Options.Create(new RagOptions { ChunkSize = size, ChunkOverlap = overlap });
        return new ChunkingService(opts, NullLogger<ChunkingService>.Instance);
    }

    [Fact]
    public void Pipeline_ProducesChunksWithNonEmptyContent()
    {
        var reader = Substitute.For<IPdfReader>();
        reader.ExtractPages(Arg.Any<string>()).Returns(
            Enumerable.Range(1, 5)
                .Select(p => (p, $"Page {p}: " + string.Join(". ",
                    Enumerable.Repeat("The embedding pipeline processes each chunk independently.", 6))))
                .ToArray());

        var chunker = BuildChunker();
        var pages = reader.ExtractPages("report.pdf");
        var chunks = chunker.Chunk(pages, "report.pdf").ToList();

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Content));
            Assert.NotEqual(Guid.Empty, c.Id);
            Assert.Equal("report.pdf", c.SourceFile);
            Assert.Empty(c.Vector); // embedding not yet done
        });
    }

    [Fact]
    public void Pipeline_TotalContentLengthApproximatesInput()
    {
        const string sentence = "Retrieval augmented generation combines search with generation. ";
        var fullText = string.Join("", Enumerable.Repeat(sentence, 30)); // ~1950 chars

        var reader = Substitute.For<IPdfReader>();
        reader.ExtractPages(Arg.Any<string>()).Returns([(1, fullText)]);

        var chunker = BuildChunker(size: 300, overlap: 50);
        var chunks = chunker.Chunk(reader.ExtractPages("x.pdf"), "x.pdf").ToList();

        // Total chars across chunks should cover most of the original text
        // (overlap means it can exceed the original by up to: chunks * overlap chars)
        int totalChars = chunks.Sum(c => c.Content.Length);
        Assert.True(totalChars >= fullText.Length * 0.9,
            $"Expected ~{fullText.Length} chars covered; got {totalChars}");
    }

    [Fact]
    public void Pipeline_NoDuplicateChunkIds()
    {
        var reader = Substitute.For<IPdfReader>();
        reader.ExtractPages(Arg.Any<string>()).Returns(
        [
            (1, string.Join(". ", Enumerable.Repeat("Unique sentence for ID collision test", 20)))
        ]);

        var chunker = BuildChunker(size: 100, overlap: 20);
        var chunks = chunker.Chunk(reader.ExtractPages("ids.pdf"), "ids.pdf").ToList();

        var ids = chunks.Select(c => c.Id).ToHashSet();
        Assert.Equal(chunks.Count, ids.Count); // all IDs must be unique
    }

    [Fact]
    public void Pipeline_ChunksAreSortedByChunkIndex()
    {
        var reader = Substitute.For<IPdfReader>();
        reader.ExtractPages(Arg.Any<string>()).Returns(
        [
            (1, string.Join(" ", Enumerable.Range(1, 100).Select(i => $"Word{i}")))
        ]);

        var chunker = BuildChunker(size: 80, overlap: 15);
        var chunks = chunker.Chunk(reader.ExtractPages("ordered.pdf"), "ordered.pdf").ToList();

        for (int i = 1; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }
}
