using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class NativeRecipeExecutionTests
{
    [Theory]
    [InlineData(NativeRecipeMode.Plan)]
    [InlineData(NativeRecipeMode.NoLoad)]
    [InlineData(NativeRecipeMode.Claims)]
    public async Task CanonicalEngineReadsRealPropbankThroughTheManagedProcessBoundary(NativeRecipeMode mode)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out var repository));
        string workspace = Directory.GetParent(repository)!.FullName;
        var execution = NativeRecipeExecution.Configured();
        string fixture = Path.Combine(workspace, "Laplace-Engine", "tests", "fixtures",
            "highway-identities", "propbank", "abandon.xml");
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await execution.ExecuteAsync(new NativeRecipeRequest(mode, "propbank", [fixture],
            ConnectionInfo: "host=127.0.0.1 port=1 dbname=unavailable connect_timeout=1", Threads: 2), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        string stderr = Encoding.UTF8.GetString(error.ToArray());
        Assert.True(code == 0, $"Native mode {mode} exited {code}: {stderr}\n{stdout}");
        if (mode == NativeRecipeMode.Claims)
        {
            // These are three different attribute-bearing rolesets from the real
            // file, recovered by the same compiled Engine recipe used for loading.
            Assert.Contains("abandon.01", stdout, StringComparison.Ordinal);
            Assert.Contains("abandon.02", stdout, StringComparison.Ordinal);
            Assert.Contains("abandon.03", stdout, StringComparison.Ordinal);
            Assert.Contains("leave behind", stdout, StringComparison.Ordinal);
        }
        else if (mode == NativeRecipeMode.Plan)
            Assert.Matches(@"propbank-frames\s+1 files", stdout);
        else
        {
            Assert.Contains("mismatched 0", stdout, StringComparison.Ordinal);
            Assert.Matches(@"roleset\.name\s+3(?:\r?\n|$)", stdout);
            Assert.Contains("unresolved obligations: 0", stdout, StringComparison.Ordinal);
        }
    }
    [Theory]
    [InlineData("line-pairs", true)]
    [InlineData("line-pairs-incomplete", false)]
    public async Task NativeMultilineGrammarPreservesFragmentsAndRejectsUnpairedInput(string fixture, bool valid)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out var repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            fixture, "LinkFormattingTest.txt");
        var execution = NativeRecipeExecution.Configured();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await execution.ExecuteAsync(new NativeRecipeRequest(NativeRecipeMode.Claims, "unicode", [path],
            ConnectionInfo: "host=127.0.0.1 port=1 dbname=unavailable connect_timeout=1", Threads: 2), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        string stderr = Encoding.UTF8.GetString(error.ToArray());
        if (!valid)
        {
            Assert.NotEqual(0, code);
            Assert.Contains("does not parse completely", stderr, StringComparison.Ordinal);
            return;
        }
        Assert.True(code == 0, stderr + stdout);
        var claims = stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[]
        {
            "claim\t0\t1\thttps://example.com/%CE%B1\tnormalizes to\thttps://example.com/α",
            "claim\t0\t1\thttps://example.com#%CE%B1\tnormalizes to\thttps://example.com#α",
        }, claims);
        Assert.Contains("unresolved obligations: 0", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeNormalizationRecipeRetainsEveryNonIdentityResultAndItsForm()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out var repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            "normalization", "NormalizationTest.txt");
        static string Characters(string codepoints) => string.Concat(codepoints.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static hex => char.ConvertFromUtf32(Convert.ToInt32(hex, 16))));
        string[] forms = ["NFC", "NFD", "NFKC", "NFKD"];
        var expected = new List<string>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] is '#' or '@') continue;
            var fields = line.Split('#', 2)[0].Split(';', StringSplitOptions.TrimEntries);
            string source = Characters(fields[0]);
            for (int form = 0; form < forms.Length; form++)
            {
                string result = Characters(fields[form + 1]);
                if (result != source) expected.Add($"claim\t0\t1\t{source}\tnormalizes to {forms[form]}\t{result}");
            }
        }
        Assert.Equal(16, expected.Count);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.Claims, "unicode", [path], Threads: 2), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
        Assert.Equal(expected, stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal)));
        Assert.Contains("unresolved obligations: 0", stdout, StringComparison.Ordinal);
    }

}
