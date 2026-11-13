using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Laplace.Decomposers.Abstractions;

/// <summary>One step of a turn: what it emitted and what the step added to the response (Engine receipt).</summary>
public sealed record NativeTurnStep(
    int Step, string Kind, string Id, string Text, double Grounds, int Continuity, double Confidence);

/// <summary>
/// What a turn did, as the Engine's receipt says it (<c>laplace turn|said --receipt</c>): the prompt and the
/// session it resolved, each step, the response it realized, and whether the turn was witnessed, with the
/// turn's ID and its ordinal in the session. <see cref="Trace"/> is the Engine's own trace of the turn.
/// </summary>
public sealed record NativeTurnReceipt(
    string Participant, string Session, string PromptId, string? ResponseId, string? TurnId,
    int Ordinal, int TurnsBefore, string Response, string? Disposition,
    bool Witnessed, int? Held, string? Reason, IReadOnlyList<NativeTurnStep> Steps, string Trace);

public sealed partial class NativeRecipeExecution
{
    /// <summary>The firmware the forward program runs under, installed beside the recipes.</summary>
    public string Firmware => Path.Combine(Directory.GetParent(Recipes)!.FullName, "firmware", "program.firmware");

    /// <summary>
    /// A turn of a session answered by the one forward program (Engine <c>laplace turn</c>): the prompt is
    /// admitted as content, the session's turns are its discourse, and unless <paramref name="read"/> the
    /// Engine witnesses the turn itself, the participant's [session, turn] at its ordinal and Laplace's
    /// [response, prompt]. <paramref name="step"/> sees each step as the Engine takes it. Nothing here
    /// composes an identity, reads a standing or writes a row.
    /// </summary>
    public Task<NativeTurnReceipt> TurnAsync(string connectionInfo, string participant, string session,
        string prompt, bool read = false, Action<NativeTurnStep>? step = null, CancellationToken ct = default)
        => RunTurnAsync(connectionInfo, participant, session, prompt, null, null, read, step, ct);

