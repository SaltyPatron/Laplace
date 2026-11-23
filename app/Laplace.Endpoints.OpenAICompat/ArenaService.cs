using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

#region Arena Contracts

public sealed record ArenaDecomposeNodeView(
    [property: JsonPropertyName("ordinal")] uint Ordinal,
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tier")] byte Tier);

public sealed record ArenaEntitySummary(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tier")] short? Tier,
    [property: JsonPropertyName("hub_type")] string? HubType,
    [property: JsonPropertyName("witnessed")] bool Witnessed,
    [property: JsonPropertyName("coord")] IReadOnlyList<double> Coord,
    [property: JsonPropertyName("record")] EntityRecordResponse? Record,
    [property: JsonPropertyName("nodes")] IReadOnlyList<ArenaDecomposeNodeView> Nodes);

public sealed record ArenaBridgeHopView(
    [property: JsonPropertyName("bridge_id")] string BridgeId,
    [property: JsonPropertyName("bridge_label")] string BridgeLabel,
    [property: JsonPropertyName("relation_a")] string RelationA,
    [property: JsonPropertyName("relation_b")] string RelationB,
    [property: JsonPropertyName("claim_id_a")] string? ClaimIdA,
    [property: JsonPropertyName("claim_id_b")] string? ClaimIdB,
    [property: JsonPropertyName("eff_mu")] double EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses,
    [property: JsonPropertyName("source")] string Source);

public sealed record ArenaWitnessShareView(
    [property: JsonPropertyName("witness_id")] string WitnessId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("trust")] double Trust,
    [property: JsonPropertyName("fisher_info")] double FisherInfo,
    [property: JsonPropertyName("games")] ulong Games,
    [property: JsonPropertyName("share_bp")] int ShareBp);

public sealed record ArenaCombineResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("side_a")] ArenaEntitySummary SideA,
    [property: JsonPropertyName("side_b")] ArenaEntitySummary SideB,
    [property: JsonPropertyName("novel_merkle_id")] string NovelMerkleId,
    [property: JsonPropertyName("midpoint_4d")] IReadOnlyList<double> Midpoint4d,
    [property: JsonPropertyName("midpoint_radius")] double MidpointRadius,
    [property: JsonPropertyName("distance_4d")] double Distance4d,
    [property: JsonPropertyName("bridges")] IReadOnlyList<ArenaBridgeHopView> Bridges,
    [property: JsonPropertyName("shared_tape")] IReadOnlyList<TapeRow> SharedTape,
    [property: JsonPropertyName("synthesis_summary")] string? SynthesisSummary,
    [property: JsonPropertyName("synthesis_firmware")] string? SynthesisFirmware,
    [property: JsonPropertyName("shares")] IReadOnlyList<ArenaWitnessShareView> Shares,
    [property: JsonPropertyName("shares_total_bp")] int SharesTotalBp);

public sealed record ArenaEntityRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("hub_type")] string? HubType,
    [property: JsonPropertyName("neighbors")] IReadOnlyList<MeshLink> Neighbors);

public sealed record ArenaGraphleRedactedNeighbor(
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("redacted_label")] string RedactedLabel,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record ArenaGraphleClues(
    [property: JsonPropertyName("tier")] short? Tier,
    [property: JsonPropertyName("mask_u64")] ulong MaskU64,
    [property: JsonPropertyName("mask_hex")] string MaskHex,
    [property: JsonPropertyName("highway_banks")] IReadOnlyList<string> HighwayBanks,
    [property: JsonPropertyName("quadrant")] string Quadrant,
    [property: JsonPropertyName("coord")] IReadOnlyList<double> Coord,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("label_length")] int LabelLength,
    [property: JsonPropertyName("redacted_pattern")] string RedactedPattern,
    [property: JsonPropertyName("hub_type")] string? HubType,
    [property: JsonPropertyName("confirmed")] long Confirmed,
    [property: JsonPropertyName("contested")] long Contested,
    [property: JsonPropertyName("refuted")] long Refuted,
    [property: JsonPropertyName("thin")] long Thin,
    [property: JsonPropertyName("redacted_neighbors")] IReadOnlyList<ArenaGraphleRedactedNeighbor> RedactedNeighbors);

public sealed record ArenaWitnessCandidate(
    [property: JsonPropertyName("witness_id")] string WitnessId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record ArenaProvenanceDetectiveClue(
    [property: JsonPropertyName("claim_entity_id")] string ClaimEntityId,
    [property: JsonPropertyName("claim_entity_label")] string ClaimEntityLabel,
    [property: JsonPropertyName("claim_summary")] string ClaimSummary,
    [property: JsonPropertyName("record")] EntityRecordResponse? Record,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ArenaWitnessCandidate> Candidates);

public sealed record ArenaChallengeResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("seed")] string Seed,
    [property: JsonPropertyName("challenge_token")] string ChallengeToken,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("start")] ArenaEntityRef? Start = null,
    [property: JsonPropertyName("target")] ArenaEntityRef? Target = null,
    [property: JsonPropertyName("optimal_hint_hops")] int? OptimalHintHops = null,
    [property: JsonPropertyName("graphle")] ArenaGraphleClues? Graphle = null,
    [property: JsonPropertyName("detective")] ArenaProvenanceDetectiveClue? Detective = null);

public sealed record ArenaHighwayStepResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("current_id")] string CurrentId,
    [property: JsonPropertyName("next_id")] string NextId,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("step_count")] int StepCount,
    [property: JsonPropertyName("reached_target")] bool ReachedTarget,
    [property: JsonPropertyName("next_entity")] ArenaEntityRef? NextEntity,
    [property: JsonPropertyName("target_record")] EntityRecordResponse? TargetRecord,
    [property: JsonPropertyName("shares")] IReadOnlyList<ArenaWitnessShareView> Shares,
    [property: JsonPropertyName("shares_total_bp")] int SharesTotalBp,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ErrorBody? Error = null);

public sealed record ArenaVerifyResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("seed")] string Seed,
    [property: JsonPropertyName("correct")] bool Correct,
    [property: JsonPropertyName("target_id")] string? TargetId,
    [property: JsonPropertyName("target_label")] string? TargetLabel,
    [property: JsonPropertyName("consensus_rating")] double ConsensusRating,
    [property: JsonPropertyName("guess_id")] string? GuessId,
    [property: JsonPropertyName("distance_4d")] double? Distance4d,
    [property: JsonPropertyName("shares")] IReadOnlyList<ArenaWitnessShareView> Shares,
    [property: JsonPropertyName("shares_total_bp")] int SharesTotalBp);

public sealed record ArenaBlindSlotView(
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("prompt_id")] string PromptId,
    [property: JsonPropertyName("response_id")] string? ResponseId,
    [property: JsonPropertyName("top_salience")] double TopSalience,
    [property: JsonPropertyName("mean_confidence")] double MeanConfidence,
    [property: JsonPropertyName("wall_us")] ulong WallUs,
    [property: JsonPropertyName("db_probes")] ulong DbProbes,
    [property: JsonPropertyName("tokens_out")] uint TokensOut,
    [property: JsonPropertyName("ooda_rounds_run")] int OodaRoundsRun,
    [property: JsonPropertyName("ooda_verified")] bool OodaVerified);

public sealed record ArenaBlindRevealSide(
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("firmware")] string Firmware,
    [property: JsonPropertyName("firmware_id")] string? FirmwareId,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("shares")] IReadOnlyList<ArenaWitnessShareView> Shares,
    [property: JsonPropertyName("shares_total_bp")] int SharesTotalBp);

public sealed record ArenaBlindMatchupResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("left")] ArenaBlindSlotView Left,
    [property: JsonPropertyName("right")] ArenaBlindSlotView Right,
    [property: JsonPropertyName("reveal_left")] ArenaBlindRevealSide RevealLeft,
    [property: JsonPropertyName("reveal_right")] ArenaBlindRevealSide RevealRight,
    [property: JsonPropertyName("swapped")] bool Swapped);

#endregion

/// <summary>
/// Live substrate service for the Knowledge Arena and Constructive Discovery (<c>COMBINE(A, B)</c>).
/// Every challenge, hop validation, clue, bridge, and blind matchup reads directly from the live substrate.
/// </summary>
internal sealed class ArenaService
{
    private const int ShareBpTotal = 10000;

    private static readonly string[] FallbackProbeTerms =
    [
        "whale", "cetacean", "dog", "chess", "king",
        "water", "light", "tree", "star", "music",
    ];

    private readonly ISubstrateClient _substrate;
    private readonly ExploreDecomposeService _decompose;
    private readonly IConversationTurns _turns;

    public ArenaService(
        ISubstrateClient substrate,
        ExploreDecomposeService decompose,
        IConversationTurns turns)
    {
        _substrate = substrate ?? throw new ArgumentNullException(nameof(substrate));
        _decompose = decompose ?? throw new ArgumentNullException(nameof(decompose));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
    }

    private sealed record CandidateEntity(string IdHex, string Label, double Standing, long Witnesses);

    private sealed record ResolvedSide(
        string Input,
        string IdHex,
        Hash128 Hash,
        string Label,
        short? Tier,
        double[] Coord,
        IReadOnlyList<ArenaDecomposeNodeView> Nodes);

    private sealed record RawWitnessSource(
        string WitnessId,
        string Label,
        double Trust,
        double FisherInfo,
        ulong Games,
        double Weight);

    private sealed record DetectiveSelection(
        CandidateEntity ClaimEntity,
        string ClaimSummary,
        EntityRecordResponse? Record,
        RawWitnessSource TrueWitness,
        IReadOnlyList<ArenaWitnessCandidate> Candidates,
        IReadOnlyList<RawWitnessSource> AllWitnesses);

