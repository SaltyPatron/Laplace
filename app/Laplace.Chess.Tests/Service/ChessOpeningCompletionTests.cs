using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessOpeningCompletionTests
{
    private static Hash128 Id(string name) => Hash128.OfCanonical("test/opening-generation/" + name);

    [Fact]
    public void CatalogGenerationBindsSelectedMapAndOwnsItsSnapshot()
    {
        var first = new Dictionary<Hash128, (Hash128 NameId, Hash128? EcoId)>
        {
            [Id("p1")] = (Id("name1"), null),
            [Id("p2")] = (Id("name2"), Id("eco2")),
        };
        var snapshot = new ChessOpeningIndex(first);
        var reversed = new ChessOpeningIndex(first.Reverse().ToDictionary(entry => entry.Key, entry => entry.Value));
        Assert.Equal(snapshot.GenerationId, reversed.GenerationId);
        Assert.NotNull(snapshot.GenerationId);

        first[Id("p1")] = (Id("renamed"), null);
        Assert.NotEqual(snapshot.GenerationId, new ChessOpeningIndex(first).GenerationId);
        Assert.Equal(Id("name1"), snapshot.Lookup(Id("p1"))!.Value.NameId);
        first[Id("p1")] = (Id("name1"), default(Hash128));
        Assert.NotEqual(snapshot.GenerationId, new ChessOpeningIndex(first).GenerationId);
        first[Id("p1")] = (Id("name1"), null);
        first[Id("p3")] = (Id("deeper"), Id("eco3"));
        Assert.NotEqual(snapshot.GenerationId, new ChessOpeningIndex(first).GenerationId);
    }

    [Fact]
    public async Task ChangedCatalogReopensBothPriorMissesAndPriorShallowerMatches()
    {
        CodepointPerfcache.LoadDefault();
        const string pgn = "[Event \"catalog\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n1. e4 e5 1-0";
        var parsed = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        var game = ChessAnalyze.WitnessedFromParsed(parsed);
        var empty = new ChessOpeningIndex(new Dictionary<Hash128, (Hash128, Hash128?)>());
        var shallow = new ChessOpeningIndex(new Dictionary<Hash128, (Hash128, Hash128?)>
        {
            [parsed.PositionIds[0]] = (Id("shallow-name"), null),
        });
        var deep = new ChessOpeningIndex(new Dictionary<Hash128, (Hash128, Hash128?)>
        {
            [parsed.PositionIds[0]] = (Id("shallow-name"), null),
            [parsed.PositionIds[^1]] = (Id("deep-name"), Id("deep-eco")),
        });
        var marker = ChessOpeningMatchDecomposer.MarkerId(game.LineId);
        var owner = ChessVocabulary.OpeningMatchSourceId;

        foreach (var previous in new[] { empty, shallow })
        {
            using var oldBuilder = new SubstrateChangeBuilder(owner, "opening-generation");
            ChessOpeningMatchDecomposer.Match(oldBuilder, game, previous, owner);
            var old = oldBuilder.Build();
            var oldReceipt = Assert.Single(old.UnitCompletions);
            Assert.Equal(previous.GenerationId, oldReceipt.Digest);
            var reader = new ReceiptReader();
            reader.Present.Add(oldReceipt);

            async Task<Hash128[]> RemainingAsync()
            {
                var remaining = new List<Hash128>();
                await foreach (var id in ChessWitnessHydrator.FilterByMarkerAsync(
                                   [game.LineId], reader, 1, ChessOpeningMatchDecomposer.MarkerId,
                                   21, [owner], CancellationToken.None, deep.GenerationId))
                    remaining.Add(id);
                return remaining.ToArray();
            }
            Assert.Equal(new[] { game.LineId }, await RemainingAsync());
            using var newBuilder = new SubstrateChangeBuilder(owner, "opening-generation");
            ChessOpeningMatchDecomposer.Match(newBuilder, game, deep, owner);
            var current = newBuilder.Build();
            Assert.Contains(current.Attestations, row =>
                row.TypeId == RelationTypeRegistry.RelationTypeId(ChessSeedManifest.GameHasOpening)
                && row.ObjectId == Id("deep-name"));
            var currentReceipt = Assert.Single(current.UnitCompletions);
            Assert.Equal(new IngestUnitCompletionKey(owner, marker, 21, deep.GenerationId),
                currentReceipt);
            Assert.NotEqual(oldReceipt, currentReceipt);
            reader.Present.Add(currentReceipt);
            Assert.Empty(await RemainingAsync());
        }
    }

    [Fact]
    public void CatalogViewWithoutIdentityCannotGrantDurableCompletion()
    {
        CodepointPerfcache.LoadDefault();
        var parsed = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(
            "[Event \"unversioned\"]\n[White \"A\"]\n[Black \"B\"]\n\n1. e4 e5 1-0"));
        using var builder = new SubstrateChangeBuilder(ChessVocabulary.OpeningMatchSourceId, "unversioned-catalog");
        ChessOpeningMatchDecomposer.Match(builder, ChessAnalyze.WitnessedFromParsed(parsed),
            new UnversionedView(), ChessVocabulary.OpeningMatchSourceId);
        Assert.Empty(builder.Build().UnitCompletions);
        var record = new ChessOpeningMatchRecord(ChessAnalyze.WitnessedFromParsed(parsed));
        Assert.Null(record.Completion);
    }

    private sealed class UnversionedView : ChessOpeningIndexView
    {
        public (Hash128 NameId, Hash128? EcoId, int Ply)? DeepestMatch(IReadOnlyList<Hash128> positions) => null;
    }

    private sealed class ReceiptReader : ISubstrateReader
    {
        internal readonly HashSet<IngestUnitCompletionKey> Present = [];
        public Task<bool> HasSourceEverCompletedAsync(int layer, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 source, int layer, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 type, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> ids, CancellationToken ct = default) =>
            throw new InvalidOperationException("catalog completion cannot be inferred from an entity");
        public Task<IReadOnlySet<IngestUnitCompletionKey>> CompletedUnitsAsync(
            IReadOnlyList<IngestUnitCompletionKey> keys, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<IngestUnitCompletionKey>>(keys.Where(Present.Contains).ToHashSet());
    }
}
