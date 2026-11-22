using System.Text;
using Laplace.Engine.Core;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Unified OODA code construction and repair player: binds target Tree-sitter modality +
/// <c>public.laplace_code_symbols</c> + in-process <see cref="ILaplaceEngineRuntime.Turn"/>
/// (<c>lp_engine_turn</c> with <c>coding.firmware</c>, <c>InternalMonologue</c> 0.10, and
/// <c>ToolchainVerification</c> 0.90) + external <see cref="CodeToolchain"/> fallback verification.
/// </summary>
internal sealed class CodePlayerService
{
    private const string CodePlayer = "CodePlayer";
    private const int DefaultAttempts = 4;
    private const int MaxAttempts = 8;
    private const int MaxCandidateBytes = 2 * 1024 * 1024;

    private readonly EngineRuntimeService _runtime;
    private readonly ISubstrateClient? _substrate;

    public CodePlayerService() : this(new EngineRuntimeService(), null) { }

    public CodePlayerService(ILaplaceEngineRuntime engine, ISubstrateClient? substrate = null)
        : this(new EngineRuntimeService(engine), substrate) { }

    public CodePlayerService(EngineRuntimeService runtime, ISubstrateClient? substrate = null)
    {
        _runtime = runtime;
        _substrate = substrate;
    }

    internal sealed record AttemptReceipt(
        int Attempt,
        string CandidateId,
        bool Verified,
        string Tool,
        bool ToolAvailable,
        bool TimedOut,
        int ExitCode,
        string Stdout,
        string Stderr);

    internal sealed record Result(
        string Modality,
        string Code,
        string? CandidateId,
        bool Verified,
        string? FailureKind,
        IReadOnlyList<AttemptReceipt> Attempts,
        EngineTurnResponse? AbiResult = null,
        string? EnvelopePreset = null);

