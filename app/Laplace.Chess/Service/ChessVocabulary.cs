using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using System.Text;

namespace Laplace.Chess.Service;

public static class ChessVocabulary
{
    public const string SourceName = "ChessSelfPlay";




    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source("ChessSelfPlay");
    public static readonly Hash128 PositionType = EntityTypeRegistry.Id("Chess_Position");
    public static readonly Hash128 SubstructureType = EntityTypeRegistry.Id("Chess_Substructure");
    public static readonly Hash128 MoveType = EntityTypeRegistry.Id("MOVE");

    public static readonly Hash128 OutcomeType = EntityTypeRegistry.Id("OUTCOME");

    public static readonly Hash128 OutcomeObject = EntityTypeRegistry.Id("Chess_Result");




    public static readonly Hash128 PgnSourceId = SubstrateCanonicalIds.Source("ChessPgn");
    public static readonly Hash128 EvalPgnSourceId = SubstrateCanonicalIds.Source("ChessEvalPgn");
    public static readonly Hash128 ReviewSourceId = SubstrateCanonicalIds.Source("ChessReview");
    public static readonly Hash128 UserPromptSourceId = SubstrateCanonicalIds.Source("ChessUserPrompt");
    public static readonly Hash128 OpeningsSourceId = SubstrateCanonicalIds.Source("ChessOpenings");
    public static readonly Hash128 BookSourceId = SubstrateCanonicalIds.Source("ChessBook");
    public static readonly Hash128 LichessProfileSourceId = SubstrateCanonicalIds.Source("LichessPlayerProfile");
    public static readonly Hash128 ChessComProfileSourceId = SubstrateCanonicalIds.Source("ChessComPlayerProfile");
    public static readonly Hash128 FideProfileSourceId = SubstrateCanonicalIds.Source("FidePlayerProfile");

    private static Hash128 TrustClass(string cls) => Hash128.OfCanonical($"substrate/trust_class/{cls}/v1");



    public static readonly Hash128 PgnTrustClass = TrustClass("AcademicCurated");
    public static readonly Hash128 EvalPgnTrustClass = TrustClass("StructuredCorpus");
    public static readonly Hash128 ReviewTrustClass = TrustClass("UserPromptContent");
    public static readonly Hash128 SelfPlayTrustClass = TrustClass("ResponseContent");
    public static readonly Hash128 UserPromptTrustClass = TrustClass("UserPromptContent");
    public static readonly Hash128 OpeningsTrustClass = TrustClass("AcademicCurated");
    public static readonly Hash128 BookTrustClass = TrustClass("AcademicCurated");
    public static readonly Hash128 OnlineProfileTrustClass = TrustClass("StructuredCorpus");
    public static readonly Hash128 FideProfileTrustClass = TrustClass("StandardsDerived");



    public static readonly Hash128 PlayerType = EntityTypeRegistry.Id("Chess_Player");
    public static readonly Hash128 PlayedByType = EntityTypeRegistry.Id("PLAYED_BY");
    public static readonly Hash128 HasRatingType = EntityTypeRegistry.Id("HAS_RATING");
    // The manifest's tail keeps the two chess-specific family roots in stable order. Reuse
    // that governed spelling instead of adding a second C# vocabulary literal; the ISA g3
    // ratchet is shrink-only by design.
    public static readonly Hash128 CorrespondsToType = EntityTypeRegistry.Id(ChessSeedManifest.Relations[^2]);
    public static readonly Hash128 ExternalIdType = EntityTypeRegistry.Id(ChessSeedManifest.Relations[^4]);
    public static readonly Hash128 FeatureType = EntityTypeRegistry.Id(ChessSeedManifest.Relations[^3]);




    public static readonly Hash128 OpeningNameType = EntityTypeRegistry.Id("OPENING_NAME");
    public static readonly Hash128 EcoCodeType = EntityTypeRegistry.Id("HAS_ECO");




