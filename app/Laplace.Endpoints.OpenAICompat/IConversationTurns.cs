using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record NativeTurnStep(
    int Step,
    string Kind,
    string Id,
    string Text,
    double Grounds,
    int Continuity,
    double Confidence);

internal sealed record NativeTurnReceipt(
    string Participant,
    string Session,
    string PromptId,
    string? ResponseId,
    string? TurnId,
    int Ordinal,
    int TurnsBefore,
    string Response,
    string? Disposition,
    bool Witnessed,
    string? Reason,
    string? Detail,
    IReadOnlyList<NativeTurnStep> Steps,
    string Trace,
    EngineTurnResponse? AbiResult = null,
    string? EnvelopePreset = null,
    ulong EnvelopeEstimatedUs = 0);

/// <summary>
/// The conversation surfaces' one forward program: a turn of a session answered and witnessed
/// in-process by <c>laplace_engine_abi</c> (<see cref="ILaplaceEngineRuntime.Turn"/>).
/// </summary>
internal interface IConversationTurns
{
    /// <summary>Why no turn can run here (the native Engine ABI is unavailable), or null.</summary>
    string? Unavailable { get; }

    Task<NativeTurnReceipt> TurnAsync(string participant, string session, string prompt,
        Action<NativeTurnStep>? step, CancellationToken ct);

    Task<NativeTurnReceipt> TurnAsync(
        EngineTurnRequest request,
        string? envelopePreset,
        ulong envelopeEstimatedUs,
        Action<NativeTurnStep>? step,
        CancellationToken ct) =>
        TurnAsync(
            request.User ?? "anonymous",
            request.Session ?? "default",
            request.Prompt ?? request.Messages?.LastOrDefault(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty,
            step,
            ct);
}

/// <summary>
/// Runs turns directly in-process via <see cref="ILaplaceEngineRuntime"/> (<c>lp_engine_turn</c>).
/// Turns within one session are serialized cleanly by per-session gate; different sessions run concurrently.
/// </summary>
internal sealed class EngineConversationTurns : IConversationTurns
{
    private const int Gates = 64;
    private readonly SemaphoreSlim[] _gates =
        Enumerable.Range(0, Gates).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly EngineRuntimeService _runtime;

    public EngineConversationTurns(EngineRuntimeService runtime) => _runtime = runtime;

    public EngineConversationTurns(ILaplaceEngineRuntime runtime) : this(new EngineRuntimeService(runtime)) { }

    public string? Unavailable => _runtime.Unavailable;

    public Task<NativeTurnReceipt> TurnAsync(
        string participant, string session, string prompt,
        Action<NativeTurnStep>? step, CancellationToken ct) =>
        TurnAsync(
            new EngineTurnRequest(
                FirmwarePath: null,
                ModelName: "program",
                Prompt: prompt,
                Messages: null,
                SystemPrompt: null,
                User: participant,
                Session: session,
                ReadOnly: false,
                MaxTokens: 0,
                Temperature: 0.0,
                StopSequences: null,
                Seed: 0,
                Authority: null,
                Envelope: null),
            envelopePreset: "standard",
            envelopeEstimatedUs: 0,
            step,
            ct);

    public async Task<NativeTurnReceipt> TurnAsync(
        EngineTurnRequest request,
        string? envelopePreset,
        ulong envelopeEstimatedUs,
        Action<NativeTurnStep>? step,
        CancellationToken ct)
    {
        var engine = _runtime.Runtime;
        if (engine is null)
            throw new InvalidOperationException(_runtime.Unavailable ?? "Native Laplace engine runtime is unavailable.");

        string participant = string.IsNullOrWhiteSpace(request.User) ? "anonymous" : request.User;
        string session = string.IsNullOrWhiteSpace(request.Session) ? "default" : request.Session;
        var gate = Gate(participant, session);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            EngineTurnResponse result = await Task.Run(() => engine.Turn(request), ct).ConfigureAwait(false);

            if (result.StatusCode != 0 && string.IsNullOrEmpty(result.Text) && !string.IsNullOrWhiteSpace(result.Error))
                throw new InvalidOperationException(result.Error);

            string response = result.Text ?? string.Empty;
            string responseIdHex = result.ResponseId == Hash128.Zero
                ? string.Empty
                : result.ResponseId.ToHex();
            string? turnIdHex = result.TurnId == Hash128.Zero
                ? null
                : result.TurnId.ToHex();
            string promptIdHex = result.PromptId == Hash128.Zero
                ? new string('0', 32)
                : result.PromptId.ToHex();

            var steps = BuildSteps(response, responseIdHex, result.TopSalience, result.MeanConfidence);
            foreach (var s in steps)
                step?.Invoke(s);

            bool witnessed = turnIdHex is not null && !request.ReadOnly;
            string trace =
                $"FIRMWARE   {request.ModelName ?? "program"} ({(result.FirmwareId == Hash128.Zero ? "default" : result.FirmwareId.ToHex()[..12])})\n"
                + $"SALIENCE   top={result.TopSalience:F3} mean_conf={result.MeanConfidence:F3}\n"
                + $"ENVELOPE   {envelopePreset ?? "standard"} wall_us={result.Work.wall_us} probes={result.Work.db_probes} rows={result.Work.rows_scanned} tokens={result.Work.tokens_out}\n"
                + (result.OodaRoundsRun > 0
                    ? $"OODA       rounds={result.OodaRoundsRun} verified={result.OodaVerified} unverified={result.OodaUnverified} culprit={(result.CulpritId == Hash128.Zero ? "none" : result.CulpritId.ToHex()[..12])}\n"
                    : string.Empty)
                + $"DISPOSE    {result.Disposition}";

            return new NativeTurnReceipt(
                Participant: participant,
                Session: session,
                PromptId: promptIdHex,
                ResponseId: string.IsNullOrEmpty(responseIdHex) ? null : responseIdHex,
                TurnId: turnIdHex,
                Ordinal: 1,
                TurnsBefore: 0,
                Response: response,
                Disposition: result.Disposition,
                Witnessed: witnessed,
                Reason: witnessed ? null : (request.ReadOnly ? "read-only pass" : "nothing emitted: nothing to witness"),
                Detail: result.Error,
                Steps: steps,
                Trace: trace,
                AbiResult: result,
                EnvelopePreset: envelopePreset,
                EnvelopeEstimatedUs: envelopeEstimatedUs);
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<NativeTurnStep> BuildSteps(
        string response, string responseIdHex, double topSalience, double meanConfidence)
    {
        if (string.IsNullOrEmpty(response))
            return Array.Empty<NativeTurnStep>();

        var parts = response.Split(' ');
        var list = new List<NativeTurnStep>(parts.Length);
        string stepId = string.IsNullOrEmpty(responseIdHex) ? new string('0', 32) : responseIdHex;
        for (int i = 0; i < parts.Length; i++)
        {
            string piece = i == 0 ? parts[i] : " " + parts[i];
            list.Add(new NativeTurnStep(
                Step: i + 1,
                Kind: "answers",
                Id: stepId,
                Text: piece,
                Grounds: topSalience,
                Continuity: i,
                Confidence: meanConfidence));
        }
        return list;
    }

    private SemaphoreSlim Gate(string participant, string session) =>
        _gates[(int)((uint)HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(participant),
            StringComparer.Ordinal.GetHashCode(session)) % Gates)];
}
