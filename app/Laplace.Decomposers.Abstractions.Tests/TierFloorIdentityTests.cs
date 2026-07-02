using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Tier is a FLOOR, not identity (CLAUDE.md / 05_Substrate_Invariants Rule
/// #1b): same content = same hash at every tier; `tier` records the lowest
/// form only. A one-word content unit IS the word — "dog" ingested alone
/// must not mint a separate sentence/document entity wrapping the word;
/// its root id must equal the word's id.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class TierFloorIdentityTests
{
    [Fact]
    public void OneWordContent_TiersAboveWord_CarryTheWordId_NoWrapperEntities()
    {
        using var tree = IntentStage.BuildContentTree(Encoding.UTF8.GetBytes("dog"));
        Assert.NotNull(tree);

        Hash128? wordId = null;
        var aboveWord = new List<(int Tier, Hash128 Id)>();
        for (uint i = 0; i < tree!.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            if (node.Tier == 2) wordId ??= node.Id;
            if (node.Tier > 2) aboveWord.Add((node.Tier, node.Id));
        }

        Assert.NotNull(wordId);
        var wrappers = aboveWord.Where(n => n.Id != wordId!.Value).ToList();
        Assert.True(wrappers.Count == 0,
            "one-word content minted wrapper entities above the word — tier is a floor, " +
            "'dog' alone IS the tier-2 word; distinct ids found at tiers: " +
            string.Join(", ", wrappers.Select(w => w.Tier).Distinct()));
    }
}
