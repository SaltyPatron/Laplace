using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Pins the canonical-key builders and forbids raw canonical-key literals elsewhere. A
/// hand-typed key with a different spelling or case is a different content id that nothing
/// joins to, so every key goes through the builder.
/// </summary>
public sealed class SubstrateCanonicalIdsTests
{
    // The id SQL's source_id('WordNetDecomposer') and
    // realize.canonical_id('substrate/source/WordNetDecomposer/v1') resolve to; the C#
    // builder must produce the same bytes.
    private const string WordNetSourceIdHex = "4b1ee33be3034910df7629b2948cde35";

    private static string Hex(Hash128 h) => Convert.ToHexStringLower(h.ToBytes());

    [Fact]
    public void SourceKeyMatchesTheSqlSurface()
    {
        Assert.Equal("substrate/source/WordNetDecomposer/v1",
            SubstrateCanonicalKeys.Source("WordNetDecomposer"));
        Assert.Equal(WordNetSourceIdHex, Hex(SubstrateCanonicalIds.Source("WordNetDecomposer")));
        Assert.Equal(Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1"),
            SubstrateCanonicalIds.Source("WordNetDecomposer"));
    }

    [Fact]
    public void KeyShapesAreExact()
    {
        Assert.Equal("substrate/test/reg/a", SubstrateCanonicalKeys.Of("test", "reg", "a"));
        Assert.Equal("substrate/test/word/v1", SubstrateCanonicalKeys.OfVersioned("test", "word"));
    }

    // Case and spelling change the content, so these are different ids.
    [Fact]
    public void NearMissesAreDistinctIds()
    {
        Assert.NotEqual(SubstrateCanonicalIds.Source("WordNetDecomposer"),
                        SubstrateCanonicalIds.Source("WordnetDecomposer"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has/slash")]
    public void MalformedSegmentsThrowInsteadOfMintingAWrongId(string bad)
    {
        Assert.Throws<ArgumentException>(() => SubstrateCanonicalKeys.Source(bad));
        Assert.Throws<ArgumentException>(() => SubstrateCanonicalKeys.Of("test", bad));
    }

    [Fact]
    public void EmptySegmentListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SubstrateCanonicalKeys.Of());
    }

    /// <summary>
    /// No raw substrate canonical-key literal appears anywhere in app/ outside the builder
    /// that defines the shape.
    /// </summary>
    [Fact]
    public void NoRawCanonicalKeyLiteralsOutsideTheBuilder()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var appDir = Path.Combine(repoRoot, "app");
        var literal = new Regex("OfCanonical\\s*\\(\\s*\"substrate/", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(appDir, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (rel.EndsWith("Core/SubstrateCanonicalIds.cs", StringComparison.Ordinal)) continue;
            if (rel.EndsWith("Abstractions/SubstrateCanonicalIdsTests.cs", StringComparison.Ordinal)) continue;

            var text = File.ReadAllText(file);
            if (literal.IsMatch(text)) offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "Raw substrate canonical-key literals found — route them through " +
            "SubstrateCanonicalIds/SubstrateCanonicalKeys so a typo cannot silently mint a " +
            "different entity:\n  " + string.Join("\n  ", offenders));
    }
}
