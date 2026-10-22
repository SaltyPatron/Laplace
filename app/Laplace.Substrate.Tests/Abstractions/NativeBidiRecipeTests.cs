using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("cpu-topology-global")]
public sealed class NativeBidiRecipeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BidiResolutionKeepsNestedOrderedOperands(bool resolved)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string fixture = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            "bidi-character", "BidiCharacterTest.txt");
        var installed = NativeRecipeExecution.Configured();
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string scratch = Path.GetFullPath(Path.Combine(tempRoot, "laplace-bidi-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot) + Path.DirectorySeparatorChar, scratch,
            StringComparison.OrdinalIgnoreCase);
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES");
        try
        {
            // A small estate tests both compiler forms with the production
            // grammar and the production recipe, changing only its modifier.
            string source = Path.Combine(scratch, "unicode");
            string formats = Path.Combine(scratch, "formats");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(formats);
            File.WriteAllText(Path.Combine(scratch, "order"), "unicode\n");
            File.Copy(Path.Combine(installed.Recipes, "file.recipe"), Path.Combine(scratch, "file.recipe"));
            File.Copy(Path.Combine(installed.Recipes, "unicode", "source"), Path.Combine(source, "source"));
            File.Copy(Path.Combine(installed.Recipes, "formats", "lp_bidi_character.format"),
                Path.Combine(formats, "lp_bidi_character.format"));
            string recipe = File.ReadAllText(Path.Combine(installed.Recipes, "unicode", "bidi-character-tests.recipe"));
            if (resolved)
                recipe = recipe.Replace("thing resolution paragraph_level levels visual_order preserve",
                    "thing resolution resolved paragraph_level required levels required visual_order preserve",
                    StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(source, "bidi-character-tests.recipe"), recipe);
            var execution = new NativeRecipeExecution(installed.Executable, scratch, installed.Grammars,
                installed.Tier0, installed.Highway);
            Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", "17");
            using var output = new MemoryStream();
            using var error = new MemoryStream();
            int code = await execution.ExecuteAsync(new NativeRecipeRequest(NativeRecipeMode.Claims,
                "unicode", [fixture], Threads: 4), output, error);
            string stdout = Encoding.UTF8.GetString(output.ToArray());
            Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
            Assert.Contains("unresolved obligations: 0", stdout, StringComparison.Ordinal);

            static string Tuple(string field)
            {
                string[] values = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return values.Length == 1 ? values[0] : "[" + string.Join(", ", values) + "]";
            }
            var expected = File.ReadLines(fixture).Where(static line => line.Length > 0 && line[0] != '#')
                .Select(line =>
                {
                    string[] fields = line.Split('#', 2)[0].Split(';', StringSplitOptions.TrimEntries);
                    string text = string.Concat(fields[0].Split(' ').Select(static hex =>
                        char.ConvertFromUtf32(Convert.ToInt32(hex, 16))));
                    string[] result = fields[4].Length == 0
                        ? [fields[2], Tuple(fields[3])] : [fields[2], Tuple(fields[3]), Tuple(fields[4])];
                    return $"claim\t0\t1\t[{text}, {fields[1]}]\tUnicode Bidirectional Algorithm\t[{string.Join(", ", result)}]";
                }).ToArray();
            Assert.Equal(6, expected.Length);
            Assert.Equal(expected, stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
                .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal)));

            // X9 removes an isolated embedding control, leaving an empty order.
            // Keep that valid result, and reject glued level values such as x1.
            string edge = Path.Combine(scratch, "BidiCharacterTest.txt");
            File.WriteAllText(edge, "202A ; 0 ; 0 ; x ; \n");
            output.SetLength(0); error.SetLength(0);
            code = await execution.ExecuteAsync(new NativeRecipeRequest(NativeRecipeMode.Claims,
                "unicode", [edge], Threads: 2), output, error);
            stdout = Encoding.UTF8.GetString(output.ToArray());
            Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
            Assert.Contains("claim\t0\t1\t[\u202a, 0]\tUnicode Bidirectional Algorithm\t[0, x]", stdout,
                StringComparison.Ordinal);
            File.WriteAllText(edge, "202A 0061;0;0;x1;0\n");
            output.SetLength(0); error.SetLength(0);
            code = await execution.ExecuteAsync(new NativeRecipeRequest(NativeRecipeMode.Claims,
                "unicode", [edge], Threads: 2), output, error);
            Assert.NotEqual(0, code);
            Assert.Contains("does not parse completely", Encoding.UTF8.GetString(error.ToArray()), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", previous);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
}