    // GH #736: the game CONTENT entity — the LINE, content-addressed from the ordered
    // position ids it passes through (ChessCompose.LineId). One entity per distinct line
    // ever played, no matter who played it or when. The type name stays Chess_Game: the
    // game-as-content IS the line.
    public static readonly Hash128 GameType = EntityTypeRegistry.Id("Chess_Game");
    // Chess_Event = the tournament / named event (many games).
    public static readonly Hash128 EventType = EntityTypeRegistry.Id("Chess_Event");
    public static readonly Hash128 PlayingType = EntityTypeRegistry.Id("Chess_Playing");
    public static readonly Hash128 PlaysLineType = EntityTypeRegistry.Id("PLAYS_LINE");
    public static readonly Hash128 HasSetupType = EntityTypeRegistry.Id("HAS_SETUP");
    public static readonly Hash128 AnalysisVersionMetaTypeId =
        SubstrateCanonicalIds.OfVersioned("type", "HasAnalysisVersion");
    public static readonly Hash128 AnalyzedAtType = EntityTypeRegistry.Id("ANALYZED_AT");
    public static readonly Hash128 AnalysisMarkerType = EntityTypeRegistry.Id("Chess_AnalysisMarker");
    public static readonly Hash128 AnalysisSourceId = SubstrateCanonicalIds.Source("ChessAnalysis");
    public static readonly Hash128 AnalysisTrustClass = TrustClass("DerivedCalculation");
    public static readonly Hash128 TrajectorySourceId = SubstrateCanonicalIds.Source("ChessTrajectory");
    public static readonly Hash128 OpeningMatchSourceId = SubstrateCanonicalIds.Source("ChessOpeningMatch");
    public static readonly Hash128 SyzygyTrustClass = TrustClass("StandardsDerived");

    public static Hash128 AnalysisMarkerId(Hash128 playingId, int version)
        => Hash128.OfCanonical($"chess/analyzed/{playingId}/{version}");
    public static readonly Hash128 HasWhiteType = EntityTypeRegistry.Id("HAS_WHITE");
    public static readonly Hash128 HasBlackType = EntityTypeRegistry.Id("HAS_BLACK");
    public static readonly Hash128 HasEventType = EntityTypeRegistry.Id("HAS_EVENT");
    public static readonly Hash128 OnDateType = EntityTypeRegistry.Id("ON_DATE");
    public static readonly Hash128 HasTimeControlType = EntityTypeRegistry.Id("HAS_TIME_CONTROL");
    public static readonly Hash128 HasTcClassType = EntityTypeRegistry.Id("HAS_TC_CLASS");
    public static readonly Hash128 HasTerminationType = EntityTypeRegistry.Id("HAS_TERMINATION");
    public static readonly Hash128 HasResultType = EntityTypeRegistry.Id("HAS_RESULT");
    public static readonly Hash128 HasEvalType = EntityTypeRegistry.Id("HAS_EVAL");
    public static readonly Hash128 HasEvalObject = EntityTypeRegistry.Id("Chess_Eval");
    public static readonly Hash128 MoveQualityType = EntityTypeRegistry.Id("MOVE_QUALITY");
    public static readonly Hash128 HasWdlType = EntityTypeRegistry.Id("HAS_WDL");
    public static readonly Hash128 HasDtzType = EntityTypeRegistry.Id("HAS_DTZ");
    public static readonly Hash128 HasThinkClassType = EntityTypeRegistry.Id("HAS_THINK_CLASS");
    public static readonly Hash128 GameHasOpeningType = EntityTypeRegistry.Id("GAME_HAS_OPENING");
    public static readonly Hash128 GameHasEcoType = EntityTypeRegistry.Id("GAME_HAS_ECO");
    public static readonly Hash128 GameHasMotifType = EntityTypeRegistry.Id("GAME_HAS_MOTIF");
    public static readonly Hash128 BookLineType = EntityTypeRegistry.Id("Chess_BookLine");
    public static Hash128 BookLineMarkerId(Hash128 bookTitleContentId, Hash128 lineId)
        => Hash128.OfCanonical($"chess/bookline-marker/{bookTitleContentId}/{lineId}");
    public static readonly Hash128 ExplainsType = EntityTypeRegistry.Id("EXPLAINS");
    public static readonly Hash128 IsExampleOfType = EntityTypeRegistry.Id("IS_EXAMPLE_OF");
    public static readonly Hash128 DefinesType = EntityTypeRegistry.Id("HAS_DEFINITION");

