using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The generic ETL lane is the substrate's declared "one way" to ingest a tabular source:
/// declare a row in <see cref="EtlManifest"/>, and <see cref="EtlDecomposer"/> +
/// <see cref="StructuredGrammarIngest"/> carry it — no per-source decomposer. Nothing
/// reaches it today, and nothing said so.
///
/// <see cref="EtlSource.IsRoutableViaEtl"/> = !HasDedicatedDecomposer AND IsComplete, where
/// IsComplete = Modality.GrammarReady AND (NodeEdgeMap.Count > 0 OR a registered witness).
/// Three independent facts, each alone sufficient to make it false for EVERY row:
///   1. <c>new EdgeRule(...)</c> appears nowhere in the repo, so NodeEdgeMap is always empty.
///   2. <c>EtlWitnessFactory.Register</c> is called nowhere, so IsRegistered is always false.
///   3. 19 of the 22 rows declare GrammarReady: false outright; the 3 that do not
///      (tatoeba, wiktionary, omw) all declare hasDedicatedDecomposer: true.
///
/// Consequence: <c>IngestDispatchTable.TryDispatch</c>'s ETL branch and the
/// <c>EtlManifest.Names.Where(IsRoutable)</c> term in RegisteredKeys are both unreachable,
/// and the <c>GrammarReady</c> flag on every manifest row decides nothing. That is a
/// direction to finish or delete, not a bug to patch — but it must not stay silent, because
/// the manifest reads like a routing table and routes nothing.
///
/// This test pins the count. It fails the day a row becomes routable (finish the lane: drop
/// the dead-branch note here) and the day the lane is deleted (delete this file with it).
/// </summary>
public sealed class EtlLaneReachabilityTests
{
    [Fact]
    public void EtlManifest_HasNoRoutableRow_AndSaysSoOutLoud()
    {
        var routable = EtlManifest.Names.Where(EtlManifest.IsRoutable).OrderBy(n => n).ToList();

        Assert.True(routable.Count == 0,
            "A manifest row is now routable via the generic ETL lane: "
            + string.Join(", ", routable)
            + ". That is the intended end state — remove this gate and the "
            + "\"unreachable\" notes on IngestDispatchTable.TryDispatch's ETL branch.");
    }

    [Fact]
    public void EveryManifestRow_FailsIsCompleteForARecordedReason()
    {
        var unexplained = new List<string>();
        foreach (var name in EtlManifest.Names)
        {
            var row = EtlManifest.Get(name);
            bool grammarBlocked = !row.Modality.GrammarReady;
            bool noWiring = row.NodeEdgeMap.Count == 0 && !EtlWitnessFactory.IsRegistered(row.Name);
            bool dedicated = row.HasDedicatedDecomposer;
            if (!grammarBlocked && !noWiring && !dedicated)
                unexplained.Add(name);
        }

        Assert.True(unexplained.Count == 0,
            "Rows that are wired for the ETL lane but still not routed: "
            + string.Join(", ", unexplained));
    }

    /// <summary>
    /// GrammarReady is declared 22 times and read by exactly one expression
    /// (<see cref="EtlSource.IsComplete"/>) whose only consumer is the unreachable branch
    /// above. Until the lane is reachable, the flag is documentation wearing a bool's
    /// clothes; this records the shape so a future reader does not trust it as a switch.
    /// </summary>
    [Fact]
    public void GrammarReadyFlag_CurrentlyGovernsNothingReachable()
    {
        var ready = EtlManifest.Names
            .Where(n => EtlManifest.Get(n).Modality.GrammarReady)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "omw", "tatoeba", "wiktionary" }, ready);
        Assert.All(ready, n => Assert.True(EtlManifest.Get(n).HasDedicatedDecomposer,
            $"'{n}' is GrammarReady without a dedicated decomposer — it should now route "
            + "via EtlDecomposer, which means the lane is live and this gate is stale."));
    }
}
