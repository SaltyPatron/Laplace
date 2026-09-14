using System.Text.Json;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// A deterministic, order-insensitive player-set export recipe. The export is a projection/filter
/// over one substrate evidence boundary: the members are canonical Chess_Player ids, not display
/// names, and changing the evidence boundary re-mints the export while preserving the same member
/// composition id.
/// </summary>
public sealed record ChessPlayerModelExport(
    int Version,
    Hash128 Id,
    Hash128 MemberSetId,
    string EvidenceBoundary,
    IReadOnlyList<Hash128> Members)
{
    public const int CurrentVersion = 1;
    public const string Recipe = "chess/player-model/export/v1";

    public static ChessPlayerModelExport Create(
        IEnumerable<Hash128> members,
        string evidenceBoundary)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceBoundary);

        var ordered = members
            .Distinct()
            .OrderBy(static id => Hex(id), StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("a player-model export needs at least one member", nameof(members));

        string memberSurface = string.Join("/", ordered.Select(static id => Hex(id)));
        Hash128 memberSetId = Hash128.OfCanonical($"chess/player-model/set/v{CurrentVersion}/{memberSurface}");
        Hash128 id = Hash128.OfCanonical(
            $"{Recipe}/{Hex(memberSetId)}/{evidenceBoundary.Trim()}");
        return new ChessPlayerModelExport(
            CurrentVersion, id, memberSetId, evidenceBoundary.Trim(), ordered);
    }

    public string ToJson(bool indented = true)
    {
        var dto = new ExportDto(
            Version,
            Hex(Id),
            Hex(MemberSetId),
            EvidenceBoundary,
            Members.Select(static id => Hex(id)).ToArray());
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = indented });
    }

    public static ChessPlayerModelExport FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var dto = JsonSerializer.Deserialize<ExportDto>(json)
                  ?? throw new InvalidDataException("player-model export JSON is empty");
        if (dto.Version != CurrentVersion)
            throw new InvalidDataException(
                $"unsupported player-model export version {dto.Version}; expected {CurrentVersion}");

        var members = dto.Members.Select(ParseHash).ToArray();
        var rebuilt = Create(members, dto.EvidenceBoundary);
        if (rebuilt.Id != ParseHash(dto.Id) || rebuilt.MemberSetId != ParseHash(dto.MemberSetId))
            throw new InvalidDataException(
                "player-model export identity does not match its canonical members/evidence boundary");
        return rebuilt;
    }

    public static ChessPlayerModelExport ForNames(
        IEnumerable<string> playerNames,
        string evidenceBoundary)
    {
        ArgumentNullException.ThrowIfNull(playerNames);
        return Create(
            playerNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => ChessVocabulary.PlayerId(name.Trim())),
            evidenceBoundary);
    }

    private sealed record ExportDto(
        int Version,
        string Id,
        string MemberSetId,
        string EvidenceBoundary,
        string[] Members);

    internal static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());

    internal static Hash128 ParseHash(string hex)
    {
        if (hex is null || hex.Length != 32)
            throw new InvalidDataException("player-model ids must be 16-byte / 32-hex identities");
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); }
        catch (FormatException ex)
        {
            throw new InvalidDataException("player-model id is not valid hex", ex);
        }
        return new Hash128(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
    }
}

internal readonly record struct ChessPlayerMoveEvidence(
    Hash128 NextPosition,
    long Games,
    double Score);

public sealed record ChessPlayerModelReceipt(
    string ExportId,
    string MemberSetId,
    int Members,
    long RootReads,
    long MemberReads,
    long MembersWithEvidence,
    long MovesInfluenced,
    long ContextReads = 0,
    long ContextCells = 0)
{
    public string Summary =>
        $"player-model={ExportId[..Math.Min(12, ExportId.Length)]} " +
        $"members={Members} roots={RootReads} member-reads={MemberReads} " +
        $"evidence-members={MembersWithEvidence} moves={MovesInfluenced} " +
        $"context-reads={ContextReads} context-cells={ContextCells}";
}

