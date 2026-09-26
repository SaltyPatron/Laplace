using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// IngestSizing.MeasureBytesPerRecord: a sampled mean over a line-delimited file, which
/// returns the caller's default instead of throwing on unusable input, and the check of a
/// declared per-source bytes/record against its corpus.
/// </summary>
public sealed class IngestRecordSizeMeasurementTests
{
    private static string WriteLines(int count, int bytesEach)
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-rec-{Guid.NewGuid():N}.jsonl");
        var line = new string('x', bytesEach - 1);   // -1: the newline is the other byte
        File.WriteAllLines(path, Enumerable.Repeat(line, count));
        return path;
    }

    [Fact]
    public void Measures_TheMeanOfALineDelimitedFile()
    {
        var path = WriteLines(count: 500, bytesEach: 300);
        try
        {
            Assert.Equal(300, IngestSizing.MeasureBytesPerRecord(path, sampleRecords: 500));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StopsAtTheSampleSize_RatherThanReadingTheWholeFile()
    {
        // Sizing reads only the sample, never the whole file.
        var path = WriteLines(count: 5_000, bytesEach: 200);
        try
        {
            Assert.Equal(200, IngestSizing.MeasureBytesPerRecord(path, sampleRecords: 64));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/nonexistent/path/that/should/not/resolve.jsonl")]
    public void FallsBack_RatherThanThrowing_OnUnusableInput(string path)
    {
        Assert.Equal(
            IngestSizing.DefaultEstBytesPerRecord,
            IngestSizing.MeasureBytesPerRecord(path));
    }

    [Fact]
    public void FallsBack_OnAnEmptyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-empty-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Empty);
        try
        {
            Assert.Equal(4242, IngestSizing.MeasureBytesPerRecord(path, fallback: 4242));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BlankLinesAreSeparators_NotRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-blank-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "abcd\n\n\nabcd\n");   // two 5-byte records, two blanks
        try
        {
            Assert.Equal(5, IngestSizing.MeasureBytesPerRecord(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The declared Wiktionary bytes/record against a 20,000-record sample of its corpus.
    /// Vacuous where the vault is not mounted.
    /// </summary>
    [Fact]
    public void DeclaredWiktionaryRecordSize_IsCheckedAgainstTheRealCorpus()
    {
        const string corpus = "/vault/Data/Wiktionary/raw-wiktextract-data.jsonl";
        if (!File.Exists(corpus)) return;

        int measured = IngestSizing.MeasureBytesPerRecord(corpus, sampleRecords: 20_000);
        int declared = IngestSourceProfile.Wiktionary.EstBytesPerRecord;

        Assert.True(measured > 0, "measurement failed on a corpus that exists");
        // Declared must be within 1.5x of measured in either direction.
        double ratio = (double)declared / measured;
        Assert.True(ratio is > 0.667 and < 1.5,
            $"IngestSourceProfile.Wiktionary declares {declared} bytes/record but the corpus "
            + $"measures {measured} ({ratio:F2}x). Re-derive it -- this is the "
            + "per-worker memory denominator, so drift here costs the whole run.");
    }
}
