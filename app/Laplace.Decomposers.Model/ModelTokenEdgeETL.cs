using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

// Model ingestion = pull token-to-token relations out of the weights, store nothing else.
//
// The embedding is a token-indexed address book (row i = token i's learned vector). It carries
// no glome position (only codepoints surface; "king" is already the inward centroid of its
// [k,i,n,g] codepoint trajectory, model-independent) and is never stored. We read it through a
// ROBUST spectral reduction (Laplacian eigenmaps, MKL) to the dominant low-rank structure, take
// each token's strongest partners, and stage (token, RELATED_TO, token) edges. The value becomes
// a Glicko game score via laplace_score; consensus_fold turns the accumulated games into the
// rating. C# only decodes + stages; MKL reduces; SPI scores + folds.
public sealed class ModelTokenEdgeETL
{
    private const int RowTile     = 256;
    private const int AttsPerChange = 200_000;
    private const int EigTargetDim = 64;
    private static readonly double ModelWeight =
        RelationTypeRegistry.Resolve("RELATED_TO").Rank * SourceTrust.AiModelProbe;

    private static readonly int PartnerCap =
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_MODEL_PARTNERS"), out var pc) && pc > 0
            ? pc : 32;

    private readonly string _modelDir;
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly ILogger _log;

    public ModelTokenEdgeETL(string modelDir, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, Hash128 sourceId, ILogger? log = null)
    {
        _modelDir = modelDir; _recipe = recipe; _tokens = tokens; _source = sourceId;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        int commitEpoch, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prof = ArchitectureProfile.For(_recipe.ModelType);
        int vocab = _recipe.VocabSize, d = _recipe.HiddenSize;
        var refs = SafetensorsContainerParser.ParseModel(_modelDir);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;
        if (!refMap.ContainsKey(prof.EmbedTokens)) yield break;

        // collapse the address book onto distinct content token entities (king==king across models)
        var ents = new List<Hash128>(vocab);
        var rowOfToken = new List<int>(vocab);
        var seen = new HashSet<Hash128>();
        foreach (var rec in _tokens)
        {
            if (rec.TokenId < 0 || rec.TokenId >= vocab) continue;
            if (!seen.Add(rec.EntityId)) continue;
            ents.Add(rec.EntityId);
            rowOfToken.Add(rec.TokenId);
        }
        int n = ents.Count;
        int kmax = Math.Min(EigTargetDim, Math.Min(d, n));
        if (n < 4 || kmax < 2)
        {
            _log.LogWarning("phase=edges: only {N} content tokens; skipping", n);
            yield break;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        float[] embed = WeightTensorETL.LoadTensorF32(refMap, prof.EmbedTokens, (long)vocab * d);

        // gather the address-book rows for our content tokens → A[n,d]
        var A = new float[(long)n * d];
        for (int i = 0; i < n; i++)
            Array.Copy(embed, (long)rowOfToken[i] * d, A, (long)i * d, d);

        // ROBUST reduction: truncated SVD (MKL) → dominant subspace. F = U·diag(S) preserves
        // E·Eᵀ (the token-similarity field), denoised to the retained rank (the winning ticket).
        // SVD preserves inner products; Laplacian eigenmaps would distort them — wrong tool here.
        int rank;
        var U  = new float[(long)n * kmax];
        var S  = new float[kmax];
        var Vt = new float[(long)kmax * d];
        unsafe
        {
            nuint r;
            int rc;
            fixed (float* ap = A) fixed (float* up = U) fixed (float* sp = S) fixed (float* vp = Vt)
                rc = SynInterop.TensorSvdTruncate(ap, (nuint)n, (nuint)d, 0.01, &r, up, sp, vp, (nuint)kmax);
            if (rc != 0) { _log.LogWarning("phase=edges: tensor_svd_truncate rc={Rc}; skipping", rc); yield break; }
            rank = (int)r;
        }
        if (rank < 2) { _log.LogWarning("phase=edges: SVD rank {R}<2; skipping", rank); yield break; }

        var Y = new double[(long)n * rank];
        for (int i = 0; i < n; i++)
            for (int t = 0; t < rank; t++)
                Y[(long)i * rank + t] = (double)U[(long)i * kmax + t] * S[t];
        NormRows(Y, n, rank);
        _log.LogInformation("phase=edges: SVD reduced {N:N0} tokens d={D}->rank {R} (tol 1%), {S:F0}s; staging RELATED_TO partners (cap {Cap})",
            n, d, rank, sw.Elapsed.TotalSeconds, PartnerCap);

        var typeId = RelationTypeRegistry.RelationTypeId("RELATED_TO");
        // In the reduced eigenmap space rows are normalized → edge value is a cosine in [-1,1].
        // The per-token top-k cap is the selector; the floor only admits positive associations
        // (a fixed dim-scaled noise floor like 5/√d is wrong here — it can exceed max cosine).
        double theta = double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_MODEL_EDGE_FLOOR"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var tf) ? tf : 0.0;
        long cap = (long)RowTile * n;
        var oR = new int[cap]; var oC = new int[cap]; var oV = new double[cap]; var oS = new long[cap];
        var objs = new List<int>(256); var scr = new List<long>(256);

        var b = NewChunk(commitEpoch); int inChunk = 0; long edges = 0;
        for (int rb = 0; rb < n; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int re = Math.Min(rb + RowTile, n);
            int cnt = RunTile(Y, rb, re, n, rank, theta, oR, oC, oV, oS, cap);
            int e = 0;
            while (e < cnt)
            {
                int row = oR[e]; objs.Clear(); scr.Clear();
                for (; e < cnt && oR[e] == row; e++)
                {
                    if (oC[e] == row) continue;           // no self
                    objs.Add(oC[e]); scr.Add(oS[e]);
                }
                if (objs.Count == 0) continue;
                TopK(objs, scr, PartnerCap);              // each token keeps its strongest partners
                for (int t = 0; t < objs.Count; t++)
                {
                    b.AddAttestation(NativeAttestation.Aggregated(
                        ents[row], typeId, ents[objs[t]], _source, null, 1, scr[t], ModelWeight));
                    edges++;
                    if (++inChunk >= AttsPerChange)
                    {
                        yield return b.Build(); b = NewChunk(commitEpoch); inChunk = 0;
                        await Task.Yield();
                    }
                }
            }
        }
        if (inChunk > 0) yield return b.Build();
        _log.LogInformation("phase=edges COMPLETE: {E:N0} token<->token RELATED_TO edges staged (consensus_fold does Glicko), {S:F0}s",
            edges, sw.Elapsed.TotalSeconds);
    }

    private SubstrateChangeBuilder NewChunk(int epoch) =>
        new SubstrateChangeBuilder(_source, "model/token-edges", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttsPerChange)
            .SetCommitEpoch(epoch);

    private static void TopK(List<int> objs, List<long> scr, int cap)
    {
        if (objs.Count <= cap) return;
        var ord = new int[objs.Count];
        for (int i = 0; i < ord.Length; i++) ord[i] = i;
        Array.Sort(ord, (a, b) => scr[b].CompareTo(scr[a]));
        var ko = new List<int>(cap); var ks = new List<long>(cap);
        for (int i = 0; i < cap; i++) { ko.Add(objs[ord[i]]); ks.Add(scr[ord[i]]); }
        objs.Clear(); objs.AddRange(ko); scr.Clear(); scr.AddRange(ks);
    }

    private static unsafe int RunTile(double[] y, int rb, int re, int n, int dim,
        double theta, int[] oR, int[] oC, double[] oV, long[] oS, long cap)
    {
        nuint count = 0; int overflow = 0;
        fixed (double* p = y) fixed (int* pR = oR) fixed (int* pC = oC)
        fixed (double* pV = oV) fixed (long* pS = oS)
            DynInterop.BilinearEdgesTile(p, (nuint)rb, (nuint)re, p, (nuint)n,
                (nuint)dim, theta, pR, pC, pV, pS, (nuint)cap, &count, &overflow);
        return (int)count;
    }

    private static void NormRows(double[] v, int n, int dim)
    {
        unsafe { fixed (double* p = v) { if (DynInterop.NormRowsD(p, (nuint)n, (nuint)dim) != 0)
            throw new InvalidOperationException("norm_rows_d failed"); } }
    }
}
