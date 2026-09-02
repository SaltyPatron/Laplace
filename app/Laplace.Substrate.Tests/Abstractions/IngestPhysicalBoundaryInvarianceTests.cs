using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// First executable slice of #1443: transport/read boundaries are physical-plan
/// choices and may not become source-record or canonical-semantic boundaries.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class IngestPhysicalBoundaryInvarianceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(31)]
    public void CsvRecordFraming_IsInvariantToTransportFeedChunkSize(int chunkSize)
    {
        // Intentionally combines:
        // - multibyte UTF-8 whose scalar bytes are split by the 1/2-byte plans;
        // - a quoted embedded newline that is NOT a record boundary;
        // - CRLF and LF record terminators;
        // - quotes and delimiters that cross several tested feed boundaries.
        byte[] payload = Encoding.UTF8.GetBytes(
            "α,b,\"line1\nβline2\",c\r\n" +
            "x,é,\"quoted,comma\",z\n" +
            "終,tail,\"done\",q\n");

        var expected = FrameCsv(payload, payload.Length);
        var actual = FrameCsv(payload, chunkSize);

        Assert.Equal(3, expected.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void CsvRecordFraming_OneByteFeedsPreserveExactSourceBytes()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            "a,b,\"line1\nline2\",c\n" +
            "猫,犬,\"é\",終\n");

        var rows = FrameCsv(payload, 1);

        Assert.Equal(2, rows.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("a,b,\"line1\nline2\",c"), rows[0]);
        Assert.Equal(Encoding.UTF8.GetBytes("猫,犬,\"é\",終"), rows[1]);
    }

    private static List<byte[]> FrameCsv(byte[] payload, int chunkSize)
    {
        Assert.True(chunkSize > 0);
        IntPtr recipe = GrammarDecomposer.LookupById("csv");
        Assert.NotEqual(IntPtr.Zero, recipe);

        IntPtr iter = StructuredGrammarIngest.CreateRowIterForPipeline(recipe);
        Assert.NotEqual(IntPtr.Zero, iter);

        try
        {
            var rows = new List<byte[]>();
            int offset = 0;
            while (offset < payload.Length)
            {
                int count = Math.Min(chunkSize, payload.Length - offset);
                byte[] feed = payload.AsSpan(offset, count).ToArray();
                rows.AddRange(StructuredGrammarIngest.FeedRawLinesForPipeline(iter, feed, count));
                offset += count;
            }

            // EOF is an explicit zero-length feed in the production grammar-file
            // stream. Use a non-empty backing array with read=0 to exercise the
            // identical native iterator contract without making array allocation
            // semantics part of the test.
            rows.AddRange(StructuredGrammarIngest.FeedRawLinesForPipeline(iter, new byte[1], 0));
            return rows;
        }
        finally
        {
            NativeInterop.GrammarRowIterFree(iter);
        }
    }
}