    public async Task<ArenaCombineResponse> CombineAsync(string a, string b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(a))
            throw new ArgumentException("Concept A must be non-empty.", nameof(a));
        if (string.IsNullOrWhiteSpace(b))
            throw new ArgumentException("Concept B must be non-empty.", nameof(b));

        var trimmedA = a.Trim();
        var trimmedB = b.Trim();

        var sideAInfo = await ResolveSideAsync(trimmedA, ct).ConfigureAwait(false);
        var sideBInfo = await ResolveSideAsync(trimmedB, ct).ConfigureAwait(false);

        var matchupTask = TrySubstrateAsync(() => _substrate.MatchupAsync(trimmedA, trimmedB, ct), ct);
        var fieldTask = TrySubstrateAsync(() => _substrate.ResponseFieldAsync(trimmedA, trimmedB, 256, 12, ct), ct);
        var meshATask = TrySubstrateAsync(() => _substrate.MeshAsync(sideAInfo.IdHex, 32, 32, ct), ct);
        var meshBTask = TrySubstrateAsync(() => _substrate.MeshAsync(sideBInfo.IdHex, 32, 32, ct), ct);
        var entityATask = TrySubstrateAsync(() => _substrate.ExploreEntityAsync(sideAInfo.IdHex, 40, 20, ct), ct);
        var entityBTask = TrySubstrateAsync(() => _substrate.ExploreEntityAsync(sideBInfo.IdHex, 40, 20, ct), ct);
        var recordATask = TrySubstrateAsync(() => _substrate.EntityRecordAsync(sideAInfo.IdHex, ct), ct);
        var recordBTask = TrySubstrateAsync(() => _substrate.EntityRecordAsync(sideBInfo.IdHex, ct), ct);

        await Task.WhenAll(
            matchupTask, fieldTask, meshATask, meshBTask,
            entityATask, entityBTask, recordATask, recordBTask).ConfigureAwait(false);

        var matchup = matchupTask.Result;
        var field = fieldTask.Result;
        var meshA = meshATask.Result;
        var meshB = meshBTask.Result;
        var entityA = entityATask.Result;
        var entityB = entityBTask.Result;
        var recordA = matchup?.X.Record ?? recordATask.Result;
        var recordB = matchup?.Y.Record ?? recordBTask.Result;

        var coordA = SelectCoord(sideAInfo.Coord, entityA);
        var coordB = SelectCoord(sideBInfo.Coord, entityB);
        var midpoint = ComputeMidpoint4d(coordA, coordB);
        double midpointRadius = Norm4d(midpoint);
        double distance4d = Distance4d(coordA, coordB);

        var novelMerkleId = ComposeMerklePair(sideAInfo.Hash, sideBInfo.Hash).ToHex();

        string labelA = BestLabel(sideAInfo.Label, matchup?.X.Label, entityA?.Label, meshA?.Label, sideAInfo.IdHex);
        string labelB = BestLabel(sideBInfo.Label, matchup?.Y.Label, entityB?.Label, meshB?.Label, sideBInfo.IdHex);

        bool witnessedA = entityA?.Exists == true
            || (meshA is not null && (meshA.BelongsTo.Count > 0 || meshA.Roster.Count > 0))
            || (recordA is not null && (recordA.Confirmed + recordA.Contested + recordA.Refuted + recordA.Thin) > 0);
        bool witnessedB = entityB?.Exists == true
            || (meshB is not null && (meshB.BelongsTo.Count > 0 || meshB.Roster.Count > 0))
            || (recordB is not null && (recordB.Confirmed + recordB.Contested + recordB.Refuted + recordB.Thin) > 0);

        var bridges = ExtractBridges(
            sideAInfo.IdHex, labelA,
            sideBInfo.IdHex, labelB,
            meshA, meshB, field, matchup);

        var sharedTape = (matchup?.Tape ?? Array.Empty<TapeRow>())
            .Where(static t => string.Equals(t.Holder, "both", StringComparison.OrdinalIgnoreCase))
            .Take(24)
            .ToArray();

        string? synthesisSummary = null;
        string? synthesisFirmware = null;
        IReadOnlyList<EngineWitnessShare>? turnShares = null;

        if (_turns.Unavailable is null)
        {
            var bridgeSnippet = bridges.Count > 0
                ? $" Witnessed bridge candidates: {string.Join(", ", bridges.Take(4).Select(br => br.BridgeLabel))}."
                : string.Empty;
            var synthPrompt =
                $"Synthesize the constructive bridge between \"{labelA}\" ({sideAInfo.IdHex}) and \"{labelB}\" ({sideBInfo.IdHex}).{bridgeSnippet}";

            try
            {
                var turnReq = new EngineTurnRequest(
                    FirmwarePath: null,
                    ModelName: "constructive",
                    Prompt: synthPrompt,
                    Messages: null,
                    SystemPrompt: null,
                    User: "arena-combine",
                    Session: $"arena-combine-{sideAInfo.IdHex[..8]}-{sideBInfo.IdHex[..8]}",
                    ReadOnly: true,
                    MaxTokens: 160,
                    Temperature: 0.0,
                    StopSequences: null,
                    Seed: 0,
                    Authority: new EngineAuthorityScope(
                        Scoped: false,
                        Capabilities: (uint)LaplaceNative.LP_CAP_READ,
                        AllowWrite: false),
                    Envelope: null);

                var receipt = await _turns.TurnAsync(turnReq, "fast", 150_000, null, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(receipt.Response))
                {
                    synthesisSummary = receipt.Response.Trim();
                    synthesisFirmware = "constructive";
                }
                if (receipt.AbiResult?.Shares is { Count: > 0 } abiShares)
                    turnShares = abiShares;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
            {
                // Synthesis enrichment is optional when native turn execution is unavailable.
            }
        }

        var shares = BuildCombineShares(turnShares, entityA, entityB, bridges, matchup);
        int sharesTotalBp = shares.Sum(static s => s.ShareBp);

        var summaryA = new ArenaEntitySummary(
            IdHex: sideAInfo.IdHex,
            Label: labelA,
            Tier: entityA?.Tier ?? sideAInfo.Tier,
            HubType: entityA?.Type ?? meshA?.HubType,
            Witnessed: witnessedA,
            Coord: coordA,
            Record: recordA,
            Nodes: sideAInfo.Nodes);

        var summaryB = new ArenaEntitySummary(
            IdHex: sideBInfo.IdHex,
            Label: labelB,
            Tier: entityB?.Tier ?? sideBInfo.Tier,
            HubType: entityB?.Type ?? meshB?.HubType,
            Witnessed: witnessedB,
            Coord: coordB,
            Record: recordB,
            Nodes: sideBInfo.Nodes);

        return new ArenaCombineResponse(
            Object: "laplace.arena.combine",
            SideA: summaryA,
            SideB: summaryB,
            NovelMerkleId: novelMerkleId,
            Midpoint4d: midpoint,
            MidpointRadius: midpointRadius,
            Distance4d: distance4d,
            Bridges: bridges,
            SharedTape: sharedTape,
            SynthesisSummary: synthesisSummary,
            SynthesisFirmware: synthesisFirmware,
            Shares: shares,
            SharesTotalBp: sharesTotalBp);
    }

    public async Task<ArenaChallengeResponse> GetChallengeAsync(string? mode, string? seed, CancellationToken ct)
    {
        string normalizedMode = NormalizeMode(mode);
        string normalizedSeed = NormalizeSeed(seed);
        string token = ComputeChallengeToken(normalizedMode, normalizedSeed);

        var pool = await LoadCandidatePoolAsync(ct).ConfigureAwait(false);
        if (pool.Count == 0)
            throw new SubstrateUnavailableException("No substrate entities are available for an Arena challenge.");

        switch (normalizedMode)
        {
            case "highway_race":
                return await BuildHighwayChallengeAsync(normalizedSeed, token, pool, ct).ConfigureAwait(false);
            case "graphle":
                return await BuildGraphleChallengeAsync(normalizedSeed, token, pool, ct).ConfigureAwait(false);
            case "provenance_detective":
                return await BuildProvenanceDetectiveChallengeAsync(normalizedSeed, token, pool, ct).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Unsupported arena challenge mode '{mode}'.", nameof(mode));
        }
    }