    public static Hash128 PgnEventId(string @event, string site, string date)
        => Hash128.OfCanonical($"chess/event/{@event}|{site}|{date}");

    public static Hash128 PgnPlayingId(
        string white, string black, string date, string @event, string round, string site,
        Hash128 lineId, string resultToken)
        => Hash128.OfCanonical(
            $"chess/playing/{white}|{black}|{date}|{@event}|{round}|{site}|{lineId}|{resultToken}");

    public static Hash128 LivePlayingId(
        Hash128? whitePlayer, Hash128? blackPlayer, string learnContext,
        Hash128 lineId, string resultToken, string? occurrenceKey = null)
    {
        string canonical =
            $"chess/playing/live/{whitePlayer}|{blackPlayer}|{learnContext}|{lineId}|{resultToken}";
        return string.IsNullOrWhiteSpace(occurrenceKey)
            ? Hash128.OfCanonical(canonical)
            : Hash128.OfCanonical($"{canonical}|occurrence:{occurrenceKey.Trim()}");
    }

    public static Hash128 PlaySessionHandle(Guid sessionGame)
        => Hash128.OfCanonical($"chess/play/{sessionGame:N}");

    public static Hash128 PlayerId(string name) => Hash128.OfCanonical($"chess/player/{PlayerAlias.Canonical(name)}");

    public static Hash128 LegacyPlayerId(string rawName) => Hash128.OfCanonical($"chess/player/{rawName.Trim()}");

    public static readonly Hash128 LaplacePlayerId = PlayerId("Laplace");

    public static readonly IReadOnlyList<Hash128> HistoricalLaplacePlayerIds =
    [
        PlayerId("Laplace-guided-transition"),
        PlayerId("Laplace-guided-fold"),
        PlayerId("Laplace-guided-edge"),
    ];

