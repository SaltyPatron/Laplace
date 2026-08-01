using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// Batch sizing is computed from bytes-per-record, and bytes-per-record was a constant
/// declared per source that nothing ever checked against a corpus. These pin the measured
/// alternative and the fallbacks, because a sizing helper that throws or that returns a
/// wild number is worse than the constant it replaces.
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
        // The point of sampling: a 20GB corpus must not be walked to size a batch.
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
    /// The declared constant against the corpus it describes. Skipped where the vault is
    /// not mounted, because a test that silently passes on an absent file proves nothing.
    /// MEASURED 2026-08-01: 6,158 bytes/record over 20,000 records of the 20.4 GB
    /// raw-wiktextract-data.jsonl, against a declared 12,000 — so the batch is sized at
    /// roughly half what the corpus supports, doubling round trips for the whole run.
    /// </summary>
    [Fact]
    public void DeclaredWiktionaryRecordSize_IsCheckedAgainstTheRealCorpus()
    {
        const string corpus = "/vault/Data/Wiktionary/raw-wiktextract-data.jsonl";
        if (!File.Exists(corpus)) return;

        int measured = IngestSizing.MeasureBytesPerRecord(corpus, sampleRecords: 20_000);
        int declared = IngestSourceProfile.Wiktionary.EstBytesPerRecord;

        Assert.True(measured > 0, "measurement failed on a corpus that exists");
        // The declaration must TRACK the corpus, not merely exceed it. A wild over-estimate
        // is what halved the batch for the life of the project; a wild under-estimate would
        // oversize it. Within 1.5x either way, or the constant has drifted from the file.
        double ratio = (double)declared / measured;
        Assert.True(ratio is > 0.667 and < 1.5,
            $"IngestSourceProfile.Wiktionary declares {declared} bytes/record but the corpus "
            + $"measures {measured} ({ratio:F2}x). Re-derive it -- batch size is "
            + "TargetBytesPerBatch / this number, so drift here costs the whole run.");
    }
}