/// <summary>
/// Root-only player-conditioned evidence plane. Each constituent player is read independently;
/// disagreement remains disagreement instead of being erased at export time. Exact-position
/// successor evidence is confidence-shrunk by its own game count. A8 context evidence then
/// adjusts that constituent's authority: board phase is always selected from the current board;
/// an optional witnessed clock/think class can be supplied by a live driver. The context cannot
/// invent a move by itself — it can only strengthen or weaken the constituent's actual move
/// evidence — and the final result remains inside the same ±150cp steering envelope.
/// </summary>
public sealed class ChessCompositePlayerBias : IRootBias
{
    private readonly ChessPlayerModelExport _export;
    private readonly Func<Hash128, Hash128, bool, int, IReadOnlyList<ChessPlayerMoveEvidence>> _read;
    private readonly Func<Hash128, Hash128, NpgsqlConsensusCell.Row?>? _readContext;
    private readonly int _capCp;
    private string? _thinkContext;
    private long _rootReads;
    private long _memberReads;
    private long _membersWithEvidence;
    private long _movesInfluenced;
    private long _contextReads;
    private long _contextCells;

    public ChessCompositePlayerBias(
        NpgsqlDataSource ds,
        ChessPlayerModelExport export,
        int capCp = 150)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _capCp = Math.Clamp(capCp, 0, 150);
        _read = (position, player, whiteToMove, limit) =>
            NpgsqlSubstrateReads.ChessPlayerMovesAsync(
                    ds, position.ToBytes(), player.ToBytes(), whiteToMove,
                    limit, CancellationToken.None)
                .GetAwaiter().GetResult()
                .Select(static row => new ChessPlayerMoveEvidence(
                    Hash128.FromBytes(row.NextPosition), row.Games, row.Score))
                .ToArray();
        _readContext = (player, context) =>
            NpgsqlConsensusCell.ReadAsync(
                    ds, player, ChessVocabulary.OutcomeType, context, CancellationToken.None)
                .GetAwaiter().GetResult();
    }

    internal ChessCompositePlayerBias(
        ChessPlayerModelExport export,
        Func<Hash128, Hash128, bool, int, IReadOnlyList<ChessPlayerMoveEvidence>> read,
        int capCp = 150,
        Func<Hash128, Hash128, NpgsqlConsensusCell.Row?>? readContext = null)
    {
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _readContext = readContext;
        _capCp = Math.Clamp(capCp, 0, 150);
    }

    public ChessPlayerModelExport Export => _export;

    /// <summary>
    /// Optional live clock/think lens (rushed, deep, planned_quick, pressed_think, flagging).
    /// Phase is always derived from the board. Null means no witnessed clock context is available;
    /// unknown is never rewritten as normal.
    /// </summary>
    public void SetThinkContext(string? context)
        => _thinkContext = string.IsNullOrWhiteSpace(context) ? null : context.Trim();

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0 || _capCp == 0) return bonus;
        Interlocked.Increment(ref _rootReads);

        Hash128 rootId = ChessCompose.PositionId(root);
        var moveByNext = new Dictionary<Hash128, int>(moves.Count);
        for (int i = 0; i < moves.Count; i++)
        {
            var next = root.Clone();
            MoveApply.Make(next, moves[i]);
            moveByNext[ChessCompose.PositionId(next)] = i;
        }

        var sums = new double[moves.Count];
        var contributors = new int[moves.Count];

        foreach (Hash128 member in _export.Members)
        {
            Interlocked.Increment(ref _memberReads);
            var rows = _read(rootId, member, root.WhiteToMove, moves.Count);
            double contextAuthority = ContextAuthority(root, member);
            bool memberContributed = false;
            foreach (var row in rows)
            {
                if (row.Games <= 0 || !double.IsFinite(row.Score)
                    || !moveByNext.TryGetValue(row.NextPosition, out int moveIndex))
                    continue;

                // chess.player_moves emits score in [0,1]: loss=0, draw=.5, win=1.
                // Shrink sparse exact-position evidence toward neutral before combining members.
                double centered = Math.Clamp((row.Score - 0.5d) * 2d, -1d, 1d);
                double confidence = 1d - 1d / Math.Sqrt(row.Games + 1d);
                sums[moveIndex] += centered * confidence * contextAuthority;
                contributors[moveIndex]++;
                memberContributed = true;
            }
            if (memberContributed)
                Interlocked.Increment(ref _membersWithEvidence);
        }

        long influenced = 0;
        for (int i = 0; i < bonus.Length; i++)
        {
            if (contributors[i] == 0) continue;
            // Equal member prior before context: a prolific player contributes stronger confidence
            // within a cell but cannot dominate merely by corpus size. A8 context can then alter
            // constituent authority by at most ±50%; it never bypasses the final ±cap envelope.
            double signal = sums[i] / contributors[i];
            bonus[i] = Math.Clamp(
                (int)Math.Round(signal * _capCp, MidpointRounding.AwayFromZero),
                -_capCp, _capCp);
            if (bonus[i] != 0) influenced++;
        }
        if (influenced != 0)
            Interlocked.Add(ref _movesInfluenced, influenced);
        return bonus;
    }

    private double ContextAuthority(Board root, Hash128 member)
    {
        if (_readContext is null) return 1d;

        string phaseContext = ChessCanonical.PhaseClass(root);
        double signal = 0;
        int cells = 0;
        Hash128 priorId = default;
        for (int i = 0; i < 2; i++)
        {
            string? surface = i == 0 ? phaseContext : _thinkContext;
            if (string.IsNullOrWhiteSpace(surface)) continue;
            if (ContentEmitter.RootId(surface) is not { } contextId) continue;
            if (i > 0 && contextId == priorId) continue;
            priorId = contextId;

            Interlocked.Increment(ref _contextReads);
            if (_readContext(member, contextId) is not { } row || row.WitnessCount <= 0)
                continue;
            Interlocked.Increment(ref _contextCells);

            // Compare conservative standing to the neutral-prior conservative standing, not
            // raw 1500. That keeps a sparse context from looking bad merely because its RD is
            // still wide. Witness saturation damps one-game extremes.
            double baseline = GlickoPriors.NeutralMu - 2d * GlickoPriors.InitialRd;
            double effective = row.Rating - 2d * row.Rd;
            double normalized = Math.Clamp(
                (effective - baseline) / (2d * GlickoPriors.InitialRd), -1d, 1d);
            double confidence = 1d - 1d / Math.Sqrt(row.WitnessCount + 1d);
            signal += normalized * confidence;
            cells++;
        }

        if (cells == 0) return 1d;
        return Math.Clamp(1d + 0.5d * (signal / cells), 0.5d, 1.5d);
    }

    public ChessPlayerModelReceipt Receipt() => new(
        ChessPlayerModelExport.Hex(_export.Id),
        ChessPlayerModelExport.Hex(_export.MemberSetId),
        _export.Members.Count,
        Volatile.Read(ref _rootReads),
        Volatile.Read(ref _memberReads),
        Volatile.Read(ref _membersWithEvidence),
        Volatile.Read(ref _movesInfluenced),
        Volatile.Read(ref _contextReads),
        Volatile.Read(ref _contextCells));
}

