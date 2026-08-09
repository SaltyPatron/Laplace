using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Pins the canonical-key builders (#275) and gates the literals from coming back.
///
/// Law: ids are NEVER constructed outside the system. A hand-typed
/// "substrate/source/WordnetDecomposer/v1" (note the case) is not a compile error and
/// not a runtime error — it silently mints a different entity that no query will ever
/// join to. The builder plus the gate below is what makes that class of typo impossible.
/// </summary>
public sealed class SubstrateCanonicalIdsTests
{
    // Byte-for-byte agreement with the SQL surface. source_id('WordNetDecomposer') and
    // canonical_id('substrate/source/WordNetDecomposer/v1') both resolve to this on the
    // live DB, so C# and SQL cannot drift apart without failing here.
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
        Assert.Equal("substrate/trust_class/AcademicCurated/v1",
            SubstrateCanonicalKeys.TrustClass("AcademicCurated"));
        Assert.Equal("substrate/pos/probationary/framenet/IDIO/v1",
            SubstrateCanonicalKeys.PosProbationary("framenet", "IDIO"));
        Assert.Equal("substrate/test/reg/a", SubstrateCanonicalKeys.Of("test", "reg", "a"));
        Assert.Equal("substrate/test/word/v1", SubstrateCanonicalKeys.OfVersioned("test", "word"));
    }

    // Case and spelling matter: these must be DIFFERENT ids, which is exactly why the
    // literals were dangerous.
    [Fact]
    public void NearMissesAreDistinctIds()
    {
        Assert.NotEqual(SubstrateCanonicalIds.Source("WordNetDecomposer"),
                        SubstrateCanonicalIds.Source("WordnetDecomposer"));
        Assert.NotEqual(SubstrateCanonicalIds.Source("WordNetDecomposer"),
                        SubstrateCanonicalIds.TrustClass("WordNetDecomposer"));
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
    /// The gate: no raw substrate canonical-key literal anywhere in app/, except inside
    /// the builder that defines the shape. Without this the 152 literals grow back one
    /// convenient copy-paste at a time.
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
