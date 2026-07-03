using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Trait("Tier", "fast")]
[Collection("GrammarPerfcache")]
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
        File.WriteAllText(Path.Combine(_dir, IliMap.MapFileName),
            "i12345\t00012345-n\n" +
            "i23456\t02244956-v\n" +
            "i34567\t02164298-s\n");

        _prevCiliDir = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR");
        Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", _dir);
        SourceEntityIdConventions.ResetIliMapCacheForTests();
    }

    [Theory]
    [InlineData("30-00012345-n")]
    [InlineData("ili-30-02244956-v")]
    [InlineData("v#02244956")]
    [InlineData("30-02164298-a")]
    [InlineData("wn:synset/v#02244956")]
    [InlineData("30-99999999-n")]
    [InlineData("not-a-key")]
    [InlineData("NULL")]
    [InlineData("")]
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
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

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
    [InlineData("give%2:40:00::")]
    [InlineData("hot_dog%1:13:00::")]
    [InlineData("?!give%2:40:00::")]
    [InlineData("  cat%1:05:00::  ")]
    [InlineData("nokey")]
    [InlineData("%2:40:00")]
    [InlineData("give%2:40")]
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
    [InlineData("  Cooking  ")]
    [InlineData("Apply_heat/cook.v")]
    [InlineData("Causation")]
    [InlineData("")]
    [InlineData("   ")]
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