/// <summary>
/// Combines independent root evidence planes without increasing Search's steering authority.
/// The final clamp remains ±150cp, matching Search.RootBiasMargin's soundness contract.
/// </summary>
internal sealed class ChessCombinedRootBias(
    IRootBias global,
    IRootBias playerModel) : IRootBias
{
    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var left = global.Bonus(root, moves);
        var right = playerModel.Bonus(root, moves);
        var result = new int[moves.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = Math.Clamp(left[i] + right[i], -150, 150);
        return result;
    }
}

/// <summary>
/// Executable realization of a player-set export against the substrate it was exported from.
/// Exact chess law and the normal Laplace provider stack remain active; the player composition is
/// an additional typed root-evidence plane, not a replacement engine and not a hidden bestmove.
/// </summary>
public sealed class ChessPlayerModelRuntime
{
    private readonly ChessCompositePlayerBias _playerBias;
    private readonly IRootBias _rootBias;
    private readonly SubstrateBoardEvaluator _positionEvaluator;

    public ChessPlayerModelRuntime(NpgsqlDataSource ds, ChessPlayerModelExport export)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _playerBias = new ChessCompositePlayerBias(ds, export);
        _rootBias = new ChessCombinedRootBias(new SubstrateRootBias(ds), _playerBias);
        _positionEvaluator = new SubstrateBoardEvaluator(ds);
    }

    public ChessPlayerModelExport Export => _playerBias.Export;
    public ChessPlayerModelReceipt Receipt => _playerBias.Receipt();

    /// <summary>Pass a witnessed live clock lens into the player policy; null preserves unknown.</summary>
    public void SetThinkContext(string? context) => _playerBias.SetThinkContext(context);

    public Search BuildSearch(int ttBits = 20) => new(
        EvalTerm.All,
        _rootBias,
        ttBits,
        positionEvaluator: _positionEvaluator,
        tablebase: ChessTablebaseRuntime.ProbeSearch);
}

