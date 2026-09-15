using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class CutechessOpeningInputTests
{
    private const string Epd = "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - id \"operator selection\";";

    [Fact]
    public void SelectedEpdIsPreservedAndInventoryDistinguishesAvailableFromRequested()
    {
        using var fixture = new OpeningFixture();
        byte[] original = System.Text.Encoding.UTF8.GetBytes($"\n{Epd}\r\n\r\n{Epd}\n{Epd}\n");
        File.WriteAllBytes(fixture.Input, original);
        DateTime modified = File.GetLastWriteTimeUtc(fixture.Input);

        Assert.True(CutechessRunner.TryPrepareOpeningSuite(
            fixture.Options, out var path, out var requested, out var available, out var error), error);

        Assert.Equal(fixture.Input, path);
        Assert.Equal(2, requested);
        Assert.Equal(3, available);
        Assert.Equal(original, File.ReadAllBytes(fixture.Input));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fixture.Input));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "openings.epd")));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData(" \r\n\t\n", 0)]
    [InlineData(Epd, 1)]
    public void MissingEmptyAndInsufficientSelectedInputsFailWithoutCreationOrReplacement(string? contents, int available)
    {
        using var fixture = new OpeningFixture();
        if (contents is not null) File.WriteAllText(fixture.Input, contents);

        Assert.False(CutechessRunner.TryPrepareOpeningSuite(
            fixture.Options, out var path, out var requested, out var actualAvailable, out var error));

        Assert.Equal(fixture.Input, path);
        Assert.Equal(2, requested);
        Assert.Equal(available, actualAvailable);
        Assert.Contains("selected EPD input", error);
        if (contents is null) Assert.False(File.Exists(fixture.Input));
        else Assert.Equal(contents, File.ReadAllText(fixture.Input));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "openings.epd")));
    }

    [Fact]
    public void RelativeSelectionIsResolvedBeforeCutechessChangesItsWorkingDirectory()
    {
        using var fixture = new OpeningFixture();
        File.WriteAllText(fixture.Input, $"{Epd}\n{Epd}\n");
        var options = fixture.Options with
        {
            OpeningsFile = Path.GetRelativePath(Environment.CurrentDirectory, fixture.Input)
        };

        Assert.True(CutechessRunner.TryPrepareOpeningSuite(
            options, out var path, out _, out _, out var error), error);

        Assert.Equal(fixture.Input, path);
        Assert.Contains("file=" + fixture.Input, CutechessRunner.BuildArguments(options, "uci", "sf"));
    }

    [Fact]
    public void SelectedEpdCannotBeUsedAsThePgnOutput()
    {
        using var fixture = new OpeningFixture();
        string original = $"{Epd}\n{Epd}\n";
        File.WriteAllText(fixture.Input, original);

        Assert.False(CutechessRunner.TryPrepareOpeningSuite(
            fixture.Options with { PgnOut = fixture.Input }, out _, out _, out _, out var error));

        Assert.Contains("must differ from the PGN output", error);
        Assert.Equal(original, File.ReadAllText(fixture.Input));
    }

    private sealed class OpeningFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "laplace-opening-input-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Directory, "selected.epd");
        public CutechessOptions Options => new() { Rounds = 4, OpeningsFile = Input, PgnOut = Path.Combine(Directory, "games.pgn") };
        public OpeningFixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
