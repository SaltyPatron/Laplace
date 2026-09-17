using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class CodePlayerService(SubstrateClient substrate)
{
    private static readonly Hash128 Source = SubstrateCanonicalIds.Source("CodePlayer");
    private const double Trust = SourceTrust.AppDerived;
    private const int DefaultAttempts = 4;
    private const int MaxAttempts = 8;
    private const int MaxCandidateBytes = 2 * 1024 * 1024;

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

    public async Task<Result> GenerateAsync(string prompt, string modality, int steps, int maxStride,
        double spread, int topK, int? maxAttempts, CancellationToken ct)
    {
        if (!TryNormalizeModality(modality, out modality))
            return new Result(modality, "", null, false, "unsupported_code_language", []);

        int attempts = Math.Clamp(maxAttempts ?? DefaultAttempts, 1, MaxAttempts);
        var feedback = new List<byte[]>(attempts);
        var receipts = new List<AttemptReceipt>(attempts);
        string lastCode = "";
        Hash128? lastRoot = null;

        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(substrate.DataSource), substrate.DataSource);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var candidate = await NpgsqlSubstrateReads.ForwardCodeAsync(
                substrate.DataSource, prompt, steps, maxStride, spread, topK,
                frontierLimit: Math.Max(16, topK * 4), feedback, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(candidate))
                return new Result(modality, lastCode, lastRoot?.ToString(), false,
                    receipts.Count == 0 ? "code_evidence_unavailable" : "code_generation_exhausted", receipts);

            lastCode = candidate;
            byte[] utf8 = Encoding.UTF8.GetBytes(candidate);
            if (utf8.Length > MaxCandidateBytes)
                return new Result(modality, candidate, null, false, "candidate_too_large", receipts);

            var record = new GrammarComposeRecord(Utf8: utf8, Modality: modality, RequireSourceAst: true);
            var handler = new GrammarComposeHandler(Source, Trust, reader: null);
            using var unit = handler.CreateDeferredUnit(record);
            using var builder = new SubstrateChangeBuilder(
                Source, $"code-player/{modality}/{attempt}/{Guid.NewGuid():N}")
                .DeclareSourcePrior(Source, Trust);

            builder.AddEntity(Source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId, Source);
            Hash128 root = unit.DrainInto(builder, Trust, descentBitmap: null);
            if (root == default)
                return new Result(modality, candidate, null, false, "candidate_not_composed", receipts);
            handler.WalkWitness(record, root, builder, unit);
            lastRoot = root;

            builder.AddAttestation(NativeAttestation.Categorical(
                root, "IS_TYPED_AS", EntityTypeRegistry.CodeConcept,
                Source, Trust, contextId: null));

            var tool = await CodeToolchain.VerifyAsync(candidate, modality, ct).ConfigureAwait(false);
            Hash128 resultRoot = ContentEmitter.Emit(builder, tool.CanonicalJson, Source)
                ?? throw new InvalidOperationException("Toolchain receipt did not compose into substrate content.");
            Hash128 toolRoot = ContentEmitter.Emit(builder, $"toolchain/{tool.Tool}/{modality}", Source) ?? resultRoot;
            builder.AddAttestation(NativeAttestation.Categorical(
                root, "HAS_RESULT", resultRoot, Source, toolRoot, Trust, confirm: tool.Verified));

            await writer.ApplyAsync(builder.Build(), ct).ConfigureAwait(false);

            receipts.Add(new AttemptReceipt(attempt, root.ToString(), tool.Verified, tool.Tool,
                tool.ToolAvailable, tool.TimedOut, tool.ExitCode, tool.Stdout, tool.Stderr));

            if (!tool.ToolAvailable)
                return new Result(modality, candidate, root.ToString(), false, "toolchain_unavailable", receipts);
            if (tool.Verified)
                return new Result(modality, candidate, root.ToString(), true, null, receipts);

            feedback.Clear();
            feedback.Add(root.ToBytes());
        }

        return new Result(modality, lastCode, lastRoot?.ToString(), false,
            "toolchain_rejected_all_candidates", receipts);
    }
}
