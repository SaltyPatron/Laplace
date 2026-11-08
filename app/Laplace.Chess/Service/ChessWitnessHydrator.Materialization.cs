using System.Runtime.CompilerServices;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

internal static partial class ChessWitnessHydrator
{
    /// <summary>
    /// Strict inputs for the position-outcome version transition: the complete line,
    /// its start position, and the literal recorded result for every selected playing.
    /// Annotation, clock and engine-evaluation trajectories do not participate. Unknown, missing
    /// or conflicting recorded inputs are refused before source eviction.
    /// </summary>
    internal static async Task<IReadOnlyList<ChessWitnessedGame>> HydratePositionOutcomeInputsAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> playingIds, long maximumMaterializedBytes,
        CancellationToken ct)
    {
        if (playingIds.Count == 0) return [];
        var budget = new NpgsqlSubstrateReads.ChessWitnessReadBudget(maximumMaterializedBytes);
        budget.Reserve(checked(65536L + playingIds.Count * 512L));
        byte[][] selected = playingIds.Select(id => id.ToBytes()).ToArray();
        var bindings = await NpgsqlSubstrateReads.ChessWitnessInputsAsync(ds, selected,
            [RelPlaysLine.ToBytes()], TransitionWitnessSources(), null, budget, ct).ConfigureAwait(false);
        var lineByPlaying = new Dictionary<Hash128, Hash128>();
        foreach (var row in bindings)
        {
            if (row.Object is null || row.Context is not null)
                throw new InvalidDataException("Recorded playing has an invalid PLAYS_LINE binding.");
            Hash128 playing = Hash128.FromBytes(row.Subject), line = Hash128.FromBytes(row.Object);
            if (lineByPlaying.TryGetValue(playing, out var previous) && previous != line)
                throw new InvalidDataException("Recorded playing has competing PLAYS_LINE bindings.");
            lineByPlaying[playing] = line;
        }
        if (playingIds.Any(id => !lineByPlaying.ContainsKey(id)))
            throw new InvalidDataException("Selected playing corpus could not be completely reconstructed: missing recorded line binding.");

        var lines = lineByPlaying.Values.Distinct().ToArray();
        var headers = await NpgsqlSubstrateReads.ChessWitnessInputsAsync(ds,
            lines.Select(id => id.ToBytes()).ToArray(), GameRelationTypes,
            TransitionWitnessSources(), selected, budget, ct).ConfigureAwait(false);
        var values = new Dictionary<(Hash128 Playing, Hash128 Type, Side Side), Hash128>();
        foreach (var row in headers)
        {
            if (row.Context is null || row.Object is null)
                throw new InvalidDataException("Recorded playing has an incomplete header binding.");
            Hash128 playing = Hash128.FromBytes(row.Context), line = Hash128.FromBytes(row.Subject);
            if (lineByPlaying[playing] != line)
                throw new InvalidDataException("Recorded playing header names a competing line.");
            Hash128 type = Hash128.FromBytes(row.Type);
            Side side = SideOf(type, row.Qualifiers);
            var key = (playing, side != Side.None ? RelHasPlayer : type, side);
            var value = Hash128.FromBytes(row.Object);
            if (values.TryGetValue(key, out var previous) && previous != value)
                throw new InvalidDataException("Recorded playing has competing header values.");
            values[key] = value;
        }
        var wanted = new List<(Hash128 Line, Hash128 Event, GameMeta Meta)>(playingIds.Count);
        foreach (var playing in playingIds)
        {
            if (!values.TryGetValue((playing, RelHasResult, Side.None), out var result))
                throw new InvalidDataException("Selected playing corpus could not be completely reconstructed: missing recorded result.");
            wanted.Add((lineByPlaying[playing], playing, new GameMeta
            {
                ResultObj = result,
                White = values.GetValueOrDefault((playing, RelHasPlayer, Side.White)),
                Black = values.GetValueOrDefault((playing, RelHasPlayer, Side.Black)),
                SetupObj = values.GetValueOrDefault((playing, RelHasSetup, Side.None)),
            }));
        }

        var resultIds = wanted.Select(w => w.Meta.ResultObj).Distinct().ToArray();
        var resultText = await ReadStrictResultsAsync(ds, resultIds, budget, ct).ConfigureAwait(false);
        // Reserve the encoded geometry and native-expanded work before constituents are
        // expanded or replay/board arrays are built.
        var lineShape = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds, lines.Select(id => id.ToBytes()).ToArray(),
            budget, 4096, ct).ConfigureAwait(false);
        var lineMultiplicity = wanted.GroupBy(row => row.Line).ToDictionary(group => group.Key, group => group.Count());
        foreach (var row in lineShape)
            budget.Reserve(checked((long)row.RunLength * (lineMultiplicity[Hash128.FromBytes(row.Parent)] - 1) * 4096));
        var setupIds = wanted.Select(w => w.Meta.SetupObj).Where(id => id != default).Distinct().ToArray();
        var setupShape = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds,
            setupIds.Select(id => id.ToBytes()).ToArray(), budget, 512, ct).ConfigureAwait(false);
        var setupAtoms = setupShape.Select(row => Hash128.FromBytes(row.Child)).Distinct().ToArray();
        var atomShape = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds,
            setupAtoms.Select(id => id.ToBytes()).ToArray(), budget, 512, ct).ConfigureAwait(false);
        var atomFields = atomShape.GroupBy(row => Hash128.FromBytes(row.Parent))
            .ToDictionary(group => group.Key, group => group.Sum(row => (long)row.RunLength));
        foreach (var row in setupShape)
            budget.Reserve(checked((long)row.RunLength * atomFields.GetValueOrDefault(Hash128.FromBytes(row.Child)) * 512));
        var games = await MaterializeAsync(ds, wanted, ct, resultText).ConfigureAwait(false);
        if (games.Count != playingIds.Count || games.Select(game => game.PlayingId).Distinct().Count() != playingIds.Count)
            throw new InvalidDataException("Selected playing corpus could not be completely reconstructed from its recorded inputs.");
        return games;
    }

    private static async Task<IReadOnlyDictionary<Hash128, string>> ReadStrictResultsAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> selected,
        NpgsqlSubstrateReads.ChessWitnessReadBudget budget, CancellationToken ct)
    {
        // The finite result vocabulary uses the ordinary native text composer. Its
        // actual emitted manifests delimit the graph the renderer may visit; this
        // avoids admitting an arbitrary text closure under a purported result id.
        var supported = new Dictionary<Hash128, string>();
        var expected = new Dictionary<Hash128, Hash128[]>();
        foreach (string token in new[] { "1-0", "0-1", "1/2-1/2" })
        {
            using var tree = ContentTierSpine.BuildTree(System.Text.Encoding.UTF8.GetBytes(token))
                ?? throw new InvalidDataException("Cannot compose the canonical recorded result vocabulary.");
            var ids = tree.NodeIds();
            supported[ids[tree.NaturalUnitIndex()]] = token;
            for (uint index = 0; index < tree.NodeCount; index++)
            {
                if (!tree.ShouldEmitCompositional(index)) continue;
                var node = tree.GetNode(index);
                var children = new Hash128[node.ChildCount];
                for (uint child = 0; child < node.ChildCount; child++)
                    children[child] = ids[node.FirstChildIdx + child];
                expected[ids[index]] = children;
            }
        }
        if (selected.Any(id => !supported.ContainsKey(id)))
            throw new InvalidDataException("Recorded result is unsupported or unknown; '*' is not a draw.");
        var actual = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds,
            expected.Keys.Select(id => id.ToBytes()).ToArray(), budget, 256, ct).ConfigureAwait(false);
        var byParent = actual.GroupBy(row => Hash128.FromBytes(row.Parent))
            .ToDictionary(group => group.Key, group => group.ToArray());
        // Only required result graphs must be present. The complete finite graph is
        // checked when present so native rendering cannot follow unbounded extras.
        foreach (var (parent, rows) in byParent)
        {
            var children = expected[parent];
            int offset = 0;
            foreach (var row in rows)
            {
                if (row.RunLength > children.Length - offset)
                    throw new InvalidDataException("Recorded result Content disagrees with its canonical native manifest.");
                for (int repeat = 0; repeat < row.RunLength; repeat++)
                    if (children[offset++] != Hash128.FromBytes(row.Child))
                        throw new InvalidDataException("Recorded result Content disagrees with its canonical native manifest.");
            }
            if (offset != children.Length)
                throw new InvalidDataException("Recorded result Content is incomplete.");
        }
        budget.Reserve(4096);
        var rendered = await NpgsqlSubstrateReads.RenderPreflightedChessResultsAsync(ds,
            selected.Select(id => id.ToBytes()).ToArray(), ContentTierSpine.MaxContentTier + 1, ct).ConfigureAwait(false);
        if (rendered is null || rendered.Length != selected.Count)
            throw new InvalidDataException("Recorded result could not be rendered completely.");
        var result = new Dictionary<Hash128, string>();
        for (int index = 0; index < selected.Count; index++)
        {
            if (rendered[index] != supported[selected[index]])
                throw new InvalidDataException("Recorded result is missing or could not be rendered exactly.");
            result[selected[index]] = rendered[index];
        }
        return result;
    }

    internal static async Task<IReadOnlyList<ChessWitnessedGame>> TryHydrateLinesAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> lineIds, CancellationToken ct)
    {
        if (lineIds.Count == 0) return Array.Empty<ChessWitnessedGame>();

        var groups = await FetchHeaderGroupsAsync(ds, lineIds.ToArray(), ct).ConfigureAwait(false);

        var representative = new Dictionary<Hash128, (Hash128 Event, GameMeta Meta)>(lineIds.Count);
        foreach (var ((lineId, eventId), gm) in groups)
        {
            if (!representative.TryGetValue(lineId, out var cur) || eventId.CompareToBytewise(cur.Event) < 0)
                representative[lineId] = (eventId, gm);
        }

        var wanted = new List<(Hash128 Line, Hash128 Event, GameMeta Meta)>(lineIds.Count);
        foreach (var lineId in lineIds)
            if (representative.TryGetValue(lineId, out var rep))
                wanted.Add((lineId, rep.Event, rep.Meta));
        return await MaterializeAsync(ds, wanted, ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<(Hash128 Line, Hash128 Event), GameMeta>> FetchHeaderGroupsAsync(
        NpgsqlDataSource ds, Hash128[] lineIds, CancellationToken ct)
    {
        var lineBytes = new byte[lineIds.Length][];
        for (int i = 0; i < lineIds.Length; i++) lineBytes[i] = lineIds[i].ToBytes();

        var groups = new Dictionary<(Hash128, Hash128), GameMeta>();
        var rows = await NpgsqlSubstrateReads.AttestationsBySubjectsAndTypesAsync(
            ds, lineBytes, GameRelationTypes, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.ContextId is null) continue;
            var key = (Hash128.FromBytes(row.SubjectId), Hash128.FromBytes(row.ContextId));
            if (!groups.TryGetValue(key, out var gm)) groups[key] = gm = new GameMeta();
            var type = Hash128.FromBytes(row.TypeId);
            var obj = row.ObjectId is null ? default : Hash128.FromBytes(row.ObjectId);
            Side side = SideOf(type, row.QualifierMask);
            if (side == Side.White) gm.White = obj;
            else if (side == Side.Black) gm.Black = obj;
            else if (type == RelHasSetup) gm.SetupObj = obj;
            else if (type == RelHasResult) gm.ResultObj = obj;
        }
        return groups;
    }

    /// <summary>Replay the complete typed input after the strict reader has reserved its
    /// checked expanded work; the UI replay window does not apply.</summary>
    internal static ChessReplayResult ReplayAdmittedLine(IReadOnlyList<Hash128> moveIds, string? startFen, ChessVariantRules? rules = null)
        => ChessReplay.Replay(moveIds, startFen, maxPlies: moveIds.Count, rules: rules);

    private static async Task<IReadOnlyList<ChessWitnessedGame>> MaterializeAsync(
        NpgsqlDataSource ds, IReadOnlyList<(Hash128 Line, Hash128 Event, GameMeta Meta)> wanted,
        CancellationToken ct, IReadOnlyDictionary<Hash128, string>? positionOutcomeResults = null)
    {
        if (wanted.Count == 0) return Array.Empty<ChessWitnessedGame>();

        var contentIds = new List<Hash128>();
        void Need(Hash128 id) { if (id != default) contentIds.Add(id); }
        if (positionOutcomeResults is null)
            foreach (var (_, _, gm) in wanted) Need(gm.ResultObj);

        var setupIds = wanted.Select(static w => w.Meta.SetupObj)
            .Where(static id => id != default).Distinct().ToArray();
        IReadOnlyDictionary<Hash128, Board> setupBoards;
        if (setupIds.Length == 0) setupBoards = new Dictionary<Hash128, Board>();
        else
        {
            var setupBytes = new byte[setupIds.Length][];
            for (int i = 0; i < setupIds.Length; i++) setupBytes[i] = setupIds[i].ToBytes();
            var setupRows = await NpgsqlSubstrateReads.NestedTrajectoryConstituentsAsync(
                ds, setupBytes, ct).ConfigureAwait(false);
            setupBoards = ChessPositionTrajectory.Decode(setupRows);
        }
        var trajectoryOwners = wanted.SelectMany(w => positionOutcomeResults is null ? new[] { w.Line, w.Event } : new[] { w.Line })
            .Distinct().ToArray();
        var ownerBytes = new byte[trajectoryOwners.Length][];
        for (int i = 0; i < trajectoryOwners.Length; i++) ownerBytes[i] = trajectoryOwners[i].ToBytes();
        var trajectoryRows = await NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync(
            ds, ownerBytes,
            positionOutcomeResults is null
                ? [PhysicalityType.Content, PhysicalityType.ChessComment, PhysicalityType.ChessAnnotation]
                : [PhysicalityType.Content],
            ct).ConfigureAwait(false);
        var lanes = new Dictionary<(Hash128 Playing, PhysicalityType Type), List<Hash128>>();
        foreach (var row in trajectoryRows)
        {
            var playing = Hash128.FromBytes(row.ParentId);
            var key = (playing, row.Type);
            if (!lanes.TryGetValue(key, out var values)) lanes[key] = values = [];
            var id = Hash128.FromBytes(row.EntityId);
            values.Add(id);
            if (row.Type != PhysicalityType.Content
                && id != ChessCompose.AnnotationMissing().Id)
                Need(id);
        }
        var textById = positionOutcomeResults is null
            ? await RenderTextBatchAsync(ds, contentIds, ct).ConfigureAwait(false)
            : positionOutcomeResults;

        var outList = new List<ChessWitnessedGame>(wanted.Count);
        foreach (var (lineId, eventId, gm) in wanted)
        {
            string? resultStr = gm.ResultObj != default && textById.TryGetValue(gm.ResultObj, out var rs)
                ? rs : null;
            string? startFen = gm.SetupObj != default
                && setupBoards.TryGetValue(gm.SetupObj, out var setupBoard)
                ? setupBoard.ToFen() : null;
            if (positionOutcomeResults is not null && gm.SetupObj != default && startFen is null)
                throw new InvalidDataException("Selected playing corpus could not be completely reconstructed: missing recorded start position.");

            var rules = gm.SetupObj != default && setupBoards.TryGetValue(gm.SetupObj, out var ruleBoard) ? ruleBoard.Rules : null;
            var modality = new ChessModality();
            if (ChessAnalyze.InitialState(startFen, modality, rules) is not { } initial) continue;
            Hash128 expectedStart = ChessCompose.PositionId(initial.Initial.Board);
            Hash128 startPositionId;
            Hash128[] moveIds;
            if (lineId == expectedStart)
            {
                // A zero-move playing reuses its sole start constituent under the native
                // singleton content law. Its board Content belongs to the position: do
                // not reinterpret those children as moves or emit a self-reference.
                startPositionId = expectedStart;
                moveIds = [];
            }
            else
            {
                if (!lanes.TryGetValue((lineId, PhysicalityType.Content), out var contentManifest)
                    || contentManifest.Count == 0) continue;
                startPositionId = contentManifest[0];
                moveIds = contentManifest.Skip(1).ToArray();
            }
            if (startPositionId != expectedStart) continue;
            if (ChessCompose.LineId(startPositionId, moveIds) != lineId) continue;

            // Strict callers reserved every expanded occurrence before MaterializeAsync.
            // A UI replay window is not a semantic limit on an admitted recorded game.
            var replay = positionOutcomeResults is null
                ? ChessReplay.Replay(moveIds, startFen, rules: rules)
                : ReplayAdmittedLine(moveIds, startFen, rules);
            if (replay.Truncated is not null || replay.Plies.Count != moveIds.Length) continue;
            var moves = replay.Plies.Select(static p => p.San).ToArray();
            string?[]? comments = RenderLane(
                lanes, eventId, PhysicalityType.ChessComment, moves.Length, textById);
            string?[]? annotations = RenderLane(
                lanes, eventId, PhysicalityType.ChessAnnotation, moves.Length, textById);
            string annotationPgn = RebuildAnnotatedMovetext(moves, comments);
            string?[]? clockTokens = comments is null
                ? null : PgnClocks.ClockTokens(annotationPgn, moves.Length);
            string?[]? evalTokens = comments is null
                ? null : PgnEvals.EvalTokens(annotationPgn, moves.Length);
            double[]? spent = comments is null
                ? null : PgnClocks.SpentSeconds(annotationPgn, moves.Length);
            string?[]? quality = annotations?.Select(MoveQuality.FromSerializedAnnotations).ToArray();
            if (quality is not null && quality.All(static q => q is null)) quality = null;
            outList.Add(new ChessWitnessedGame(
                lineId, eventId, moves, ParseResult(resultStr),
                gm.White != default ? gm.White : null,
                gm.Black != default ? gm.Black : null,
                startFen, clockTokens, evalTokens, quality, spent)
                {
                    MoveIds = moveIds, StartPositionId = startPositionId, Rules = rules,
                    AdmittedReplay = positionOutcomeResults is null ? null : replay
                });
        }
        return outList;
    }

    private static string?[]? RenderLane(
        IReadOnlyDictionary<(Hash128 Playing, PhysicalityType Type), List<Hash128>> lanes,
        Hash128 playing, PhysicalityType type, int count,
        IReadOnlyDictionary<Hash128, string> textById)
    {
        if (!lanes.TryGetValue((playing, type), out var ids) || ids.Count != count) return null;
        var missing = ChessCompose.AnnotationMissing().Id;
        var values = new string?[count];
        for (int i = 0; i < count; i++)
            if (ids[i] != missing && textById.TryGetValue(ids[i], out var value)) values[i] = value;
        return values;
    }

    private static string RebuildAnnotatedMovetext(IReadOnlyList<string> moves, string?[]? comments)
    {
        var text = new System.Text.StringBuilder(moves.Count * 16);
        for (int i = 0; i < moves.Count; i++)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(moves[i]);
            if (comments is not null && !string.IsNullOrWhiteSpace(comments[i]))
                text.Append(" { ").Append(comments[i]).Append(" }");
        }
        return text.ToString();
    }

    internal static async Task<ChessWitnessedGame?> TryHydrateAsync(
        NpgsqlDataSource ds, Hash128 eventId, CancellationToken ct)
    {
        var list = await TryHydrateChunkAsync(ds, [eventId], ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    private sealed class GameMeta
    {
        public Hash128 White;
        public Hash128 Black;
        public Hash128 SetupObj;
        public Hash128 ResultObj;
    }

    private static async Task<Dictionary<Hash128, string>> RenderTextBatchAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> ids, CancellationToken ct)
    {
        var map = new Dictionary<Hash128, string>();
        if (ids.Count == 0) return map;

        var unique = ids.Distinct().Where(id => id != default).ToArray();
        if (unique.Length == 0) return map;

        var bytes = new byte[unique.Length][];
        for (int i = 0; i < unique.Length; i++) bytes[i] = unique[i].ToBytes();

        var texts = await NpgsqlSubstrateReads.RenderTextBatchAsync(ds, bytes, ct)
            .ConfigureAwait(false);
        if (texts is null || texts.Length != unique.Length) return map;
        for (int i = 0; i < unique.Length; i++)
        {
            if (!string.IsNullOrEmpty(texts[i])) map[unique[i]] = texts[i];
        }
        return map;
    }

    private static GameOutcome ParseResult(string? s) => s switch
    {
        "1-0" => new GameOutcome(0),
        "0-1" => new GameOutcome(1),
        "1/2-1/2" => new GameOutcome(null),
        _ => new GameOutcome(null),
    };
}
