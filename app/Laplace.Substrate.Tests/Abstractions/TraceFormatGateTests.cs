using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// <c>System.Diagnostics.Trace.Trace{Warning,Error,Information}(format, args)</c> routes
/// through <c>string.Format</c>. A Serilog-style <c>{Name}</c> placeholder is not an argument
/// index, so the call throws <see cref="FormatException"/> the moment it fires.
///
/// Every one of these sites is a "log it and skip the bad input" path — which means the
/// handler for the error IS the crash. Twelve shipped at once; the chess.com PGN lane died
/// on <c>ChessPgnDecomposer</c>'s dropped-game warning, taking a 190k-game corpus with it,
/// and the same latent fault sat in the model, repo, ConceptNet and shared-spine lanes.
///
/// A single-argument call binds the <c>Trace.TraceWarning(string)</c> overload and does no
/// formatting, so braces are harmless there; this gate fires only when arguments follow.
/// </summary>
public sealed class TraceFormatGateTests
{
    private static readonly Regex TraceCall = new(
        @"Trace\.Trace\w*\((?<args>.*?)\);",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Any string literal — verbatim, interpolated, or plain.</summary>
    private static readonly Regex AnyLiteral = new(
        @"[\$@]*""(?:[^""\\]|\\.)*""",
        RegexOptions.Compiled);

    /// <summary>A plain (non-interpolated) literal — the only kind string.Format parses.</summary>
    private static readonly Regex PlainLiteral = new(
        @"(?<![\$])(?<!\$@)""(?:[^""\\]|\\.)*""",
        RegexOptions.Compiled);

    private static readonly Regex NamedPlaceholder = new(@"\{[A-Za-z_]", RegexOptions.Compiled);

    [Fact]
    public void TraceCalls_UseIndexedPlaceholdersOrInterpolation_NeverSerilogNames()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var appDir = Path.Combine(repoRoot, "app");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            // This gate's own doc comment names the banned idiom.
            if (Path.GetFileName(file).Equals("TraceFormatGateTests.cs", StringComparison.Ordinal)) continue;

            var text = File.ReadAllText(file);
            foreach (Match call in TraceCall.Matches(text))
            {
                var args = call.Groups["args"].Value;

                // string.Format only runs when arguments follow the format string. Strip every
                // literal, then a surviving comma means the (format, args) overload was bound.
                if (!AnyLiteral.Replace(args, string.Empty).Contains(',')) continue;

                foreach (Match lit in PlainLiteral.Matches(args))
                {
                    if (!NamedPlaceholder.IsMatch(lit.Value)) continue;
                    int line = text.AsSpan(0, call.Index).Count('\n') + 1;
                    violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{line}  {lit.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Trace.Trace*(format, args) formats through string.Format: use {0}/{1} or an "
            + "interpolated $\"\" string. A {Name} placeholder throws FormatException the moment "
            + "the path fires — and these are all failure paths:\n"
            + string.Join("\n", violations));
    }
}
