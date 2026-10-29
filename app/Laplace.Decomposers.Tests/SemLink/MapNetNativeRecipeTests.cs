using Xunit;

namespace Laplace.Decomposers.Tests.SemLink;

public sealed class MapNetNativeRecipeTests
{
    [Fact]
    public async Task BothMappingLayoutsUseNativeIdentifierCompositionWithoutGuessingAnotherWordNetEdition()
    {
        string directory = Path.Combine(Path.GetTempPath(), "laplace-mapnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string frames = Path.Combine(directory, "mapping_frame_synsets.txt");
        string units = Path.Combine(directory, "mapping_lus_synsets.txt");
        try
        {
            // Controlled compiler input, not a redistributed MapNet corpus excerpt.
            // Its WordNet 1.6 identifier must remain its own content identifier.
            await File.WriteAllTextAsync(frames, "Buildings\tn#01234567\n");
            await File.WriteAllTextAsync(units, "Buildings\thall.n\tn#01234567\n");
            string[] actual = await NativeCorpusAssertions.ClaimsAsync("mapnet", frames, units);
            string[] products = actual.Select(static line => string.Join('\t', line.Split('\t').Skip(3)))
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Buildings\tn#01234567", "[Buildings, hall.n]\tn#01234567",
                "Buildings\tidentifier vocabulary\tfnframe", "Buildings\tidentifier vocabulary\tfnframe" }
                .Order(StringComparer.Ordinal), products);
        }
        finally
        {
            File.Delete(frames);
            File.Delete(units);
            Directory.Delete(directory);
        }
    }
}