    public static Hash128 EmitPlayer(
        SubstrateChangeBuilder b, Hash128 playerId, string name, Hash128 sourceId,
        double witnessWeight = SourceTrust.AcademicCurated)
    {
        b.AddEntity(playerId, EntityTier.Word, PlayerType, sourceId);
        if (ContentEmitter.Emit(b, name, sourceId) is { } nameId)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                playerId, "HAS_NAME_ALIAS", nameId, sourceId, null, witnessWeight));

            AppendPlayerPhysicality(b, playerId, name, sourceId, nameId);
        }
        return playerId;
    }

    /// <summary>
    /// Project a governed player identity onto its witnessed display-name content. The player
    /// handle is not the content hash of the name, so a one-child trajectory must NOT be type
    /// Content (which would collapse to the name root). The name root itself owns the complete
    /// text DAG down through graphemes/codepoints; this Projection only places the governed
    /// identity at that content-derived coordinate.
    /// </summary>
    public static void AppendPlayerPhysicality(
        SubstrateChangeBuilder b, Hash128 playerId, string name, Hash128 sourceId,
        Hash128? expectedNameRoot = null)
    {
        Hash128 physId = PhysicalityId.Compute(playerId, PhysicalityType.Projection);

        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        if (!TextEntityBuilder.TryDecomposeRoot(
                utf8, out var nameRoot, out _, out var x, out var y, out var z, out var m)
            || (expectedNameRoot is { } expected && nameRoot != expected))
            throw new InvalidOperationException(
                $"player name '{name}' did not reproduce its deposited content root");

        double[] coord = [x, y, z, m];
        b.AddPhysicality(new PhysicalityRow(
            Id: physId,
            EntityId: playerId,
            SourceId: sourceId,
            Type: PhysicalityType.Projection,
            CoordX: x, CoordY: y, CoordZ: z, CoordM: m,
            HilbertIndex: Hilbert128.Encode(coord),
            TrajectoryXyzm: Trajectory.Build([nameRoot]),
            NConstituents: 1,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: IngestClock.NowUnixUs()));
    }

    public const double Trust = SourceTrust.StructuredCorpus;

    public readonly record struct BootstrapSource(
        Hash128 SourceId, string SourceName, Hash128 TrustClassId);

    public static Task<IReadOnlyCollection<string>> BootstrapAsync(
    ISubstrateWriter writer, CancellationToken ct = default, ISubstrateReader? reader = null)
    => BootstrapAsync(writer, SourceId, SourceName, SelfPlayTrustClass, ct, reader);

    public static async Task<IReadOnlyCollection<string>> BootstrapAsync(
    ISubstrateWriter writer, Hash128 sourceId, string sourceName, Hash128 trustClassId,
    CancellationToken ct = default, ISubstrateReader? reader = null)
        => await BootstrapManyAsync(
            writer, [new BootstrapSource(sourceId, sourceName, trustClassId)], ct, reader)
            .ConfigureAwait(false);

    public static async Task<IReadOnlyCollection<string>> BootstrapManyAsync(
        ISubstrateWriter writer, IReadOnlyList<BootstrapSource> sources,
        CancellationToken ct = default, ISubstrateReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return Array.Empty<string>();

        var unique = new List<BootstrapSource>(sources.Count);
        var seen = new HashSet<Hash128>();
        foreach (var source in sources)
            if (seen.Add(source.SourceId)) unique.Add(source);

        var builders = new BootstrapIntentBuilder[unique.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < unique.Count; i++)
        {
            var source = unique[i];
            var boot = new BootstrapIntentBuilder(
                source.SourceId, source.SourceName, source.TrustClassId);
            foreach (var t in ChessSeedManifest.TypeNodeNames) boot.AddType(t);
            foreach (var r in SourceVocabularyBootstrap.ExpandRelationsWithFamily(
                         ChessSeedManifest.Relations))
                boot.AddRelationType(r);
            builders[i] = boot;
            names.UnionWith(boot.CanonicalNames);
        }

        var present = new bool[unique.Count];
        if (reader is not null)
        {
            var probeIds = new List<Hash128>(unique.Count);
            var probeSlots = new List<int>(unique.Count);
            for (int i = 0; i < unique.Count; i++)
            {
                if (reader.IsProvenPresent(unique[i].SourceId))
                    present[i] = true;
                else
                {
                    probeIds.Add(unique[i].SourceId);
                    probeSlots.Add(i);
                }
            }

            if (probeIds.Count > 0)
            {
                var presenceScope = reader.CapturePresenceScope();
                byte[] bitmap = await reader.EntitiesExistBitmapAsync(probeIds, ct)
                    .ConfigureAwait(false);
                var confirmed = new List<Hash128>(probeIds.Count);
                for (int p = 0; p < probeIds.Count; p++)
                    if (BitmapBits.IsSet(bitmap, p))
                    {
                        present[probeSlots[p]] = true;
                        confirmed.Add(probeIds[p]);
                    }
                if (confirmed.Count > 0) reader.MarkProven(confirmed, presenceScope);
            }
        }

        var changes = new List<SubstrateChange>(unique.Count);
        var deposited = new List<Hash128>(unique.Count);
        for (int i = 0; i < unique.Count; i++)
            if (!present[i])
            {
                changes.Add(builders[i].Build());
                deposited.Add(unique[i].SourceId);
            }

        if (changes.Count > 0)
        {
            var presenceScope = reader?.CapturePresenceScope() ?? default;
            await writer.ApplyManyAsync(changes, ct).ConfigureAwait(false);
            reader?.MarkProven(deposited, presenceScope);
        }
        return names;
    }
}