public sealed record ChessPlayerModelMatchResult(
    string WhiteExportId,
    string BlackExportId,
    GameOutcome Outcome,
    bool Adjudicated,
    IReadOnlyList<string> Moves,
    int Plies,
    ChessPlayerModelReceipt WhiteReceipt,
    ChessPlayerModelReceipt BlackReceipt);

/// <summary>
/// Direct export-vs-export proving surface. Both sides use the same chess law/search machinery;
/// only the selected composite player evidence differs.
/// </summary>
public static class ChessPlayerModelMatch
{
    public static ChessPlayerModelMatchResult Play(
        ChessPlayerModelRuntime white,
        ChessPlayerModelRuntime black,
        int depth = 4,
        int maxPlies = 400,
        string? startFen = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(white);
        ArgumentNullException.ThrowIfNull(black);
        depth = Math.Clamp(depth, 1, 12);
        maxPlies = Math.Max(1, maxPlies);

        var modality = new ChessModality();
        ChessState state = string.IsNullOrWhiteSpace(startFen)
            ? modality.Initial()
            : modality.FromFen(startFen);
        var whiteSearch = white.BuildSearch();
        var blackSearch = black.BuildSearch();
        var moves = new List<string>(Math.Min(maxPlies, 256));
        bool adjudicated = false;
        GameOutcome? terminal = modality.Terminal(state);

        while (terminal is null)
        {
            ct.ThrowIfCancellationRequested();
            if (moves.Count >= maxPlies)
            {
                adjudicated = true;
                terminal = GameOutcome.Draw;
                break;
            }

            Search search = state.Board.WhiteToMove ? whiteSearch : blackSearch;
            var result = search.Think(
                state,
                new Search.Limits(MaxDepth: depth, MaxTimeMs: 120_000),
                ct);
            if (result.BestMove is not { } move)
            {
                terminal = modality.Terminal(state) ?? GameOutcome.Draw;
                adjudicated = modality.Terminal(state) is null;
                break;
            }
            moves.Add(move.ToUci());
            state = modality.Apply(state, move);
            terminal = modality.Terminal(state);
        }

        return new ChessPlayerModelMatchResult(
            ChessPlayerModelExport.Hex(white.Export.Id),
            ChessPlayerModelExport.Hex(black.Export.Id),
            terminal ?? GameOutcome.Draw,
            adjudicated,
            moves,
            moves.Count,
            white.Receipt,
            black.Receipt);
    }
}
