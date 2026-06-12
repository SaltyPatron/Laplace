using System.Globalization;
using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Parity probe: ContentEmitter.RootId (C# slow-path re-derivation) MUST equal the root
/// returned by the native content witness batch for every surface a decomposer references.
/// Sweeps the surfaces of the first WordNet data-0 unit — the unit whose referential proof
/// trips on the ghost id 1217D71BEBFC827E4D5FCA1EFB41B0B1.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class RootIdNativeParityProbe
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/RootIdParity/v1");

    private const string DataNoun = @"D:\vault\Data\Wordnet\WordNet-3.0\dict\data.noun";

    [SkippableFact]
    public void WordNetData0_Surfaces_RootIdMatchesNativeBatchRoot()
    {
        Skip.IfNot(File.Exists(DataNoun), "WordNet data not present on this machine.");

        var mismatches = new List<string>();
        int synsets = 0;
        using var stage = IntentStage.New(64);

        foreach (var line in File.ReadLines(DataNoun))
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            if (++synsets > 256) break;

            foreach (var surface in SurfacesOf(line))
            {
                if (string.IsNullOrEmpty(surface)) continue;
                var slow = Laplace.Decomposers.Abstractions.ContentEmitter.RootId(surface);
                bool ok = Laplace.Decomposers.Abstractions.ContentWitnessBatch.TryAddToIntentStage(
                    stage, Encoding.UTF8.GetBytes(surface), Src, out var fast);

                if (slow is null && !ok) continue;
                if (slow is null || !ok || !slow.Value.EqualsBytewise(fast))
                    mismatches.Add(
                        $"surface={surface.Replace("\n", "\\n")}|slow={(slow is null ? "null" : Convert.ToHexString(slow.Value.ToBytes()))}|fast={(ok ? Convert.ToHexString(fast.ToBytes()) : "fail")}");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"RootId/native-batch root mismatches ({mismatches.Count}):\n" + string.Join("\n", mismatches.Take(10)));
    }

    /// <summary>
    /// Pins the 2026-06-12 wordnet ghost: raw ss_type through the UPOS resolver mints a
    /// probationary id nothing emits. PosWordNet must route the WORDNET tagset mode, whose
    /// closed set lands on the seeded canonicals.
    /// </summary>
    [Fact]
    public void Ghost_Is_WordNet_SsType_Misrouted_Through_UposResolver()
    {
        var misrouted = Laplace.Decomposers.Abstractions.NativeAttestation.ResolvePos(
            "n", Laplace.Decomposers.Abstractions.PosReference.PosTagset.Upos, out bool probationary);
        var lawful = Laplace.Decomposers.Abstractions.NativeAttestation.ResolvePos(
            "n", Laplace.Decomposers.Abstractions.PosReference.PosTagset.WordNet, out bool lawfulProbationary);
        Assert.Equal("1217D71BEBFC827E4D5FCA1EFB41B0B1", Convert.ToHexString(misrouted.ToBytes()));
        Assert.True(probationary);
        Assert.False(lawfulProbationary);
        Assert.NotEqual(Convert.ToHexString(misrouted.ToBytes()), Convert.ToHexString(lawful.ToBytes()));

        // The WORDNET tagset mode resolves every ss_type onto a seeded canonical.
        foreach (var (ssType, upos) in new[] { ("n", "NOUN"), ("v", "VERB"), ("a", "ADJ"), ("s", "ADJ"), ("r", "ADV") })
        {
            Assert.Equal(
                Laplace.Decomposers.Abstractions.PosReference.CanonicalId(upos),
                Laplace.Decomposers.Abstractions.PosReference.Resolve(
                    ssType, Laplace.Decomposers.Abstractions.PosReference.PosTagset.WordNet));
        }
    }

    /// <summary>The surfaces EmitSynsetAttestations references: lemmas (surfaced), def, examples.</summary>
    private static IEnumerable<string> SurfacesOf(string line)
    {
        var fields = line.Split(' ');
        int wcnt = int.Parse(fields[3], NumberStyles.HexNumber);
        for (int i = 0; i < wcnt; i++)
            yield return fields[4 + 2 * i].Replace('_', ' ');

        int bar = line.IndexOf('|');
        if (bar < 0) yield break;
        var gloss = line[(bar + 1)..].Trim();
        yield return gloss;

        // def = text before the first quoted example; examples = quoted spans
        int q = gloss.IndexOf('"');
        if (q >= 0)
        {
            yield return gloss[..q].TrimEnd().TrimEnd(';').TrimEnd();
            var rest = gloss[q..];
            var parts = rest.Split('"');
            for (int i = 1; i < parts.Length; i += 2)
                yield return parts[i];
        }
    }
}
