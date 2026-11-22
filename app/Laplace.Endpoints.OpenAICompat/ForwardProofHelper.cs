using System.Text.Json;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

internal static class ForwardProofHelper
{
    public static ForwardPassProof Build(
        NativeTurnReceipt turn,
        string sessionKey)
    {
        var baseProof = FromAbi(
            turn.AbiResult,
            sessionKey,
            turn.Participant,
            turn.EnvelopePreset,
            turn.EnvelopeEstimatedUs,
            turn.Steps);
        if (baseProof is not null)
        {
            return baseProof with
            {
                PromptId = string.IsNullOrEmpty(baseProof.PromptId) ? turn.PromptId : baseProof.PromptId,
                ResponseId = baseProof.ResponseId ?? turn.ResponseId,
                TurnId = baseProof.TurnId ?? turn.TurnId,
                Ordinal = turn.Ordinal > 0 ? turn.Ordinal : baseProof.Ordinal,
                TurnsBefore = Math.Max(turn.TurnsBefore, baseProof.TurnsBefore),
                Witnessed = turn.Witnessed || baseProof.Witnessed,
                Reason = baseProof.Reason ?? turn.Reason,
                Disposition = string.IsNullOrEmpty(baseProof.Disposition) ? (turn.Disposition ?? "") : baseProof.Disposition,
                Trace = string.IsNullOrEmpty(turn.Trace) ? baseProof.Trace : turn.Trace,
            };
        }

        return new ForwardPassProof(
            Session: sessionKey,
            Participant: turn.Participant,
            PromptId: turn.PromptId,
            ResponseId: turn.ResponseId,
            TurnId: turn.TurnId,
            Ordinal: turn.Ordinal,
            TurnsBefore: turn.TurnsBefore,
            Witnessed: turn.Witnessed,
            Reason: turn.Reason,
            Disposition: turn.Disposition ?? "",
            Steps: turn.Steps.Select(ToContractStep).ToArray(),
            Trace: turn.Trace);
    }

    public static ForwardPassProof? FromAbi(
        EngineTurnResponse? abi,
        string sessionKey,
        string participant,
        string? preset = null,
        ulong estimatedUs = 0,
        IReadOnlyList<NativeTurnStep>? nativeSteps = null)
    {
        if (abi is null)
            return null;

        IReadOnlyList<ForwardTurnStep> steps;
        if (nativeSteps is { Count: > 0 })
        {
            steps = nativeSteps.Select(ToContractStep).ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(abi.Text))
        {
            steps = [new ForwardTurnStep(
                Step: 1,
                Kind: abi.OodaVerified ? "ooda_verified" : "forward",
                Id: abi.ResponseId == Hash128.Zero ? "" : abi.ResponseId.ToHex(),
                Text: abi.Text,
                Grounds: abi.TopSalience,
                Continuity: abi.OodaRoundsRun,
                Confidence: abi.MeanConfidence)];
        }
        else
        {
            steps = Array.Empty<ForwardTurnStep>();
        }

        return new ForwardPassProof(
            Session: sessionKey,
            Participant: participant,
            PromptId: abi.PromptId == Hash128.Zero ? "" : abi.PromptId.ToHex(),
            ResponseId: abi.ResponseId == Hash128.Zero ? null : abi.ResponseId.ToHex(),
            TurnId: abi.TurnId == Hash128.Zero ? null : abi.TurnId.ToHex(),
            Ordinal: 0,
            TurnsBefore: 0,
            Witnessed: abi.TurnId != Hash128.Zero,
            Reason: abi.Work.truncated != 0 ? $"truncated_{abi.Work.exhausted_reason}" : null,
            Disposition: !string.IsNullOrWhiteSpace(abi.Disposition)
                ? abi.Disposition
                : abi.OodaVerified ? "verified" : abi.OodaUnverified ? "unverified" : "accepted",
            Steps: steps,
            Trace: abi.Text ?? string.Empty,
            FirmwareId: abi.FirmwareId == Hash128.Zero ? null : abi.FirmwareId.ToHex(),
            ProgramId: abi.ProgramId == Hash128.Zero ? null : abi.ProgramId.ToHex(),
            DecisionId: abi.DecisionId == Hash128.Zero ? null : abi.DecisionId.ToHex(),
            TopSalience: abi.TopSalience,
            MeanConfidence: abi.MeanConfidence,
            Ooda: new OodaProofReceipt(
                Verified: abi.OodaVerified,
                Unverified: abi.OodaUnverified,
                RoundsRun: abi.OodaRoundsRun,
                CulpritId: abi.CulpritId == Hash128.Zero ? null : abi.CulpritId.ToHex(),
                CulpritContrast: abi.CulpritContrast),
            Work: WorkFromAbi(abi.Work, preset, estimatedUs),
            WitnessShares: abi.Shares.Select(ShareFromAbi).ToArray());
    }

    public static WorkReceipt WorkFromAbi(lp_work_counters w, string? preset, ulong estimatedUs) => new(
        WallUs: w.wall_us,
        DbProbes: w.db_probes,
        RowsScanned: w.rows_scanned,
        BytesAlloc: w.bytes_alloc,
        Hops: w.hops,
        TokensOut: w.tokens_out,
        Truncated: w.truncated,
        ExhaustedReason: w.exhausted_reason,
        EstimatedUs: estimatedUs,
        Preset: string.IsNullOrWhiteSpace(preset) ? "standard" : preset);

    public static WitnessShareReceipt ShareFromAbi(EngineWitnessShare s) => new(
        WitnessId: s.Witness.ToHex(),
        Trust: s.Trust,
        FisherInfo: s.FisherInfo,
        Games: s.Games,
        ShareBp: s.ShareBasisPoints);

    public static ForwardTurnStep ToContractStep(NativeTurnStep step) => new(
        step.Step,
        step.Kind,
        step.Id,
        step.Text,
        step.Grounds,
        step.Continuity,
        step.Confidence);

    public static (string? Prompt, string? SystemPrompt, IReadOnlyList<EngineChatMessage> History) ExtractChatInputs(
        string? explicitSystem,
        IReadOnlyList<ChatMessage> messages)
    {
        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitSystem))
            systemParts.Add(explicitSystem.Trim());

        var history = new List<EngineChatMessage>(messages.Count);
        string? lastUserPrompt = null;
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m.Content))
                continue;
            string role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role.Trim().ToLowerInvariant();
            string content = m.Content.Trim();
            if (role == "system" || role == "developer")
            {
                systemParts.Add(content);
            }
            else
            {
                history.Add(new EngineChatMessage(role, content));
                if (role == "user")
                    lastUserPrompt = content;
            }
        }

        string? systemPrompt = systemParts.Count > 0 ? string.Join("\n\n", systemParts) : null;
        return (lastUserPrompt, systemPrompt, history);
    }

    public static bool TryParseStopSequences(JsonElement? stop, out IReadOnlyList<string>? sequences, out string? error)
    {
        sequences = null;
        error = null;
        if (stop is not { } el || el.ValueKind == JsonValueKind.Null)
            return true;

        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s))
                    sequences = [s];
                return true;
            case JsonValueKind.Array:
                var list = new List<string>();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        error = "Field 'stop' array elements must be strings.";
                        return false;
                    }
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v))
                        list.Add(v);
                }
                if (list.Count > 4)
                {
                    error = "Field 'stop' accepts at most 4 stop sequences.";
                    return false;
                }
                sequences = list.Count > 0 ? list : null;
                return true;
            default:
                error = "Field 'stop' must be a string or an array of up to 4 strings.";
                return false;
        }
    }
}
