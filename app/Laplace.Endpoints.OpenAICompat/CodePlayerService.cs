using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class CodePlayerService(SubstrateClient substrate)
{
    private static readonly Hash128 CodePlayerSource = SubstrateCanonicalIds.Source("CodePlayer");
    private static readonly Hash128 ToolchainSource = SubstrateCanonicalIds.Source("ToolchainWitness");
    private static readonly Hash128 CodePlayerTrustClass = SubstrateCanonicalIds.TrustClass("AppDerived");
    private static readonly Hash128 ToolchainTrustClass = SubstrateCanonicalIds.TrustClass("StandardsDerived");
    private const double CodePlayerTrust = SourceTrust.AppDerived;
    private const double ToolchainTrust = SourceTrust.StandardsDerived;
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

        // Proposal generation and deterministic verification are independent witnesses.
        // Bootstrap their source/trust identities once through the same substrate owner;
        // the compiler must not inherit the weaker AppDerived standing of the proposer.
        var codePlayerBootstrap = new BootstrapIntentBuilder(
            CodePlayerSource, "CodePlayer", CodePlayerTrustClass).Build();
        var toolchainBootstrap = new BootstrapIntentBuilder(
            ToolchainSource, "ToolchainWitness", ToolchainTrustClass).Build();
        await writer.ApplyManyAsync([codePlayerBootstrap, toolchainBootstrap], ct).ConfigureAwait(false);

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

            // STAGE: mint the candidate as exact content + native Tree-sitter grammar/Merkle
            // structure before a verifier is allowed to say anything about it.
            var record = new GrammarComposeRecord(Utf8: utf8, Modality: modality, RequireSourceAst: true);
            var handler = new GrammarComposeHandler(CodePlayerSource, CodePlayerTrust, reader: null);
            using var unit = handler.CreateDeferredUnit(record);
            using var candidateBuilder = new SubstrateChangeBuilder(
                CodePlayerSource, $"code-player/{modality}/{attempt}/{Guid.NewGuid():N}")
                .DeclareSourcePrior(CodePlayerSource, CodePlayerTrust);

            Hash128 root = unit.DrainInto(candidateBuilder, CodePlayerTrust, null);
            if (root == default)
                return new Result(modality, candidate, null, false, "candidate_not_composed", receipts);
            handler.WalkWitness(record, root, candidateBuilder, unit);
            lastRoot = root;
            candidateBuilder.AddAttestation(NativeAttestation.Categorical(
                root, "IS_TYPED_AS", EntityTypeRegistry.CodeConcept,
                CodePlayerSource, (Hash128?)null, CodePlayerTrust, true, 1));
            await writer.ApplyAsync(candidateBuilder.Build(), ct).ConfigureAwait(false);

            // VERIFY/WITNESS: deterministic tooling is its own high-trust source. The
            // diagnostic receipt is exact substrate content and polarity lives on the
            // HAS_RESULT attestation; failed code is adjudicated down, never deleted.
            var tool = await CodeToolchain.VerifyAsync(candidate, modality, ct).ConfigureAwait(false);
            using var witnessBuilder = new SubstrateChangeBuilder(
                ToolchainSource, $"toolchain-witness/{modality}/{attempt}/{Guid.NewGuid():N}")
                .DeclareSourcePrior(ToolchainSource, ToolchainTrust);
            Hash128 resultRoot = ContentEmitter.Emit(witnessBuilder, tool.CanonicalJson, ToolchainSource)
                ?? throw new InvalidOperationException("Toolchain receipt did not compose into substrate content.");
            Hash128 toolRoot = ContentEmitter.Emit(
                witnessBuilder, $"toolchain/{tool.Tool}/{modality}", ToolchainSource) ?? resultRoot;
            witnessBuilder.AddAttestation(NativeAttestation.Categorical(
                root, "HAS_RESULT", resultRoot, ToolchainSource, toolRoot,
                ToolchainTrust, confirm: tool.Verified));
            await writer.ApplyAsync(witnessBuilder.Build(), ct).ConfigureAwait(false);

            receipts.Add(new AttemptReceipt(attempt, root.ToString(), tool.Verified, tool.Tool,
                tool.ToolAvailable, tool.TimedOut, tool.ExitCode, tool.Stdout, tool.Stderr));

            if (!tool.ToolAvailable)
                return new Result(modality, candidate, root.ToString(), false, "toolchain_unavailable", receipts);
            if (tool.Verified)
                return new Result(modality, candidate, root.ToString(), true, null, receipts);

            // FOLD -> next COUPLE: the exact failed code root returns to the next forward
            // frontier, while its freshly folded HAS_RESULT standing is query-visible.
            feedback.Clear();
            feedback.Add(root.ToBytes());
        }

        return new Result(modality, lastCode, lastRoot?.ToString(), false,
            "toolchain_rejected_all_candidates", receipts);
    }
}