    /// <summary>
    /// A turn said elsewhere (Engine <c>laplace said</c>): <paramref name="responder"/> gave the response;
    /// nothing is generated, and the turn is recorded as <see cref="TurnAsync"/> records one. Said again in the
    /// same session it is the turn the session holds, and nothing is witnessed again.
    /// </summary>
    public Task<NativeTurnReceipt> SaidAsync(string connectionInfo, string participant, string session,
        string responder, string prompt, string response, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responder);
        ArgumentException.ThrowIfNullOrEmpty(response);
        return RunTurnAsync(connectionInfo, participant, session, prompt, responder, response, false, null, ct);
    }

    private async Task<NativeTurnReceipt> RunTurnAsync(string connectionInfo, string participant, string session,
        string prompt, string? responder, string? response, bool read, Action<NativeTurnStep>? step, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        // A name is recorded as content and given on one line; the texts go through files, never the command line.
        foreach (var name in new[] { participant, session, responder ?? "x" })
            if (name.Contains('\n') || name.Contains('\r') || name.Contains('\0'))
                throw new ArgumentException("A participant, a session and a responder are names on one line.");

        var utf8 = new UTF8Encoding(false, true);
        string promptFile = WorkFile("turn-prompt", ".txt"), receiptFile = WorkFile("turn-receipt");
        string? responseFile = response is null ? null : WorkFile("turn-response", ".txt");
        try
        {
            await File.WriteAllBytesAsync(promptFile, utf8.GetBytes(prompt), ct).ConfigureAwait(false);
            if (responseFile is not null) await File.WriteAllBytesAsync(responseFile, utf8.GetBytes(response!), ct).ConfigureAwait(false);

            var start = TurnStartInfo(connectionInfo, participant, session, responder, read,
                promptFile, responseFile, receiptFile);
            var receipt = new TurnReceiptReader(participant, session, step);
            using var trace = new MemoryStream();
            using var error = new MemoryStream();
            int code = await RunAsync(start, trace, error,
                process => TailAsync(receiptFile, receipt.Read, process, "receipt", ct), ct).ConfigureAwait(false);
            if (code != 0)
                throw new InvalidOperationException(
                    $"The Engine's {(responseFile is null ? "turn" : "said")} failed ({code}): {Tail(error)}");
            return receipt.Receipt(Encoding.UTF8.GetString(trace.ToArray()));
        }
        finally
        {
            File.Delete(promptFile);
            File.Delete(receiptFile);
            if (responseFile is not null) File.Delete(responseFile);
        }
    }

    /// <summary>
    /// The Engine command a turn runs: <c>laplace turn</c>, or <c>laplace said</c> when another responder
    /// gave the response, under the installed firmware, the texts in files and the receipt to a file.
    /// </summary>
    internal ProcessStartInfo TurnStartInfo(string connectionInfo, string participant, string session,
        string? responder, bool read, string promptFile, string? responseFile, string receiptFile)
    {
        if ((responder is null) != (responseFile is null) || (read && responseFile is not null))
            throw new ArgumentException("A turn said elsewhere has its responder and its response, and is witnessed.");
        var start = ProcessInfo(Executable, connectionInfo);
        start.Environment["LAPLACE_RECIPES"] = Recipes;
        start.Environment["LAPLACE_TIER0"] = Tier0;
        start.Environment["LAPLACE_HIGHWAY"] = Highway;
        start.Environment["LAPLACE_GRAMMARS"] = Grammars;
        start.ArgumentList.Add(responseFile is null ? "turn" : "said");
        start.ArgumentList.Add("--firmware"); start.ArgumentList.Add(Firmware);
        start.ArgumentList.Add("--as"); start.ArgumentList.Add(participant);
        start.ArgumentList.Add("--session"); start.ArgumentList.Add(session);
        if (responder is not null) { start.ArgumentList.Add("--by"); start.ArgumentList.Add(responder); }
        if (read) start.ArgumentList.Add("--read");
        start.ArgumentList.Add("--files");
        start.ArgumentList.Add("--receipt"); start.ArgumentList.Add(receiptFile);
        start.ArgumentList.Add(promptFile);
        if (responseFile is not null) start.ArgumentList.Add(responseFile);
        return start;
    }

    private static string Tail(MemoryStream error)
    {
        string said = Encoding.UTF8.GetString(error.ToArray()).Trim();
        return said.Length <= 2000 ? said : said[^2000..];
    }

    /// <summary>The receipt's lines in the order the Engine writes them; a line it does not declare is refused.</summary>
    internal sealed class TurnReceiptReader(string participant, string session, Action<NativeTurnStep>? onStep)
    {
        private readonly List<NativeTurnStep> _steps = [];
        private string? _prompt, _response, _turn, _text, _disposition, _reason;
        private int _ordinal, _before;
        private int? _held;
        private bool _witnessed, _resolved, _done;

        public void Read(ReadOnlySpan<byte> line)
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var e = document.RootElement;
            switch (e.GetProperty("event").GetString())
            {
                case "resolve":
                    _prompt = e.GetProperty("prompt").GetString();
                    if (e.TryGetProperty("response", out var response)) _response = response.GetString();
                    _ordinal = e.GetProperty("ordinal").GetInt32();
                    _before = e.GetProperty("turns_before").GetInt32();
                    _resolved = true;
                    break;
                case "step":
                    var s = new NativeTurnStep(e.GetProperty("step").GetInt32(), e.GetProperty("kind").GetString()!,
                        e.GetProperty("id").GetString()!, e.GetProperty("text").GetString()!,
                        e.GetProperty("grounds").GetDouble(), e.GetProperty("continuity").GetInt32(),
                        e.GetProperty("confidence").GetDouble());
                    _steps.Add(s);
                    onStep?.Invoke(s);
                    break;
                case "realize":
                    _text = e.GetProperty("text").GetString();
                    _disposition = e.GetProperty("disposition").GetString();
                    break;
                case "witness":
                    _witnessed = e.GetProperty("witnessed").GetBoolean();
                    if (e.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.String) _turn = turn.GetString();
                    if (e.TryGetProperty("response", out var witnessed) && witnessed.ValueKind == JsonValueKind.String) _response = witnessed.GetString();
                    if (e.TryGetProperty("ordinal", out var ordinal)) _ordinal = ordinal.GetInt32();
                    if (e.TryGetProperty("held", out var held) && held.ValueKind == JsonValueKind.Number) _held = held.GetInt32();
                    if (e.TryGetProperty("reason", out var reason)) _reason = reason.GetString();
                    _done = true;
                    break;
                default:
                    throw new InvalidDataException("The Engine's receipt names an event it does not declare.");
            }
        }

        public NativeTurnReceipt Receipt(string trace)
        {
            if (!_resolved || !_done || _prompt is null)
                throw new InvalidDataException("The Engine's turn ended without a whole receipt.");
            return new NativeTurnReceipt(participant, session, _prompt, _response, _turn, _ordinal, _before,
                _text ?? "", _disposition, _witnessed, _held, _reason, _steps, trace);
        }
    }
}
