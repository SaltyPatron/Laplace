using System.Text;
using Laplace.Decomposers.Tests;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.UD.Tests;

/// <summary>
/// <see cref="IngestSourceProfile.UdSentence"/>'s compose-unit multiplier is the
/// denominator of the working-set record cap: under-declaring it admits more native
/// trees than the flush envelope models. These keep the declared constant bracketed
/// by the independent native-tree fan <see cref="UdContentForest"/> actually produces —
/// against the real corpus when LAPLACE_DATA_ROOT is present, and against a
/// representative synthetic treebank always.
/// </summary>
public sealed class UdComposeFanMeasurementTests
{
    static UdComposeFanMeasurementTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    private static UdToken Token(int id, string form, string? lemma = null, string misc = "_")
    {
        var f = Encoding.UTF8.GetBytes(form);
        var l = Encoding.UTF8.GetBytes(lemma ?? form);
        return new UdToken(
            id, id.ToString(), f, l, FormLemmaSame: lemma is null,
            "NOUN", "NN", [], 0, "dep", "_", misc);
    }

    private static long MeasureFan(IEnumerable<UdSentence> sentences, out long count)
    {
        long trees = 0;
        count = 0;
        foreach (var s in sentences)
        {
            using var forest = UdContentForest.Build(s);
            trees += forest.Trees.Count;
            count++;
        }
        return trees;
    }

    [Fact]
    public void SentenceTreeSuppliesExactTokenFormsWithoutStandaloneTrees()
    {
        var sentence = new UdSentence(
            Encoding.UTF8.GetBytes("alpha beta gamma"),
            [Token(1, "alpha"), Token(2, "beta"), Token(3, "gamma")],
            [],
            3);

        using var forest = UdContentForest.Build(sentence);

        Assert.Single(forest.Trees);
    }

    [Fact]
    public void DeclaredUnits_BracketTheSyntheticFan()
    {
        // 15 tokens, ~1/3 with distinct lemmas, one Gloss, one MWT — the ordinary
        // treebank shape rather than the degenerate ones.
        var sentences = Enumerable.Range(0, 200).Select(n =>
        {
            var toks = new List<UdToken>();
            var forms = new List<string>();
            for (int i = 1; i <= 15; i++)
            {
                bool lemmaDiffers = i % 3 == 0;
                string form = $"word{n}_{i}";
                forms.Add(form);
                toks.Add(Token(
                    i, form,
                    lemmaDiffers ? $"lemma{n}_{i}" : null,
                    i == 2 ? "Gloss=meaning|SpaceAfter=No" : "_"));
            }
            return new UdSentence(
                Encoding.UTF8.GetBytes(string.Join(' ', forms)),
                toks,
                [new UdMwt(1, 2, Encoding.UTF8.GetBytes($"mwt{n}"))],
                15);
        });

        long trees = MeasureFan(sentences, out long count);
        double mean = (double)trees / count;
        int declared = IngestSourceProfile.UdSentence.EstComposeUnitsPerRecord;
        // Never under-declare (that re-admits the unbounded working set); never
        // over-declare past 4x (that starves batches the way units=64 did Wiktionary).
        Assert.True(declared >= mean * 0.9,
            $"UdSentence declares {declared} compose units but the synthetic fan measures {mean:F1}");
        Assert.True(declared <= mean * 4,
            $"UdSentence declares {declared} compose units against a measured {mean:F1} — cap starvation");
    }

    [Fact]
    public void ExactTreeCapacityMeasuresARecordWhoseSourceTextCannotSupplyTokenForms()
    {
        var tokens = Enumerable.Range(1, 24)
            .Select(i => Token(i, $"detached_{i}", i % 4 == 0 ? $"lemma_{i}" : null))
            .ToList();
        var sentence = new UdSentence(null, tokens, [], 24);

        using var forest = UdContentForest.Build(sentence);
        long measuredBytes = forest.Trees
            .Where(static tree => tree is not null)
            .Sum(static tree =>
                (long)tree!.Capacity * MemoryTopology.TierTreeResidentBytesPerCapacity);

        Assert.True(measuredBytes > 0);
        Assert.True(forest.Trees.Count > IngestSourceProfile.UdSentence.EstComposeUnitsPerRecord,
            "fixture must exceed the profile fan so exact capacity, not a fan multiplier, is exercised");
    }

    [Fact]
    public async Task DeclaredUnits_BracketTheRealCorpusFan_WhenPresent()
    {
        var root = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        var dir = Path.Combine(root, "UD-Treebanks");
        if (!Directory.Exists(dir)) return;
        var file = Directory.EnumerateFiles(dir, "*.conllu", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
        if (file is null) return;

        var sentences = new List<UdSentence>();
        await foreach (var s in UdConlluParser.ParseSentencesAsync(file))
        {
            sentences.Add(s);
            if (sentences.Count >= 2_000) break;
        }
        if (sentences.Count < 100) return;

        long trees = MeasureFan(sentences, out long count);
        double mean = (double)trees / count;
        int declared = IngestSourceProfile.UdSentence.EstComposeUnitsPerRecord;
        Console.WriteLine(
            $"UD real content forest: {trees:N0} independent trees / {count:N0} sentences = {mean:F2}");

        Assert.True(declared >= mean * 0.9,
            $"UdSentence declares {declared} compose units but {Path.GetFileName(file)} measures {mean:F1}");
        Assert.True(declared <= mean * 6,
            $"UdSentence declares {declared} compose units against a measured {mean:F1} — cap starvation");
    }
}
