using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("cpu-topology-global")]
public sealed class NativeUnicodeTableTests
{
    [Theory]
    [InlineData("NamedSequences.txt")]
    [InlineData("NamedSequencesProv.txt")]
    [InlineData("RSIndex.txt")]
    public async Task NativeTablesRecoverEveryNamedSequenceAndIndexMember(string name)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            "unicode-tables", name);
        bool index = name == "RSIndex.txt";
        var expected = new List<string>();
        int largestRow = 0;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0) continue;
            string[] fields = line.Split(index ? '\t' : ';', StringSplitOptions.TrimEntries);
            string[] codepoints = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            largestRow = Math.Max(largestRow, codepoints.Length);
            static string Character(string hex) => char.ConvertFromUtf32(Convert.ToInt32(
                hex.StartsWith("U+", StringComparison.Ordinal) ? hex[2..] : hex, 16));
            if (index)
                expected.AddRange(codepoints.Select(hex => $"claim\t0\t1\t[kRSUnicode, {fields[0]}]\t{Character(hex)}"));
            else
                expected.Add($"claim\t0\t1\t{string.Concat(codepoints.Select(Character))}\tName of Sequence\t{fields[0]}");
        }
        if (index) Assert.True(largestRow > 256, "Exercise a real index row wider than a fixed operand buffer.");
        else Assert.Equal(name == "NamedSequences.txt" ? 7 : 0, expected.Count);

        string? previous = Environment.GetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", "17");
            using var output = new MemoryStream();
            using var error = new MemoryStream();
            int code = await NativeRecipeExecution.Configured().ExecuteAsync(
                new NativeRecipeRequest(NativeRecipeMode.Claims, "unicode", [path], Threads: 4), output, error);
            string stdout = Encoding.UTF8.GetString(output.ToArray());
            Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
            Assert.DoesNotMatch(@"unresolved obligations: [1-9]", stdout);
            Assert.Equal(expected, stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
                .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal)));
        }
        finally { Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", previous); }
    }

    [Theory]
    [InlineData("LinkBracket.txt", "Link_Bracket")]
    [InlineData("LinkEmail.txt", "Link_Email")]
    [InlineData("LinkTerm.txt", "Link_Term")]
    public async Task NativeLinkPropertiesRetainExplicitValuesAndIgnoreDeclaredDefaults(string name, string property)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            "unicode-tables", name);
        static string Characters(string range) => string.Concat(range.Split("..", StringSplitOptions.None)
            .Select(static hex => char.ConvertFromUtf32(Convert.ToInt32(hex, 16))));
        var expected = File.ReadLines(path).Select(static line => line.Split('#', 2)[0].Trim())
            .Where(static line => line.Length > 0).Select(line =>
            {
                string[] fields = line.Split(';', StringSplitOptions.TrimEntries);
                string suffix = property == "Link_Email" ? "" : "\t" +
                    (property == "Link_Bracket" ? Characters(fields[1]) : fields[1]);
                return $"claim\t0\t1\t{Characters(fields[0])}\t{property}{suffix}";
            }).ToArray();
        Assert.Equal(4, expected.Length);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.Claims, "unicode", [path], Threads: 4), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
        Assert.DoesNotMatch(@"unresolved obligations: [1-9]", stdout);
        Assert.Equal(expected, stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal)));
    }
}