    public async Task<ArenaHighwayStepResponse> HighwayStepAsync(
        string currentId, string nextId, string targetId, int stepCount, CancellationToken ct)
    {
        if (!Hash128.TryParseHex(currentId?.Trim(), out var currentHash))
            throw new ArgumentException("Field 'current_id' must be a 32-hex entity id.", nameof(currentId));
        if (!Hash128.TryParseHex(nextId?.Trim(), out var nextHash))
            throw new ArgumentException("Field 'next_id' must be a 32-hex entity id.", nameof(nextId));
        if (!Hash128.TryParseHex(targetId?.Trim(), out var targetHash))
            throw new ArgumentException("Field 'target_id' must be a 32-hex entity id.", nameof(targetId));

        string currentHex = currentHash.ToHex();
        string nextHex = nextHash.ToHex();
        string targetHex = targetHash.ToHex();
        int safeSteps = Math.Max(0, stepCount);

        var currentMesh = await _substrate.MeshAsync(currentHex, 80, 80, ct).ConfigureAwait(false);
        var currentNeighbors = CombineMeshLinks(currentMesh);

        bool isNeighbor = !string.Equals(currentHex, nextHex, StringComparison.OrdinalIgnoreCase)
            && currentNeighbors.Any(link => string.Equals(link.Id, nextHex, StringComparison.OrdinalIgnoreCase));

        if (!isNeighbor)
        {
            string msg = string.Equals(currentHex, nextHex, StringComparison.OrdinalIgnoreCase)
                ? "Hop target must differ from the current entity."
                : $"Entity '{nextHex}' is not an attested neighbor or member in '{currentHex}'s mesh.";

            return new ArenaHighwayStepResponse(
                Object: "laplace.arena.highway_step",
                Valid: false,
                Message: msg,
                CurrentId: currentHex,
                NextId: nextHex,
                TargetId: targetHex,
                StepCount: safeSteps,
                ReachedTarget: false,
                NextEntity: null,
                TargetRecord: null,
                Shares: Array.Empty<ArenaWitnessShareView>(),
                SharesTotalBp: 0,
                Error: new ErrorBody("invalid_request_error", "invalid_highway_hop", msg));
        }

        var nextMesh = await _substrate.MeshAsync(nextHex, 80, 80, ct).ConfigureAwait(false);
        var nextNeighbors = CombineMeshLinks(nextMesh);
        var nextEntityRef = new ArenaEntityRef(
            Id: nextHex,
            Label: BestLabel(nextMesh?.Label, nextHex),
            HubType: nextMesh?.HubType,
            Neighbors: nextNeighbors);

        bool reachedTarget = string.Equals(nextHex, targetHex, StringComparison.OrdinalIgnoreCase);
        EntityRecordResponse? targetRecord = null;
        IReadOnlyList<ArenaWitnessShareView> shares = Array.Empty<ArenaWitnessShareView>();

        if (reachedTarget)
        {
            var recordTask = TrySubstrateAsync(() => _substrate.EntityRecordAsync(targetHex, ct), ct);
            var entityTask = TrySubstrateAsync(() => _substrate.ExploreEntityAsync(targetHex, 30, 30, ct), ct);
            await Task.WhenAll(recordTask, entityTask).ConfigureAwait(false);

            targetRecord = recordTask.Result;
            shares = BuildEntityShares(entityTask.Result, nextNeighbors);
        }

        return new ArenaHighwayStepResponse(
            Object: "laplace.arena.highway_step",
            Valid: true,
            Message: reachedTarget ? "Target entity reached via attested highway hops." : "Valid hop recorded.",
            CurrentId: currentHex,
            NextId: nextHex,
            TargetId: targetHex,
            StepCount: safeSteps + 1,
            ReachedTarget: reachedTarget,
            NextEntity: nextEntityRef,
            TargetRecord: targetRecord,
            Shares: shares,
            SharesTotalBp: shares.Sum(static s => s.ShareBp));
    }

    public async Task<ArenaVerifyResponse> VerifyChallengeAsync(
        string mode, string? seed, string guess, CancellationToken ct, bool reveal = false)
    {
        string normalizedMode = NormalizeMode(mode);
        string normalizedSeed = NormalizeSeed(seed);
        string trimmedGuess = (guess ?? string.Empty).Trim();

        if (!reveal && string.IsNullOrWhiteSpace(trimmedGuess))
            throw new ArgumentException("Field 'guess' is required unless 'reveal' is true.", nameof(guess));

        var pool = await LoadCandidatePoolAsync(ct).ConfigureAwait(false);
        if (pool.Count == 0)
            throw new SubstrateUnavailableException("No substrate entities are available for challenge verification.");

        if (normalizedMode == "provenance_detective")
        {
            var selection = await SelectDetectiveAsync(normalizedSeed, pool, ct).ConfigureAwait(false);
            bool matched = !string.IsNullOrWhiteSpace(trimmedGuess) && (
                string.Equals(trimmedGuess, selection.TrueWitness.WitnessId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedGuess, selection.TrueWitness.Label, StringComparison.OrdinalIgnoreCase));

            var shares = NormalizeShares(selection.AllWitnesses);
            bool disclose = matched || reveal;
            return new ArenaVerifyResponse(
                Object: "laplace.arena.verify",
                Mode: normalizedMode,
                Seed: normalizedSeed,
                Correct: matched,
                TargetId: disclose ? selection.TrueWitness.WitnessId : null,
                TargetLabel: disclose ? selection.TrueWitness.Label : null,
                ConsensusRating: selection.TrueWitness.FisherInfo,
                GuessId: Hash128.TryParseHex(trimmedGuess, out var gHex) ? gHex.ToHex() : null,
                Distance4d: null,
                Shares: disclose ? shares : Array.Empty<ArenaWitnessShareView>(),
                SharesTotalBp: disclose ? shares.Sum(static s => s.ShareBp) : 0);
        }

        CandidateEntity target;
        if (normalizedMode == "graphle")
        {
            target = SelectGraphleTarget(normalizedSeed, pool);
        }
        else
        {
            var hw = await SelectHighwayChallengeAsync(normalizedSeed, pool, ct).ConfigureAwait(false);
            target = hw.Target;
        }

        string? guessIdHex = null;
        double[]? guessCoord = null;
        if (!string.IsNullOrWhiteSpace(trimmedGuess))
        {
            if (Hash128.TryParseHex(trimmedGuess, out var parsedGuess))
            {
                guessIdHex = parsedGuess.ToHex();
                var gEnt = await TrySubstrateAsync(() => _substrate.ExploreEntityAsync(guessIdHex, 4, 4, ct), ct).ConfigureAwait(false);
                if (gEnt?.Physicalities is { Count: > 0 } phys)
                    guessCoord = [phys[0].X, phys[0].Y, phys[0].Z, phys[0].M];
            }
            else
            {
                var gSide = await ResolveSideAsync(trimmedGuess, ct).ConfigureAwait(false);
                guessIdHex = gSide.IdHex;
                guessCoord = gSide.Coord;
            }
        }

        bool correct = !string.IsNullOrWhiteSpace(trimmedGuess) && (
            string.Equals(trimmedGuess, target.Label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmedGuess, target.IdHex, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guessIdHex, target.IdHex, StringComparison.OrdinalIgnoreCase));

        var targetEntity = await TrySubstrateAsync(() => _substrate.ExploreEntityAsync(target.IdHex, 24, 24, ct), ct).ConfigureAwait(false);
        var targetMesh = await TrySubstrateAsync(() => _substrate.MeshAsync(target.IdHex, 24, 24, ct), ct).ConfigureAwait(false);

        double[] targetCoord = await ResolveTargetCoordAsync(target, targetEntity, ct).ConfigureAwait(false);
        double? distance4d = guessCoord is not null ? Distance4d(guessCoord, targetCoord) : null;

        var entityShares = BuildEntityShares(targetEntity, CombineMeshLinks(targetMesh));
        double rating = ComputeConsensusRating(target, targetEntity);
        bool showAnswer = correct || reveal;

        return new ArenaVerifyResponse(
            Object: "laplace.arena.verify",
            Mode: normalizedMode,
            Seed: normalizedSeed,
            Correct: correct,
            TargetId: showAnswer ? target.IdHex : null,
            TargetLabel: showAnswer ? target.Label : null,
            ConsensusRating: rating,
            GuessId: guessIdHex,
            Distance4d: distance4d,
            Shares: showAnswer ? entityShares : Array.Empty<ArenaWitnessShareView>(),
            SharesTotalBp: showAnswer ? entityShares.Sum(static s => s.ShareBp) : 0);
    }

    public async Task<ArenaBlindMatchupResponse> BlindMatchupAsync(
        string prompt,
        string firmwareA,
        string firmwareB,
        EngineAuthorityScope authority,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Field 'prompt' is required.", nameof(prompt));

        string normA = NormalizeFirmware(firmwareA, nameof(firmwareA));
        string normB = NormalizeFirmware(firmwareB, nameof(firmwareB));

        if (_turns.Unavailable is { } why)
            throw new SubstrateUnavailableException("Native engine turns are unavailable: " + why);

        var safeScope = authority ?? new EngineAuthorityScope(
            Witnesses: null,
            Scoped: false,
            Capabilities: (uint)LaplaceNative.LP_CAP_READ,
            Disposition: (int)LaplaceNative.LP_DISPOSE_ALLOW,
            MinTrust: 0.0,
            AllowWrite: false);

        string trimmedPrompt = prompt.Trim();

        NativeTurnReceipt receiptA = await ExecuteReadOnlyTurnAsync(normA, trimmedPrompt, safeScope, ct).ConfigureAwait(false);
        NativeTurnReceipt receiptB = await ExecuteReadOnlyTurnAsync(normB, trimmedPrompt, safeScope, ct).ConfigureAwait(false);

        bool swapped = RandomNumberGenerator.GetInt32(2) == 1;
        var (leftFw, leftReceipt) = swapped ? (normB, receiptB) : (normA, receiptA);
        var (rightFw, rightReceipt) = swapped ? (normA, receiptA) : (normB, receiptB);

        return new ArenaBlindMatchupResponse(
            Object: "laplace.arena.blind_matchup",
            Prompt: trimmedPrompt,
            Left: ToBlindSlot("left", leftReceipt),
            Right: ToBlindSlot("right", rightReceipt),
            RevealLeft: ToBlindReveal("left", leftFw, leftReceipt),
            RevealRight: ToBlindReveal("right", rightFw, rightReceipt),
            Swapped: swapped);
    }

    #region Private Helpers