    public static bool TryNormalizeModality(string value, out string modality)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        modality = value switch
        {
            "py" => "python",
            "c++" or "cxx" or "cc" => "cpp",
            "js" => "javascript",
            "ts" or "tsx" => "typescript",
            "cs" or "c#" or "csharp" => "c-sharp",
            "rs" => "rust",
            "golang" => "go",
            "sh" => "bash",
            "rb" => "ruby",
            "jl" => "julia",
            "kt" => "kotlin",
            "cu" => "cuda",
            "f90" => "fortran",
            "ll" => "llvm",
            _ => value,
        };
        return modality.Length > 0 && GrammarDecomposer.LookupById(modality) != IntPtr.Zero;
    }

    public async Task<Result> GenerateAsync(
        string prompt,
        string modality,
        int? maxAttempts,
        CancellationToken ct,
        EngineAuthorityScope? authority = null,
        lp_envelope? envelope = null,
        string? envelopePreset = null,
        string? codePath = null,
        string? codeText = null,
        string? verifyCmd = null,
        string? user = null,
        string? session = null)
    {
        if (!TryNormalizeModality(modality, out modality))
            return new Result(modality, "", null, false, "unsupported_code_language", []);

        var engine = _runtime.Runtime;
        if (engine is null)
            return new Result(modality, "", null, false, "engine_unavailable", []);

        int attempts = Math.Clamp(maxAttempts ?? DefaultAttempts, 1, MaxAttempts);
        string effUser = string.IsNullOrWhiteSpace(user) ? CodePlayer : user;
        string effSession = string.IsNullOrWhiteSpace(session) ? $"code-{Guid.NewGuid():N}" : session;

        // Consult admitted code symbols when available to enrich context without failing if DB offline.
        string enrichedPrompt = await EnrichWithCodeSymbolsAsync(prompt, codePath, ct).ConfigureAwait(false);

        // If caller provided code/path/verify_cmd directly, run native C OODA repair loop directly inside lp_engine_turn.
        if (!string.IsNullOrWhiteSpace(verifyCmd) || !string.IsNullOrWhiteSpace(codePath) || !string.IsNullOrWhiteSpace(codeText))
        {
            string? effectivePath = codePath;
            string? effectiveCodeText = codeText;
            string? effectiveVerify = verifyCmd;
            string? tempDir = null;
            bool writeFile = false;

            if (string.IsNullOrWhiteSpace(effectivePath) && !string.IsNullOrWhiteSpace(effectiveCodeText))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "laplace-ooda", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                effectivePath = Path.Combine(tempDir, CodeToolchain.CandidateFileName(effectiveCodeText, modality) ?? "candidate.c");
                await File.WriteAllTextAsync(effectivePath, effectiveCodeText, new UTF8Encoding(false), ct).ConfigureAwait(false);
                effectiveCodeText = null;
                writeFile = true;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(effectiveVerify)
                    && !string.IsNullOrWhiteSpace(effectivePath)
                    && CodeToolchain.TryBuildOodaVerifyCommand(effectivePath, modality, out var autoCmd))
                {
                    effectiveVerify = autoCmd;
                }

                var oodaReq = new EngineTurnRequest(
                    FirmwarePath: null,
                    ModelName: "coding",
                    Prompt: enrichedPrompt,
                    Messages: null,
                    SystemPrompt: null,
                    User: effUser,
                    Session: effSession,
                    ReadOnly: authority is { AllowWrite: false },
                    MaxTokens: (int)(envelope?.max_tokens_out ?? 0),
                    Temperature: 0.0,
                    StopSequences: null,
                    Seed: 0,
                    Authority: authority,
                    Envelope: envelope,
                    CodePath: effectivePath,
                    CodeText: effectiveCodeText,
                    CodeLang: modality,
                    VerifyCommand: effectiveVerify,
                    OodaRounds: attempts,
                    WriteFile: writeFile);

                var abi = await Task.Run(() => engine.Turn(oodaReq), ct).ConfigureAwait(false);
                string codeOut = !string.IsNullOrWhiteSpace(abi.PatchedCode) ? abi.PatchedCode : (abi.Text ?? string.Empty);
                string? respId = abi.ResponseId == Hash128.Zero ? null : abi.ResponseId.ToHex();
                bool verified = abi.OodaVerified && abi.StatusCode == 0;
                var receipt = new AttemptReceipt(
                    Attempt: Math.Max(1, abi.OodaRoundsRun),
                    CandidateId: respId ?? "",
                    Verified: verified,
                    Tool: string.IsNullOrWhiteSpace(effectiveVerify) ? "laplace-ooda" : effectiveVerify,
                    ToolAvailable: !abi.OodaUnverified,
                    TimedOut: false,
                    ExitCode: abi.StatusCode,
                    Stdout: verified ? (abi.Text ?? string.Empty) : string.Empty,
                    Stderr: verified ? string.Empty : (string.IsNullOrWhiteSpace(abi.Error) ? (abi.Text ?? string.Empty) : abi.Error));

                string? failKind = verified
                    ? null
                    : abi.OodaUnverified ? "toolchain_unavailable" : "code_verification_failed";

                return new Result(modality, codeOut, respId, verified, failKind, [receipt], abi, envelopePreset);
            }
            finally
            {
                if (tempDir is not null)
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        Laplace.Ops.LaplaceLogging.LogWarning(typeof(CodePlayerService), cleanupFailure, "OODA temp cleanup failed.");
                    }
                }
            }
        }

        // Standard iterative forward-pass + Tree-sitter + external toolchain verification loop.
        var receipts = new List<AttemptReceipt>(attempts);
        string lastCode = "";
        string? lastId = null;
        EngineTurnResponse? lastAbi = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var turnReq = new EngineTurnRequest(
                FirmwarePath: null,
                ModelName: "coding",
                Prompt: attempt == 1 ? enrichedPrompt : $"{prompt}\nFix verification failure:\n{receipts[^1].Stderr}",
                Messages: null,
                SystemPrompt: null,
                User: effUser,
                Session: effSession,
                ReadOnly: authority is { AllowWrite: false },
                MaxTokens: (int)(envelope?.max_tokens_out ?? 0),
                Temperature: 0.0,
                StopSequences: null,
                Seed: 0,
                Authority: authority,
                Envelope: envelope);

            var abi = await Task.Run(() => engine.Turn(turnReq), ct).ConfigureAwait(false);
            lastAbi = abi;
            string candidate = abi.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
                return new Result(modality, lastCode, lastId, false,
                    receipts.Count == 0 ? "code_forward_unresolved" : "code_generation_exhausted",
                    receipts, abi, envelopePreset);

            lastCode = candidate;
            lastId = abi.ResponseId == Hash128.Zero ? null : Convert.ToHexStringLower(abi.ResponseId.ToBytes());
            byte[] utf8 = Encoding.UTF8.GetBytes(candidate);
            if (utf8.Length > MaxCandidateBytes)
                return new Result(modality, candidate, lastId, false, "candidate_too_large", receipts, abi, envelopePreset);

            using var syntaxAst = GrammarDecomposer.Parse(utf8, modality);
            var syntax = GrammarSourceSupport.RequireNativeSourceAst(syntaxAst);
            var tool = await CodeToolchain.VerifyAsync(candidate, modality, syntax, ct).ConfigureAwait(false);

            receipts.Add(new AttemptReceipt(attempt, lastId ?? "", tool.Verified, tool.Tool,
                tool.ToolAvailable, tool.TimedOut, tool.ExitCode, tool.Stdout, tool.Stderr));

            if (!tool.ToolAvailable)
                return new Result(modality, candidate, lastId, false, "toolchain_unavailable", receipts, abi, envelopePreset);
            if (tool.Verified)
                return new Result(modality, candidate, lastId, true, null, receipts, abi, envelopePreset);
        }

        return new Result(modality, lastCode, lastId, false, "code_verification_failed", receipts, lastAbi, envelopePreset);
    }

    private async Task<string> EnrichWithCodeSymbolsAsync(string prompt, string? codePath, CancellationToken ct)
    {
        if (_substrate is null || string.IsNullOrWhiteSpace(codePath))
            return prompt;
        try
        {
            var symbols = await _substrate.SearchCodeSymbolsAsync(prompt, codePath, limit: 8, offset: 0, ct).ConfigureAwait(false);
            if (symbols.Count == 0) return prompt;
            var sb = new StringBuilder(prompt);
            sb.AppendLine();
            foreach (var sym in symbols.Take(8))
                sb.Append($"// symbol: {sym.Kind} {sym.Name} ({sym.FilePath})\n");
            return sb.ToString();
        }
        catch (Exception)
        {
            return prompt;
        }
    }
}
