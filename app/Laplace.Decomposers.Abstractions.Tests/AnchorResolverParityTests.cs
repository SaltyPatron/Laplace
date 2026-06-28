using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The parity oracle for the native anchor port. Both the C# orchestrator (SourceEntityIdConventions
/// .ResolveSynsetAnchor → ConceptAnchor.SynsetId → ContentEmitter.RootId) and the native resolver
/// (lp_resolve_synset_anchor in etl_anchor.c) read the SAME ILI map file and must produce BIT-IDENTICAL
/// anchor ids — otherwise the native ETL path would silently diverge from the witnessed graph. Uses a
/// tiny temp map (no corpus dependency) so it runs in the fast tier and never skips.
/// (Sense-key fallback and the language path are later increments and are intentionally not asserted.)
/// </summary>
[Trait("Tier", "fast")]
[Collection("GrammarPerfcache")]   // loads the codepoint perfcache that native laplace_content_root_id needs
public sealed class AnchorResolverParityTests : IDisposable
{
    private const string Lib = "laplace_core";

    [DllImport(Lib, EntryPoint = "lp_ili_map_load")]
    private static extern IntPtr LpIliMapLoad([MarshalAs(UnmanagedType.LPUTF8Str)] string tabPath);

    [DllImport(Lib, EntryPoint = "lp_ili_map_free")]
    private static extern void LpIliMapFree(IntPtr map);

    [DllImport(Lib, EntryPoint = "lp_resolve_synset_anchor")]
    private static extern int LpResolveSynsetAnchor(IntPtr map, byte[] raw, nuint n, out Hash128 outId);

    private readonly string _dir;
    private readonly string? _prevCiliDir;

    public AnchorResolverParityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "laplace_anchor_parity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // ili-map-pwn30.tab rows: "iN \t offset-pos". Includes a satellite (-s) to prove the a/s collapse
        // travels end-to-end, and an offset reachable via both the MCR and MapNet key forms.
        File.WriteAllText(Path.Combine(_dir, IliMap.MapFileName),
            "i12345\t00012345-n\n" +
            "i23456\t02244956-v\n" +
            "i34567\t02164298-s\n");

        _prevCiliDir = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR");
        Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", _dir);
        SourceEntityIdConventions.ResetIliMapCacheForTests();
    }

    [Theory]
    [InlineData("30-00012345-n")]              // MCR with version prefix → i12345
    [InlineData("ili-30-02244956-v")]          // ili- prefix → i23456
    [InlineData("v#02244956")]                 // MapNet form → same i23456 anchor
    [InlineData("30-02164298-a")]              // OMW writes satellites as -a → resolves the -s entry i34567
    [InlineData("wn:synset/v#02244956")]       // WN-RDF tail stripped at last '/'
    [InlineData("30-99999999-n")]              // parses but absent from the map → both null
    [InlineData("not-a-key")]                  // no valid ss_type → both null
    [InlineData("NULL")]                       // rejected by both
    [InlineData("")]                           // rejected by both
    public void NativeResolver_MatchesCSharp(string key)
    {
        IntPtr map = LpIliMapLoad(Path.Combine(_dir, IliMap.MapFileName));
        Assert.NotEqual(IntPtr.Zero, map);
        try
        {
            Hash128? expected = SourceEntityIdConventions.ResolveSynsetAnchor(key);
            byte[] raw = Encoding.UTF8.GetBytes(key);
            int rc = LpResolveSynsetAnchor(map, raw, (nuint)raw.Length, out Hash128 got);

            if (expected is null)
            {
                Assert.Equal(0, rc);
            }
            else
            {
                Assert.Equal(1, rc);
                Assert.Equal(expected.Value, got);
            }
        }
        finally
        {
            LpIliMapFree(map);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", _prevCiliDir);
        SourceEntityIdConventions.ResetIliMapCacheForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}

/// <summary>
/// Parity oracle for the sense and category native resolvers (no ILI map needed — pure normalize + hash).
/// lp_resolve_sense_anchor must equal SenseAnchor.Id and lp_resolve_category_anchor must equal
/// CategoryAnchor.Id, bit-identically, so a native bespoke witness can reference the exact same anchors.
/// </summary>
[Trait("Tier", "fast")]
[Collection("GrammarPerfcache")]
public sealed class SenseCategoryAnchorParityTests
{
    private const string Lib = "laplace_core";

    [DllImport(Lib, EntryPoint = "lp_resolve_sense_anchor")]
    private static extern int LpResolveSenseAnchor(byte[] raw, nuint n, out Hash128 outId);

    [DllImport(Lib, EntryPoint = "lp_resolve_category_anchor")]
    private static extern int LpResolveCategoryAnchor(byte[] raw, nuint n, out Hash128 outId);

    [Theory]
    [InlineData("give%2:40:00::")]    // trailing fields dropped -> give%2:40:00
    [InlineData("hot_dog%1:13:00::")] // lemma '_' -> ' '
    [InlineData("?!give%2:40:00::")]  // leading ?/! stripped
    [InlineData("  cat%1:05:00::  ")] // outer whitespace trimmed
    [InlineData("nokey")]             // no '%' -> null
    [InlineData("%2:40:00")]          // '%' first -> null
    [InlineData("give%2:40")]         // < 3 fields -> null
    [InlineData("")]
    public void NativeSenseResolver_MatchesCSharp(string key)
    {
        Hash128? expected = SenseAnchor.Id(key);
        byte[] raw = Encoding.UTF8.GetBytes(key);
        int rc = LpResolveSenseAnchor(raw, (nuint)raw.Length, out Hash128 got);
        AssertParity(expected, rc, got);
    }

    [Theory]
    [InlineData("Motion")]
    [InlineData("  Cooking  ")]          // trimmed
    [InlineData("Apply_heat/cook.v")]    // FrameNet LU key — underscores/slash kept verbatim
    [InlineData("Causation")]
    [InlineData("")]
    [InlineData("   ")]                  // whitespace only -> null
    public void NativeCategoryResolver_MatchesCSharp(string key)
    {
        Hash128? expected = CategoryAnchor.Id(key);
        byte[] raw = Encoding.UTF8.GetBytes(key);
        int rc = LpResolveCategoryAnchor(raw, (nuint)raw.Length, out Hash128 got);
        AssertParity(expected, rc, got);
    }

    private static void AssertParity(Hash128? expected, int rc, Hash128 got)
    {
        if (expected is null)
        {
            Assert.Equal(0, rc);
        }
        else
        {
            Assert.Equal(1, rc);
            Assert.Equal(expected.Value, got);
        }
    }
}
