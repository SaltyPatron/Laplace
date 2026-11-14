using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The code player: the one forward program answers the prompt as a turn of the player's session (Engine
/// <c>laplace turn</c>, through <see cref="ConversationTurns"/>), the candidate is checked by its modality's
/// toolchain, and both are recorded by the Engine as what runtime witnesses state at AppDerived (an application's
/// evidence from its own operation): the candidate as code, read by its grammar's recipe under the witness
/// CodePlayer, and the toolchain's verdict as its statement [candidate, compiles] scored 1 or 0 under the witness the
/// tool is. A rejected candidate stays in the session, so the next attempt reads it as discourse. Nothing here
/// composes an identity or writes a row.
/// </summary>
internal sealed class CodePlayerService
{
    private const string CodePlayer = "CodePlayer";
    private const string TrustClass = "AppDerived";
    private const string Verdict = "compiles";
    private const int DefaultAttempts = 4;
    private const int MaxAttempts = 8;
    private const int MaxCandidateBytes = 2 * 1024 * 1024;
    private readonly Lazy<ConversationTurns> _turns = new(ConversationTurns.Installed);
    private readonly Lazy<RuntimeWitnessRecorder> _witnesses = new(RuntimeWitnessRecorder.Installed);

    internal sealed record AttemptReceipt(int Attempt, string CandidateId, bool Verified,
        string Tool, bool ToolAvailable, bool TimedOut, int ExitCode, string Stdout, string Stderr);
    internal sealed record Result(string Modality, string Code, string? CandidateId, bool Verified,
        string? FailureKind, IReadOnlyList<AttemptReceipt> Attempts);

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

    public async Task<Result> GenerateAsync(string prompt, string modality, int? maxAttempts, CancellationToken ct)
    {
        if (!TryNormalizeModality(modality, out modality))
            return new Result(modality, "", null, false, "unsupported_code_language", []);

        int attempts = Math.Clamp(maxAttempts ?? DefaultAttempts, 1, MaxAttempts);
        var receipts = new List<AttemptReceipt>(attempts);
        string lastCode = "";
        string? lastId = null;
        string session = $"code-{Guid.NewGuid():N}";

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var turn = await _turns.Value.TurnAsync(CodePlayer, session, prompt, ct: ct).ConfigureAwait(false);
            string candidate = turn.Response;
            if (string.IsNullOrWhiteSpace(candidate))
                return new Result(modality, lastCode, lastId, false,
                    receipts.Count == 0 ? "code_forward_unresolved" : "code_generation_exhausted", receipts);

            lastCode = candidate;
            lastId = turn.ResponseId;
            byte[] utf8 = Encoding.UTF8.GetBytes(candidate);
            if (utf8.Length > MaxCandidateBytes)
                return new Result(modality, candidate, lastId, false, "candidate_too_large", receipts);

            // The Tree-sitter parse yields the syntax diagnostics the toolchain check reports.
            using var syntaxAst = GrammarDecomposer.Parse(utf8, modality);
            GrammarAstDiagnostics syntax = GrammarSourceFileSupport.RequireNativeSourceAst(syntaxAst);
            var tool = await CodeToolchain.VerifyAsync(candidate, modality, syntax, ct).ConfigureAwait(false);

            if (CodeToolchain.CandidateFileName(candidate, modality) is { } file)
                await _witnesses.Value.RecordAsync(new NativeRuntimeWitness(CodePlayer, TrustClass), file, utf8, ct)
                    .ConfigureAwait(false);
            if (tool.ToolAvailable)
                await _witnesses.Value.StateAsync(new NativeRuntimeWitness(tool.Tool, TrustClass),
                    [new WitnessStatement(candidate, Verdict, "", tool.Verified ? 1.0 : 0.0)], ct).ConfigureAwait(false);

            receipts.Add(new AttemptReceipt(attempt, lastId ?? "", tool.Verified, tool.Tool,
                tool.ToolAvailable, tool.TimedOut, tool.ExitCode, tool.Stdout, tool.Stderr));

            if (!tool.ToolAvailable)
                return new Result(modality, candidate, lastId, false, "toolchain_unavailable", receipts);
            if (tool.Verified)
                return new Result(modality, candidate, lastId, true, null, receipts);
        }

        return new Result(modality, lastCode, lastId, false, "toolchain_rejected_all_candidates", receipts);
    }
}