    private async Task<NativeTurnReceipt> ExecuteReadOnlyTurnAsync(
        string firmware, string prompt, EngineAuthorityScope authority, CancellationToken ct)
    {
        var req = new EngineTurnRequest(
            FirmwarePath: null,
            ModelName: firmware,
            Prompt: prompt,
            Messages: null,
            SystemPrompt: null,
            User: "arena-matchup",
            Session: $"arena-blind-{Guid.NewGuid():N}",
            ReadOnly: true,
            MaxTokens: 256,
            Temperature: 0.0,
            StopSequences: null,
            Seed: 0L,
            Authority: authority,
            Envelope: null);

        try
        {
            return await _turns.TurnAsync(req, "standard", 1_000_000, null, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new SubstrateUnavailableException($"Blind matchup turn failed for firmware '{firmware}': {ex.Message}", ex);
        }
    }

    private static ArenaBlindSlotView ToBlindSlot(string slot, NativeTurnReceipt receipt)
    {
        var abi = receipt.AbiResult;
        return new ArenaBlindSlotView(
            Slot: slot,
            Response: receipt.Response,
            PromptId: receipt.PromptId,
            ResponseId: receipt.ResponseId,
            TopSalience: abi?.TopSalience ?? 0.0,
            MeanConfidence: abi?.MeanConfidence ?? 0.0,
            WallUs: abi?.Work.wall_us ?? 0UL,
            DbProbes: abi?.Work.db_probes ?? 0UL,
            TokensOut: abi?.Work.tokens_out ?? 0u,
            OodaRoundsRun: abi?.OodaRoundsRun ?? 0,
            OodaVerified: abi?.OodaVerified ?? false);
    }

    private static ArenaBlindRevealSide ToBlindReveal(string slot, string firmware, NativeTurnReceipt receipt)
    {
        var abi = receipt.AbiResult;
        var shares = FromAbiShares(abi?.Shares);
        return new ArenaBlindRevealSide(
            Slot: slot,
            Firmware: firmware,
            FirmwareId: abi is null || abi.FirmwareId == Hash128.Zero ? null : abi.FirmwareId.ToHex(),
            Disposition: !string.IsNullOrWhiteSpace(receipt.Disposition) ? receipt.Disposition : "accepted",
            Shares: shares,
            SharesTotalBp: shares.Sum(static s => s.ShareBp));
    }

    private sealed record HighwaySelection(
        CandidateEntity Start,
        MeshResponse? StartMesh,
        IReadOnlyList<MeshLink> StartNeighbors,
        CandidateEntity Target,
        string? TargetHubType,
        int? HintHops);

    private async Task<HighwaySelection> SelectHighwayChallengeAsync(
        string seed, IReadOnlyList<CandidateEntity> pool, CancellationToken ct)
    {
        var start = SelectDeterministic(pool, $"highway_race_start:{seed}");
        var startMesh = await TrySubstrateAsync(() => _substrate.MeshAsync(start.IdHex, 40, 40, ct), ct).ConfigureAwait(false);
        var startNeighbors = CombineMeshLinks(startMesh);

        CandidateEntity? chosenTarget = null;
        string? targetHubType = null;
        int? hintHops = null;

        // Prefer a genuine 2-hop target reachable from start's mesh so the path is navigable.
        var hop1Candidates = startNeighbors
            .Where(l => !string.Equals(l.Id, start.IdHex, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => (double)(l.EffMu ?? 0m))
            .ThenBy(l => l.Id, StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        if (hop1Candidates.Length > 0)
        {
            var startNeighborIds = hop1Candidates.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hop1Pick = hop1Candidates[(int)(SeedHashU64($"highway_hop1:{seed}") % (ulong)hop1Candidates.Length)];
            var hop1Mesh = await TrySubstrateAsync(() => _substrate.MeshAsync(hop1Pick.Id, 40, 40, ct), ct).ConfigureAwait(false);
            var hop2Links = CombineMeshLinks(hop1Mesh)
                .Where(l => !string.Equals(l.Id, start.IdHex, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(l.Id, hop1Pick.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var twoHopOnly = hop2Links
                .Where(l => !startNeighborIds.Contains(l.Id))
                .ToArray();
            var pickedHop2 = twoHopOnly.Length > 0
                ? twoHopOnly[(int)(SeedHashU64($"highway_hop2:{seed}") % (ulong)twoHopOnly.Length)]
                : hop2Links.Length > 0
                    ? hop2Links[(int)(SeedHashU64($"highway_hop2:{seed}") % (ulong)hop2Links.Length)]
                    : null;

            if (pickedHop2 is not null)
            {
                chosenTarget = new CandidateEntity(
                    pickedHop2.Id,
                    BestLabel(pickedHop2.Label, pickedHop2.Id),
                    (double)(pickedHop2.EffMu ?? 0m),
                    pickedHop2.Witnesses);
                targetHubType = pickedHop2.HubType;
                hintHops = twoHopOnly.Length > 0 ? 2 : 1;
            }
            else
            {
                chosenTarget = new CandidateEntity(
                    hop1Pick.Id,
                    BestLabel(hop1Pick.Label, hop1Pick.Id),
                    (double)(hop1Pick.EffMu ?? 0m),
                    hop1Pick.Witnesses);
                targetHubType = hop1Pick.HubType;
                hintHops = 1;
            }
        }

        if (chosenTarget is null)
        {
            var others = pool.Where(p => !string.Equals(p.IdHex, start.IdHex, StringComparison.OrdinalIgnoreCase)).ToArray();
            chosenTarget = others.Length > 0
                ? SelectDeterministic(others, $"highway_race_target:{seed}")
                : start;
        }

        return new HighwaySelection(start, startMesh, startNeighbors, chosenTarget, targetHubType, hintHops);
    }

    private async Task<ArenaChallengeResponse> BuildHighwayChallengeAsync(
        string seed, string token, IReadOnlyList<CandidateEntity> pool, CancellationToken ct)
    {
        var hw = await SelectHighwayChallengeAsync(seed, pool, ct).ConfigureAwait(false);

        return new ArenaChallengeResponse(
            Object: "laplace.arena.challenge",
            Mode: "highway_race",
            Seed: seed,
            ChallengeToken: token,
            Instructions: "Navigate from Start (S) to Target (T) by stepping through attested mesh neighbors. Every hop is verified live against the substrate mesh.",
            Start: new ArenaEntityRef(
                Id: hw.Start.IdHex,
                Label: BestLabel(hw.StartMesh?.Label, hw.Start.Label, hw.Start.IdHex),
                HubType: hw.StartMesh?.HubType,
                Neighbors: hw.StartNeighbors),
            Target: new ArenaEntityRef(
                Id: hw.Target.IdHex,
                Label: hw.Target.Label,
                HubType: hw.TargetHubType,
                Neighbors: Array.Empty<MeshLink>()),
            OptimalHintHops: hw.HintHops);
    }

    private async Task<ArenaChallengeResponse> BuildGraphleChallengeAsync(
        string seed, string token, IReadOnlyList<CandidateEntity> pool, CancellationToken ct)
    {
        var target = SelectGraphleTarget(seed, pool);
        var entityTask = TrySubstrateAsync(() => _substrate.ExploreEntityAsync(target.IdHex, 24, 16, ct), ct);
        var meshTask = TrySubstrateAsync(() => _substrate.MeshAsync(target.IdHex, 24, 24, ct), ct);
        var recordTask = TrySubstrateAsync(() => _substrate.EntityRecordAsync(target.IdHex, ct), ct);
        await Task.WhenAll(entityTask, meshTask, recordTask).ConfigureAwait(false);

        var entity = entityTask.Result;
        var mesh = meshTask.Result;
        var record = recordTask.Result;

        double[] coord = await ResolveTargetCoordAsync(target, entity, ct).ConfigureAwait(false);
        double radius = Norm4d(coord);
        string quadrant = FormatQuadrant(coord);

        short? tier = entity?.Tier;
        if (tier is null && !LooksLikeHex(target.Label))
        {
            try
            {
                var dec = _decompose.Decompose(target.Label);
                if (dec.Nodes.Count > 0)
                    tier = dec.Nodes.Max(static n => n.Tier);
            }
            catch (InvalidOperationException)
            {
                // Perfcache unavailable; tier remains null.
            }
        }

        var (maskU64, highwayBanks) = InspectHighwayMask(target.IdHex, entity, mesh);

        var redactedNeighbors = new List<ArenaGraphleRedactedNeighbor>();
        foreach (var link in CombineMeshLinks(mesh).Take(8))
        {
            // Never expose a neighbor whose label equals or directly gives away the raw target hex/label.
            string safeLabel = RedactText(link.Label, target.Label, target.IdHex);
            string safeRel = RedactText(link.Relation, target.Label, target.IdHex);
            redactedNeighbors.Add(new ArenaGraphleRedactedNeighbor(
                Relation: safeRel,
                RedactedLabel: safeLabel,
                EffMu: link.EffMu,
                Witnesses: link.Witnesses));
        }

        string redactedPattern = string.Join(' ', target.Label
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string('•', w.Length)));

        // Intentionally omit target ID and target label from the initial payload.
        return new ArenaChallengeResponse(
            Object: "laplace.arena.challenge",
            Mode: "graphle",
            Seed: seed,
            ChallengeToken: token,
            Instructions: "Deduce the hidden substrate entity from its tier, 64-bit highway mask, 4D quadrant coordinates, and redacted mesh neighborhood. Verification happens server-side.",
            Graphle: new ArenaGraphleClues(
                Tier: tier,
                MaskU64: maskU64,
                MaskHex: $"0x{maskU64:x16}",
                HighwayBanks: highwayBanks,
                Quadrant: quadrant,
                Coord: coord,
                Radius: radius,
                LabelLength: target.Label.Length,
                RedactedPattern: string.IsNullOrEmpty(redactedPattern) ? "••••" : redactedPattern,
                HubType: entity?.Type ?? mesh?.HubType,
                Confirmed: record?.Confirmed ?? 0,
                Contested: record?.Contested ?? 0,
                Refuted: record?.Refuted ?? 0,
                Thin: record?.Thin ?? 0,
                RedactedNeighbors: redactedNeighbors));
    }

    private async Task<ArenaChallengeResponse> BuildProvenanceDetectiveChallengeAsync(
        string seed, string token, IReadOnlyList<CandidateEntity> pool, CancellationToken ct)
    {
        var selection = await SelectDetectiveAsync(seed, pool, ct).ConfigureAwait(false);
        return new ArenaChallengeResponse(
            Object: "laplace.arena.challenge",
            Mode: "provenance_detective",
            Seed: seed,
            ChallengeToken: token,
            Instructions: "Inspect the attested claim and choose the true highest-standing witness from the candidate sources below.",
            Detective: new ArenaProvenanceDetectiveClue(
                ClaimEntityId: selection.ClaimEntity.IdHex,
                ClaimEntityLabel: selection.ClaimEntity.Label,
                ClaimSummary: selection.ClaimSummary,
                Record: selection.Record,
                Candidates: selection.Candidates));
    }

    private async Task<DetectiveSelection> SelectDetectiveAsync(
        string seed, IReadOnlyList<CandidateEntity> pool, CancellationToken ct)
    {
        var claimEntity = SelectDeterministic(pool, $"provenance_detective_claim:{seed}");
        var entityTask = TrySubstrateAsync(() => _substrate.ExploreEntityAsync(claimEntity.IdHex, 30, 30, ct), ct);
        var meshTask = TrySubstrateAsync(() => _substrate.MeshAsync(claimEntity.IdHex, 30, 30, ct), ct);
        var recordTask = TrySubstrateAsync(() => _substrate.EntityRecordAsync(claimEntity.IdHex, ct), ct);
        var catalogTask = TrySubstrateAsync(() => _substrate.ExploreCatalogAsync(ct), ct);
        await Task.WhenAll(entityTask, meshTask, recordTask, catalogTask).ConfigureAwait(false);

        var entity = entityTask.Result;
        var mesh = meshTask.Result;
        var record = recordTask.Result;
        var catalog = catalogTask.Result;

        var realWitnesses = ExtractEntityWitnessSources(entity, CombineMeshLinks(mesh));
        if (realWitnesses.Count == 0)
        {
            realWitnesses.Add(new RawWitnessSource(
                WitnessId: claimEntity.IdHex,
                Label: claimEntity.Label,
                Trust: 0.75,
                FisherInfo: claimEntity.Standing > 0 ? claimEntity.Standing : 1500.0,
                Games: (ulong)Math.Max(1L, claimEntity.Witnesses),
                Weight: Math.Max(1.0, claimEntity.Witnesses * 0.75)));
        }

        var orderedReal = realWitnesses
            .OrderByDescending(static w => w.FisherInfo * Math.Max(0.1, w.Trust))
            .ThenByDescending(static w => w.Games)
            .ThenBy(static w => w.WitnessId, StringComparer.Ordinal)
            .ToArray();

        var trueWitness = orderedReal[0];
        var candidatePool = new List<ArenaWitnessCandidate>
        {
            new(trueWitness.WitnessId, trueWitness.Label, "witness"),
        };
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { trueWitness.WitnessId };
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { trueWitness.Label };

        if (catalog?.Sources is { Count: > 0 } catSources)
        {
            foreach (var src in catSources.OrderBy(static s => s.Key, StringComparer.Ordinal))
            {
                if (candidatePool.Count >= 4) break;
                string sid = !string.IsNullOrWhiteSpace(src.IdHex) && Hash128.TryParseHex(src.IdHex, out _)
                    ? src.IdHex.ToLowerInvariant()
                    : DeterministicLabelIdHex(src.Key);
                if (usedIds.Contains(sid) || usedLabels.Contains(src.Key)) continue;
                usedIds.Add(sid);
                usedLabels.Add(src.Key);
                candidatePool.Add(new ArenaWitnessCandidate(sid, src.Key, "source"));
            }
        }

        foreach (var peer in orderedReal.Skip(1))
        {
            if (candidatePool.Count >= 4) break;
            if (usedIds.Contains(peer.WitnessId) || usedLabels.Contains(peer.Label)) continue;
            usedIds.Add(peer.WitnessId);
            usedLabels.Add(peer.Label);
            candidatePool.Add(new ArenaWitnessCandidate(peer.WitnessId, peer.Label, "neighbor_witness"));
        }

        foreach (var other in pool)
        {
            if (candidatePool.Count >= 4) break;
            if (string.Equals(other.IdHex, claimEntity.IdHex, StringComparison.OrdinalIgnoreCase)) continue;
            if (usedIds.Contains(other.IdHex) || usedLabels.Contains(other.Label)) continue;
            usedIds.Add(other.IdHex);
            usedLabels.Add(other.Label);
            candidatePool.Add(new ArenaWitnessCandidate(other.IdHex, other.Label, "distractor"));
        }

        // Deterministic seeded shuffle of candidates so the winner position is unpredictable.
        var shuffled = candidatePool
            .OrderBy(c => SeedHashU64($"detective_order:{seed}:{c.WitnessId}"))
            .ThenBy(c => c.WitnessId, StringComparer.Ordinal)
            .ToArray();

        string claimSummary = BuildClaimSummary(claimEntity, entity, mesh);
        return new DetectiveSelection(claimEntity, claimSummary, record, trueWitness, shuffled, orderedReal);
    }

    private static string BuildClaimSummary(
        CandidateEntity claimEntity, ExploreEntityResponse? entity, MeshResponse? mesh)
    {
        if (entity?.SalientFacts is { Count: > 0 } facts)
        {
            var top = facts[0];
            return $"{claimEntity.Label} —[{top.Type}]→ {top.Fact} (μ {top.EffMu:F2}, {top.Witnesses}g)";
        }
        if (entity?.ConsensusOut is { Count: > 0 } outLinks)
        {
            var top = outLinks[0];
            return $"{claimEntity.Label} —[{top.Type}]→ {top.EntityLabel} (μ {top.EffMu:F2}, {top.Witnesses}g)";
        }
        if (mesh?.BelongsTo is { Count: > 0 } up)
        {
            var top = up[0];
            return $"{claimEntity.Label} —[{top.Relation}]→ {top.Label}";
        }
        return $"Attested substrate entity {claimEntity.Label} ({claimEntity.IdHex})";
    }

    private async Task<ResolvedSide> ResolveSideAsync(string input, CancellationToken ct)
    {
        if (Hash128.TryParseHex(input, out var hexId))
        {
            string hex = hexId.ToHex();
            var entity = await TrySubstrateAsync(() => _substrate.ExploreEntityAsync(hex, 8, 8, ct), ct).ConfigureAwait(false);
            double[] coord = entity?.Physicalities is { Count: > 0 } p
                ? [p[0].X, p[0].Y, p[0].Z, p[0].M]
                : [0.0, 0.0, 0.0, 0.0];
            string label = BestLabel(entity?.Label, hex);
            var nodes = new List<ArenaDecomposeNodeView>
            {
                new(0, hex, label, (byte)(entity?.Tier ?? 2))
            };
            return new ResolvedSide(input, hex, hexId, label, entity?.Tier, coord, nodes);
        }

        try
        {
            var decomp = _decompose.Decompose(input);
            var anchor = _decompose.ComputeAnchor(input);
            var idHash = Hash128.ParseHex(decomp.RootIdHex);
            var nodes = decomp.Nodes
                .Select(static n => new ArenaDecomposeNodeView(n.Ordinal, n.IdHex, n.Label, n.Tier))
                .ToArray();
            short? maxTier = nodes.Length > 0 ? nodes.Max(static n => (short)n.Tier) : null;
            return new ResolvedSide(
                input,
                decomp.RootIdHex.ToLowerInvariant(),
                idHash,
                input,
                maxTier,
                [anchor.Cx, anchor.Cy, anchor.Cz, anchor.Cm],
                nodes);
        }
        catch (InvalidOperationException)
        {
            var resolved = await TrySubstrateAsync(() => _substrate.ResolveTopicAsync(input, ct), ct).ConfigureAwait(false);
            if (resolved is { } topic)
            {
                var id = Hash128.FromBytes(topic.Id);
                return new ResolvedSide(input, id.ToHex(), id, topic.Label, 2, [0.0, 0.0, 0.0, 0.0],
                    [new ArenaDecomposeNodeView(0, id.ToHex(), topic.Label, 2)]);
            }
            var fallback = Hash128.Blake3(Encoding.UTF8.GetBytes(input));
            return new ResolvedSide(input, fallback.ToHex(), fallback, input, 2, [0.0, 0.0, 0.0, 0.0],
                [new ArenaDecomposeNodeView(0, fallback.ToHex(), input, 2)]);
        }
    }

    private async Task<IReadOnlyList<CandidateEntity>> LoadCandidatePoolAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, CandidateEntity>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? idHex, string? label, double standing, long witnesses)
        {
            if (string.IsNullOrWhiteSpace(idHex) || !Hash128.TryParseHex(idHex, out var h))
                return;
            string hex = h.ToHex();
            string cleanLabel = !string.IsNullOrWhiteSpace(label) ? label.Trim() : hex;
            if (map.TryGetValue(hex, out var prev))
            {
                string chosenLabel = LooksLikeHex(prev.Label) && !LooksLikeHex(cleanLabel) ? cleanLabel : prev.Label;
                map[hex] = new CandidateEntity(hex, chosenLabel, Math.Max(prev.Standing, standing), Math.Max(prev.Witnesses, witnesses));
            }
            else
            {
                map[hex] = new CandidateEntity(hex, cleanLabel, standing, witnesses);
            }
        }

        var leadersTask = TrySubstrateAsync(() => _substrate.LeadersAsync([1, 2, 3, 4], 40, ct), ct);
        var catalogTask = TrySubstrateAsync(() => _substrate.ExploreCatalogAsync(ct), ct);
        await Task.WhenAll(leadersTask, catalogTask).ConfigureAwait(false);

        if (leadersTask.Result is { } bands)
        {
            foreach (var band in bands)
            {
                foreach (var row in band.Rows)
                {
                    AddCandidate(row.SubjectId, row.Subject, (double)row.EffMu, row.Witnesses);
                    AddCandidate(row.ObjectId, row.Object, (double)row.EffMu, row.Witnesses);
                }
                if (band.Claims is { Count: > 0 } claims)
                {
                    foreach (var claim in claims)
                    {
                        foreach (var part in claim.Parts)
                        {
                            if (!string.IsNullOrWhiteSpace(part.Label))
                                AddCandidate(part.Id, part.Label, claim.Confidence, claim.Witnesses);
                        }
                    }
                }
            }
        }

        if (catalogTask.Result is { } catalog)
        {
            foreach (var edge in catalog.TopRelations)
            {
                AddCandidate(edge.SubjectIdHex, edge.Subject, (double)edge.EffectiveMu, edge.Witnesses);
                AddCandidate(edge.ObjectIdHex, edge.Object, (double)edge.EffectiveMu, edge.Witnesses);
            }
        }

        // Thin DB fallback: decompose & probe curated/featured terms live through ExploreResolve / Mesh / EntityRecord.
        if (map.Count < 4)
        {
            var probeTerms = (catalogTask.Result?.FeaturedRefs ?? Array.Empty<string>())
                .Concat(FallbackProbeTerms)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var term in probeTerms)
            {
                if (map.Count >= 12) break;
                var resolved = await TrySubstrateAsync(() => _substrate.ExploreResolveAsync(term, ct), ct).ConfigureAwait(false);
                if (resolved is not null)
                {
                    AddCandidate(resolved.IdHex, resolved.Label, resolved.Exists ? 1500.0 : 0.0, resolved.Exists ? 1 : 0);
                    var mesh = await TrySubstrateAsync(() => _substrate.MeshAsync(resolved.IdHex, 16, 16, ct), ct).ConfigureAwait(false);
                    foreach (var link in CombineMeshLinks(mesh))
                        AddCandidate(link.Id, link.Label, (double)(link.EffMu ?? 0m), link.Witnesses);
                    continue;
                }

                var side = await ResolveSideAsync(term, ct).ConfigureAwait(false);
                var rec = await TrySubstrateAsync(() => _substrate.EntityRecordAsync(side.IdHex, ct), ct).ConfigureAwait(false);
                var sideMesh = await TrySubstrateAsync(() => _substrate.MeshAsync(side.IdHex, 16, 16, ct), ct).ConfigureAwait(false);
                long recTotal = rec is null ? 0 : rec.Confirmed + rec.Contested + rec.Refuted + rec.Thin;
                if (recTotal > 0 || (sideMesh is not null && (sideMesh.BelongsTo.Count > 0 || sideMesh.Roster.Count > 0)))
                {
                    AddCandidate(side.IdHex, side.Label, 1500.0, Math.Max(1, recTotal));
                    foreach (var link in CombineMeshLinks(sideMesh))
                        AddCandidate(link.Id, link.Label, (double)(link.EffMu ?? 0m), link.Witnesses);
                }
            }
        }

        // Prefer readable entities when enough exist; keep deterministic ordering by IdHex.
        var readable = map.Values
            .Where(static c => !LooksLikeHex(c.Label) && c.Label.Any(char.IsLetterOrDigit))
            .OrderBy(static c => c.IdHex, StringComparer.Ordinal)
            .ToArray();

        return readable.Length >= 2
            ? readable
            : map.Values.OrderBy(static c => c.IdHex, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ArenaBridgeHopView> ExtractBridges(
        string idA, string labelA,
        string idB, string labelB,
        MeshResponse? meshA,
        MeshResponse? meshB,
        ResponseFieldResponse? field,
        MatchupResponse? matchup)
    {
        var linksA = new Dictionary<string, MeshLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in CombineMeshLinks(meshA))
        {
            if (string.Equals(l.Id, idA, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.Id, idB, StringComparison.OrdinalIgnoreCase))
                continue;
            linksA.TryAdd(l.Id, l);
        }

        var linksB = new Dictionary<string, MeshLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in CombineMeshLinks(meshB))
        {
            if (string.Equals(l.Id, idA, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.Id, idB, StringComparison.OrdinalIgnoreCase))
                continue;
            linksB.TryAdd(l.Id, l);
        }

        var bridges = new Dictionary<string, ArenaBridgeHopView>(StringComparer.OrdinalIgnoreCase);

        // 1. Direct mesh intersection A -> C <- B
        foreach (var (cid, la) in linksA)
        {
            if (!linksB.TryGetValue(cid, out var lb)) continue;
            double mu = ((double)(la.EffMu ?? 0m) + (double)(lb.EffMu ?? 0m)) * 0.5;
            long wits = Math.Max(1L, la.Witnesses + lb.Witnesses);
            bridges[cid] = new ArenaBridgeHopView(
                BridgeId: cid.ToLowerInvariant(),
                BridgeLabel: BestLabel(la.Label, lb.Label, cid),
                RelationA: string.IsNullOrWhiteSpace(la.Relation) ? "mesh" : la.Relation,
                RelationB: string.IsNullOrWhiteSpace(lb.Relation) ? "mesh" : lb.Relation,
                ClaimIdA: null,
                ClaimIdB: null,
                EffMu: mu,
                Witnesses: wits,
                Source: "mesh_intersection");
        }

        // 2. Response-field shared targets and connections
        if (field is not null)
        {
            var sideARows = new Dictionary<string, ResponseFieldEntry>(StringComparer.OrdinalIgnoreCase);
            var sideBRows = new Dictionary<string, ResponseFieldEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in field.Rows)
            {
                if (string.IsNullOrWhiteSpace(row.EntityId)) continue;
                if (string.Equals(row.EntityId, idA, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(row.EntityId, idB, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool fromA = string.Equals(row.AnchorId, idA, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.Anchor, labelA, StringComparison.OrdinalIgnoreCase);
                bool fromB = string.Equals(row.AnchorId, idB, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.Anchor, labelB, StringComparison.OrdinalIgnoreCase);

                if (fromA) sideARows.TryAdd(row.EntityId, row);
                if (fromB) sideBRows.TryAdd(row.EntityId, row);
            }

            foreach (var (cid, ra) in sideARows)
            {
                if (!sideBRows.TryGetValue(cid, out var rb)) continue;
                double muA = (ra.Rating ?? 0.0) - 2.0 * (ra.Deviation ?? 0.0);
                double muB = (rb.Rating ?? 0.0) - 2.0 * (rb.Deviation ?? 0.0);
                double mu = (muA + muB) * 0.5;
                long wits = Math.Max(1L, (ra.Witnesses ?? 0L) + (rb.Witnesses ?? 0L));

                if (!bridges.TryGetValue(cid, out var existing) || mu > existing.EffMu)
                {
                    bridges[cid] = new ArenaBridgeHopView(
                        BridgeId: cid.ToLowerInvariant(),
                        BridgeLabel: BestLabel(ra.Label, rb.Label, cid),
                        RelationA: ra.Relation ?? ra.Route,
                        RelationB: rb.Relation ?? rb.Route,
                        ClaimIdA: ra.ClaimId,
                        ClaimIdB: rb.ClaimId,
                        EffMu: mu,
                        Witnesses: wits,
                        Source: "response_field");
                }
            }

            if (field.Connections is { Count: > 0 } conns)
            {
                foreach (var conn in conns)
                {
                    string? candidateId = conn.ToId ?? conn.FromId;
                    string candidateLabel = conn.ToId is not null ? conn.To : conn.From;
                    if (string.IsNullOrWhiteSpace(candidateId) || !Hash128.TryParseHex(candidateId, out _))
                        continue;
                    if (string.Equals(candidateId, idA, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidateId, idB, StringComparison.OrdinalIgnoreCase))
                        continue;
                    bridges.TryAdd(candidateId, new ArenaBridgeHopView(
                        BridgeId: candidateId.ToLowerInvariant(),
                        BridgeLabel: BestLabel(candidateLabel, candidateId),
                        RelationA: conn.Relation,
                        RelationB: conn.Relation,
                        ClaimIdA: conn.ClaimId,
                        ClaimIdB: conn.ClaimId,
                        EffMu: 1500.0,
                        Witnesses: Math.Max(1L, conn.Witnesses ?? 1L),
                        Source: "lexicon_connection"));
                }
            }
        }

        // 3. Matchup shared readings when both sides share a definition synset
        if (matchup?.X.Readings is { Count: > 0 } rx && matchup.Y.Readings is { Count: > 0 } ry)
        {
            var yDefs = ry.ToDictionary(static r => r.DefinitionId, StringComparer.OrdinalIgnoreCase);
            foreach (var rA in rx)
            {
                if (!yDefs.TryGetValue(rA.DefinitionId, out var rB)) continue;
                if (!Hash128.TryParseHex(rA.DefinitionId, out _)) continue;
                double mu = ((double)rA.EffMu + (double)rB.EffMu) * 0.5;
                bridges.TryAdd(rA.DefinitionId, new ArenaBridgeHopView(
                    BridgeId: rA.DefinitionId.ToLowerInvariant(),
                    BridgeLabel: BestLabel(rA.Definition, rB.Definition, rA.DefinitionId),
                    RelationA: "sense",
                    RelationB: "sense",
                    ClaimIdA: rA.Paths.FirstOrDefault()?.FirstOrDefault()?.ClaimId,
                    ClaimIdB: rB.Paths.FirstOrDefault()?.FirstOrDefault()?.ClaimId,
                    EffMu: mu,
                    Witnesses: Math.Max(1L, rA.Witnesses + rB.Witnesses),
                    Source: "matchup_reading"));
            }
        }

        return bridges.Values
            .OrderByDescending(static b => b.EffMu)
            .ThenByDescending(static b => b.Witnesses)
            .ThenBy(static b => b.BridgeId, StringComparer.Ordinal)
            .Take(24)
            .ToArray();
    }

    private static IReadOnlyList<ArenaWitnessShareView> BuildCombineShares(
        IReadOnlyList<EngineWitnessShare>? turnShares,
        ExploreEntityResponse? entityA,
        ExploreEntityResponse? entityB,
        IReadOnlyList<ArenaBridgeHopView> bridges,
        MatchupResponse? matchup)
    {
        if (turnShares is { Count: > 0 })
        {
            var abiNormalized = FromAbiShares(turnShares);
            if (abiNormalized.Count > 0)
                return abiNormalized;
        }

        var raw = new List<RawWitnessSource>();
        raw.AddRange(ExtractEntityWitnessSources(entityA, Array.Empty<MeshLink>()));
        raw.AddRange(ExtractEntityWitnessSources(entityB, Array.Empty<MeshLink>()));

        foreach (var b in bridges)
        {
            double fisher = b.EffMu > 0 ? b.EffMu : 1.0;
            double weight = Math.Max(1.0, fisher * Math.Log2(b.Witnesses + 2.0));
            raw.Add(new RawWitnessSource(
                WitnessId: b.ClaimIdA ?? b.BridgeId,
                Label: b.BridgeLabel,
                Trust: 0.65,
                FisherInfo: fisher,
                Games: (ulong)Math.Max(1L, b.Witnesses),
                Weight: weight));
        }

        if (raw.Count == 0 && matchup is not null)
        {
            foreach (var f in matchup.X.TopFacts.Concat(matchup.Y.TopFacts))
            {
                double fisher = (double)Math.Max(1m, f.EffMu);
                raw.Add(new RawWitnessSource(
                    WitnessId: DeterministicLabelIdHex($"{f.Type}:{f.Fact}"),
                    Label: $"{f.Type}: {f.Fact}",
                    Trust: 0.6,
                    FisherInfo: fisher,
                    Games: (ulong)Math.Max(1L, f.Witnesses),
                    Weight: Math.Max(1.0, fisher)));
            }
        }

        return NormalizeShares(raw);
    }

    private static IReadOnlyList<ArenaWitnessShareView> BuildEntityShares(
        ExploreEntityResponse? entity,
        IReadOnlyList<MeshLink> links)
    {
        var raw = ExtractEntityWitnessSources(entity, links);
        return NormalizeShares(raw);
    }

    private static List<RawWitnessSource> ExtractEntityWitnessSources(
        ExploreEntityResponse? entity,
        IReadOnlyList<MeshLink> links)
    {
        var raw = new List<RawWitnessSource>();
        if (entity?.Evidence is { Count: > 0 } evidence)
        {
            foreach (var item in evidence)
            {
                string srcLabel = !string.IsNullOrWhiteSpace(item.SourceLabel)
                    ? item.SourceLabel.Trim()
                    : !string.IsNullOrWhiteSpace(item.ObjectLabel)
                        ? item.ObjectLabel.Trim()
                        : item.ObjectId;
                string srcId = !string.IsNullOrWhiteSpace(item.SourceId) && Hash128.TryParseHex(item.SourceId, out _)
                    ? item.SourceId.ToLowerInvariant()
                    : DeterministicLabelIdHex(srcLabel);

                double trust = item.Trust is { } t && double.IsFinite(t) ? Math.Clamp(t, 0.05, 1.0) : 0.6;
                double fisher = item.EffMu > 0 ? (double)item.EffMu : 100.0;
                ulong games = (ulong)Math.Max(1.0, item.Games ?? (double)(item.WitnessCount ?? 1L));
                raw.Add(new RawWitnessSource(
                    WitnessId: srcId,
                    Label: srcLabel,
                    Trust: trust,
                    FisherInfo: fisher,
                    Games: games,
                    Weight: Math.Max(1e-6, fisher * trust * Math.Log2(games + 2.0))));
            }
        }

        if (raw.Count == 0 && entity?.ConsensusOut is { Count: > 0 } outEdges)
        {
            foreach (var edge in outEdges)
            {
                double fisher = edge.EffMu > 0 ? (double)edge.EffMu : 100.0;
                ulong games = (ulong)Math.Max(1L, edge.Witnesses);
                raw.Add(new RawWitnessSource(
                    WitnessId: edge.EntityIdHex.ToLowerInvariant(),
                    Label: $"{edge.Type} → {edge.EntityLabel}",
                    Trust: 0.6,
                    FisherInfo: fisher,
                    Games: games,
                    Weight: Math.Max(1e-6, fisher * Math.Log2(games + 2.0))));
            }
        }

        if (raw.Count == 0 && links.Count > 0)
        {
            foreach (var link in links)
            {
                double fisher = (double)Math.Max(1m, link.EffMu ?? 100m);
                ulong games = (ulong)Math.Max(1L, link.Witnesses);
                raw.Add(new RawWitnessSource(
                    WitnessId: link.Id.ToLowerInvariant(),
                    Label: link.Label,
                    Trust: 0.6,
                    FisherInfo: fisher,
                    Games: games,
                    Weight: Math.Max(1e-6, fisher * Math.Log2(games + 2.0))));
            }
        }

        return raw;
    }

    private static IReadOnlyList<ArenaWitnessShareView> FromAbiShares(IReadOnlyList<EngineWitnessShare>? shares)
    {
        if (shares is null || shares.Count == 0)
            return Array.Empty<ArenaWitnessShareView>();

        var raw = new List<RawWitnessSource>(shares.Count);
        foreach (var s in shares)
        {
            if (s.Witness == Hash128.Zero) continue;
            string hex = s.Witness.ToHex();
            double trust = double.IsFinite(s.Trust) ? Math.Clamp(s.Trust, 0.0, 1.0) : 0.0;
            double fisher = double.IsFinite(s.FisherInfo) && s.FisherInfo > 0.0 ? s.FisherInfo : 0.0;
            double weight = fisher * trust;
            if (weight <= 0.0 && s.ShareBasisPoints > 0)
                weight = s.ShareBasisPoints;
            raw.Add(new RawWitnessSource(hex, hex, trust, fisher, s.Games, Math.Max(1e-6, weight)));
        }

        if (raw.Count == 0)
            return Array.Empty<ArenaWitnessShareView>();

        int suppliedSum = shares.Where(s => s.Witness != Hash128.Zero).Sum(s => Math.Max(0, s.ShareBasisPoints));
        int uniqueCount = raw.Select(static r => r.WitnessId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (suppliedSum == ShareBpTotal && uniqueCount == raw.Count && shares.All(s => s.ShareBasisPoints >= 0))
        {
            return shares
                .Where(s => s.Witness != Hash128.Zero)
                .Select(s => new ArenaWitnessShareView(
                    WitnessId: s.Witness.ToHex(),
                    Label: s.Witness.ToHex(),
                    Trust: double.IsFinite(s.Trust) ? s.Trust : 0.0,
                    FisherInfo: double.IsFinite(s.FisherInfo) ? s.FisherInfo : 0.0,
                    Games: s.Games,
                    ShareBp: s.ShareBasisPoints))
                .OrderByDescending(static s => s.ShareBp)
                .ThenBy(static s => s.WitnessId, StringComparer.Ordinal)
                .ToArray();
        }

        return NormalizeShares(raw);
    }

    /// <summary>
    /// Largest-remainder (Hamilton) basis-point normalization guaranteeing Σ share_bp == 10000
    /// whenever one or more contributing sources exist.
    /// </summary>
    private static IReadOnlyList<ArenaWitnessShareView> NormalizeShares(IReadOnlyList<RawWitnessSource> sources)
    {
        if (sources.Count == 0)
            return Array.Empty<ArenaWitnessShareView>();

        var grouped = sources
            .Where(static s => !string.IsNullOrWhiteSpace(s.WitnessId))
            .GroupBy(static s => s.WitnessId.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(static g => new RawWitnessSource(
                WitnessId: g.Key,
                Label: g.Select(static x => x.Label).FirstOrDefault(static l => !string.IsNullOrWhiteSpace(l)) ?? g.Key,
                Trust: g.Max(static x => x.Trust),
                FisherInfo: g.Max(static x => x.FisherInfo),
                Games: g.Max(static x => x.Games),
                Weight: g.Sum(static x => x.Weight > 0.0 && double.IsFinite(x.Weight) ? x.Weight : 0.0)))
            .OrderBy(static s => s.WitnessId, StringComparer.Ordinal)
            .ToArray();

        if (grouped.Length == 0)
            return Array.Empty<ArenaWitnessShareView>();

        double totalWeight = grouped.Sum(static s => s.Weight);
        bool fallbackEqual = totalWeight <= 0.0 || !double.IsFinite(totalWeight);
        if (fallbackEqual) totalWeight = grouped.Length;

        var raw = new double[grouped.Length];
        var bp = new int[grouped.Length];
        int floorSum = 0;
        for (int i = 0; i < grouped.Length; i++)
        {
            double w = fallbackEqual ? 1.0 : grouped[i].Weight;
            raw[i] = (w * ShareBpTotal) / totalWeight;
            bp[i] = (int)Math.Floor(raw[i]);
            floorSum += bp[i];
        }

        int rem = ShareBpTotal - floorSum;
        if (rem > 0)
        {
            var remOrder = Enumerable.Range(0, grouped.Length)
                .OrderByDescending(i => raw[i] - bp[i])
                .ThenBy(i => grouped[i].WitnessId, StringComparer.Ordinal)
                .ToArray();
            for (int k = 0; k < rem && k < remOrder.Length; k++)
                bp[remOrder[k]]++;
        }

        return grouped
            .Select((s, i) => new ArenaWitnessShareView(
                WitnessId: s.WitnessId,
                Label: s.Label,
                Trust: Math.Round(s.Trust, 4),
                FisherInfo: Math.Round(s.FisherInfo, 4),
                Games: s.Games,
                ShareBp: bp[i]))
            .OrderByDescending(static s => s.ShareBp)
            .ThenBy(static s => s.WitnessId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<double[]> ResolveTargetCoordAsync(
        CandidateEntity target, ExploreEntityResponse? entity, CancellationToken ct)
    {
        if (entity?.Physicalities is { Count: > 0 } p)
            return [p[0].X, p[0].Y, p[0].Z, p[0].M];

        if (!LooksLikeHex(target.Label))
        {
            try
            {
                var anchor = _decompose.ComputeAnchor(target.Label);
                return [anchor.Cx, anchor.Cy, anchor.Cz, anchor.Cm];
            }
            catch (InvalidOperationException)
            {
                // Fall back to resolved side below.
            }
        }

        var side = await ResolveSideAsync(target.IdHex, ct).ConfigureAwait(false);
        return side.Coord;
    }

    private static (ulong Mask, IReadOnlyList<string> Banks) InspectHighwayMask(
        string idHex, ExploreEntityResponse? entity, MeshResponse? mesh)
    {
        ulong mask = 0UL;
        try
        {
            if (!HighwayPerfcache.IsLoaded)
                HighwayPerfcache.LoadDefault();
            if (Hash128.TryParseHex(idHex, out var id))
                mask = HighwayPerfcache.MaskForId(id);
        }
        catch
        {
            mask = 0UL;
        }

        var banks = new List<string>();
        ulong kind = (mask >> 0) & 0xFFUL;
        ulong upos = (mask >> 8) & 0x1FUL;
        ulong lexfile = (mask >> 13) & 0x3FUL;
        ulong deprel = (mask >> 19) & 0x3FUL;
        ulong vnrole = (mask >> 25) & 0x3FUL;
        ulong wnframe = (mask >> 31) & 0x3FUL;

        if (kind != 0) banks.Add($"kind=0x{kind:x2}");
        if (upos != 0) banks.Add($"upos=#{upos}");
        if (lexfile != 0) banks.Add($"lexfile=#{lexfile}");
        if (deprel != 0) banks.Add($"deprel=#{deprel}");
        if (vnrole != 0) banks.Add($"vnrole=#{vnrole}");
        if (wnframe != 0) banks.Add($"wnframe=#{wnframe}");

        if (banks.Count == 0)
        {
            var hubType = entity?.Type ?? mesh?.HubType;
            if (!string.IsNullOrWhiteSpace(hubType))
                banks.Add($"hub:{hubType}");
            else
                banks.Add("mask:0x0000000000000000");
        }

        return (mask, banks);
    }

    private static CandidateEntity SelectGraphleTarget(string seed, IReadOnlyList<CandidateEntity> pool)
    {
        var readable = pool
            .Where(static c => !LooksLikeHex(c.Label) && c.Label.Length >= 2)
            .ToArray();
        return readable.Length > 0
            ? SelectDeterministic(readable, $"graphle:{seed}")
            : SelectDeterministic(pool, $"graphle:{seed}");
    }

    private static CandidateEntity SelectDeterministic(IReadOnlyList<CandidateEntity> pool, string key)
    {
        ulong idx = SeedHashU64(key) % (ulong)pool.Count;
        return pool[(int)idx];
    }

    private static ulong SeedHashU64(string key) =>
        Hash128.Blake3(Encoding.UTF8.GetBytes(key)).Hi;

    private static string ComputeChallengeToken(string mode, string seed) =>
        Hash128.Blake3(Encoding.UTF8.GetBytes($"arena-token:{mode}:{seed}")).ToHex();

    private static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "highway_race";
        string norm = mode.Trim().ToLowerInvariant().Replace('-', '_');
        return norm switch
        {
            "highway_race" or "highway" => "highway_race",
            "graphle" => "graphle",
            "provenance_detective" or "detective" => "provenance_detective",
            _ => throw new ArgumentException(
                $"Unknown Arena mode '{mode}'. Expected highway_race, graphle, or provenance_detective.",
                nameof(mode)),
        };
    }

    private static string NormalizeSeed(string? seed) =>
        string.IsNullOrWhiteSpace(seed)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : seed.Trim();

    private static string NormalizeFirmware(string? firmware, string paramName)
    {
        if (string.IsNullOrWhiteSpace(firmware))
            throw new ArgumentException($"Field '{paramName}' is required.", paramName);
        string norm = firmware.Trim().ToLowerInvariant();
        if (!ModelCatalog.FirmwareProfiles.Contains(norm, StringComparer.Ordinal))
            throw new ArgumentException(
                $"Unknown firmware '{firmware}'. Supported: {string.Join(", ", ModelCatalog.FirmwareProfiles)}.",
                paramName);
        return norm;
    }

    private static double ComputeConsensusRating(CandidateEntity target, ExploreEntityResponse? entity)
    {
        if (entity?.Senses is { Count: > 0 } senses)
            return (double)senses.Max(static s => s.EffMu);
        if (entity?.ConsensusOut is { Count: > 0 } outRows)
            return (double)outRows.Max(static r => r.EffMu);
        if (entity?.SalientFacts is { Count: > 0 } facts)
            return (double)facts.Max(static f => f.EffMu);
        return target.Standing;
    }

    private static MeshLink[] CombineMeshLinks(MeshResponse? mesh)
    {
        if (mesh is null) return Array.Empty<MeshLink>();
        var list = new List<MeshLink>(mesh.BelongsTo.Count + mesh.Roster.Count);
        list.AddRange(mesh.BelongsTo);
        list.AddRange(mesh.Roster);
        return list.ToArray();
    }

    private static string RedactText(string? value, string targetLabel, string targetIdHex)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[REDACTED]";
        string redacted = value.Trim();
        if (!string.IsNullOrWhiteSpace(targetLabel) && targetLabel.Length >= 2)
            redacted = redacted.Replace(targetLabel, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetIdHex))
            redacted = redacted.Replace(targetIdHex, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        return redacted;
    }

    private static double[] SelectCoord(double[] decomposedCoord, ExploreEntityResponse? entity)
    {
        if (entity?.Physicalities is { Count: > 0 } phys)
            return [phys[0].X, phys[0].Y, phys[0].Z, phys[0].M];
        return decomposedCoord;
    }

    private static double[] ComputeMidpoint4d(double[] a, double[] b)
    {
        try
        {
            return Math4d.Centroid(new[] { a[0], a[1], a[2], a[3], b[0], b[1], b[2], b[3] });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return
            [
                (a[0] + b[0]) * 0.5,
                (a[1] + b[1]) * 0.5,
                (a[2] + b[2]) * 0.5,
                (a[3] + b[3]) * 0.5,
            ];
        }
    }

    private static double Norm4d(double[] v) =>
        Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2] + v[3] * v[3]);

    private static double Distance4d(double[] a, double[] b)
    {
        double dx = a[0] - b[0];
        double dy = a[1] - b[1];
        double dz = a[2] - b[2];
        double dm = a[3] - b[3];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz + dm * dm);
    }

    private static string FormatQuadrant(double[] c)
    {
        static char Sign(double v) => v >= 0.0 ? '+' : '-';
        return $"({Sign(c[0])}x, {Sign(c[1])}y, {Sign(c[2])}z, {Sign(c[3])}m)";
    }

    private static Hash128 ComposeMerklePair(Hash128 a, Hash128 b)
    {
        try
        {
            return HashComposer.ComputeMerkleId(2, [a, b]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            var buf = new byte[32];
            a.WriteBytes(buf.AsSpan(0, 16));
            b.WriteBytes(buf.AsSpan(16, 16));
            return Hash128.Blake3(buf);
        }
    }

    private static string DeterministicLabelIdHex(string label) =>
        Hash128.Blake3(Encoding.UTF8.GetBytes(label)).ToHex();

    private static bool LooksLikeHex(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length == 32 && text.All(Uri.IsHexDigit);

    private static string BestLabel(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && !LooksLikeHex(c))
                return c.Trim();
        }
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        }
        return string.Empty;
    }

    private static async Task<T?> TrySubstrateAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SubstrateUnavailableException)
        {
            throw;
        }
        catch (SubstrateQueryException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    #endregion
}
