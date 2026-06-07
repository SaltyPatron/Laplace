using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

public sealed class TokenS3Morph
{
    private const int S3            = 4;
    private const int EigTargetDim  = 64;
    private const int KNeighbors    = 15;

    private readonly float[] _embed;
    private readonly int _vocab;
    private readonly int _dModel;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _sourceId;
    private readonly Hash128 _tokenizerEntityId;
    private readonly ILogger _log;

    public TokenS3Morph(
        float[] embed, int vocab, int dModel,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, Hash128 tokenizerEntityId, ILogger log)
    {
        _embed = embed; _vocab = vocab; _dModel = dModel;
        _tokens = tokens; _sourceId = sourceId; _tokenizerEntityId = tokenizerEntityId;
        _log = log;
    }

    public IEnumerable<SubstrateChange> Emit()
    {
        var fitIdx     = new List<int>(_vocab);
        var anchors    = new List<double[]>(_vocab);
        var rows       = new List<LlamaTokenizerParser.TokenRecord>(_vocab);
        foreach (var rec in _tokens)
        {
            if (rec.TokenId < 0 || rec.TokenId >= _vocab) continue;
            rows.Add(rec);
        }
        int n = rows.Count;
        for (int i = 0; i < n; i++)
        {
            var rec = rows[i];
            if (!rec.HasContentCoord) continue;
            fitIdx.Add(i);
            anchors.Add(new[] { rec.ContentX, rec.ContentY, rec.ContentZ, rec.ContentM });
        }
        int nFit = fitIdx.Count;
        int targetDim = Math.Min(EigTargetDim, Math.Min(_dModel, n - 2));
        int k = Math.Min(KNeighbors, n - 1);
        if (nFit < S3 + 2 || targetDim < S3 || k < 1)
        {
            _log.LogWarning("S3-morph: only {N} anchorable tokens (need ≥{Min}); skipping morph", nFit, S3 + 2);
            return Array.Empty<SubstrateChange>();
        }

        var src = new double[(long)n * _dModel];
        for (int i = 0; i < n; i++)
        {
            long s = (long)rows[i].TokenId * _dModel;
            for (int j = 0; j < _dModel; j++) src[(long)i * _dModel + j] = _embed[s + j];
        }

        var Y = new double[(long)n * targetDim];
        int rc;
        unsafe
        {
            fixed (double* ps = src) fixed (double* py = Y)
                rc = DynInterop.LaplacianEigenmaps(
                    ps, (nuint)n, (nuint)_dModel, (nuint)k, (nuint)targetDim, py);
        }
        if (rc != 0)
        {
            _log.LogWarning("S3-morph: laplacian_eigenmaps rc={Rc} (n={N},d={D},k={K},target={T}); skipping morph",
                rc, n, _dModel, k, targetDim);
            return Array.Empty<SubstrateChange>();
        }

        var Yt = new double[(long)targetDim * n];
        for (int i = 0; i < n; i++)
            for (int d = 0; d < targetDim; d++) Yt[(long)d * n + i] = Y[(long)i * targetDim + d];
        int gsRc;
        unsafe { fixed (double* pyt = Yt) gsRc = DynInterop.GramSchmidtOrthonormalize(pyt, (nuint)targetDim, (nuint)n); }
        if (gsRc == 0)
            for (int i = 0; i < n; i++)
                for (int d = 0; d < targetDim; d++) Y[(long)i * targetDim + d] = Yt[(long)d * n + i];
        else
            _log.LogWarning("S3-morph: gram_schmidt_orthonormalize rc={Rc}; using raw eigenmap basis", gsRc);

        var Yfit = new double[(long)nFit * targetDim];
        for (int f = 0; f < nFit; f++)
            Array.Copy(Y, (long)fitIdx[f] * targetDim, Yfit, (long)f * targetDim, targetDim);
        var B = new double[(long)nFit * S3];
        for (int f = 0; f < nFit; f++)
        {
            double[] b = anchors[f];
            B[(long)f * S3 + 0] = b[0]; B[(long)f * S3 + 1] = b[1];
            B[(long)f * S3 + 2] = b[2]; B[(long)f * S3 + 3] = b[3];
        }
        IntPtr T;
        unsafe
        {
            fixed (double* py = Yfit) fixed (double* pb = B)
                T = DynInterop.ProcrustesFit(py, (nuint)nFit, (nuint)targetDim, pb);
        }
        if (T == IntPtr.Zero)
        {
            _log.LogWarning("S3-morph: procrustes_fit failed (n={N},source_dim={T}); skipping morph", nFit, targetDim);
            return Array.Empty<SubstrateChange>();
        }

        try
        {
            double globalResid = DynInterop.ProcrustesResidual(T);
            _log.LogInformation(
                "phase=S3-morph: eigenmaps(target_dim={Td},k={K}) over {N} vocab rows → gram-schmidt → "
                + "procrustes fit on {NFit} anchored (residual={R:F4})", targetDim, k, n, nFit, globalResid);

            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            var outv = new double[S3];
            var px = new double[n]; var pyv = new double[n];
            var pz = new double[n]; var pm = new double[n];
            var resids = new double?[n];
            var placed = new bool[n];
            double sumSq = 0; int nResid = 0;
            for (int i = 0; i < n; i++)
            {
                unsafe
                {
                    fixed (double* py = &Y[(long)i * targetDim]) fixed (double* po = outv)
                        DynInterop.ProcrustesApply(T, py, (nuint)targetDim, po);
                }
                double x = outv[0], y = outv[1], z = outv[2], m = outv[3];
                double norm = Math.Sqrt(x * x + y * y + z * z + m * m);
                if (norm < 1e-12 || double.IsNaN(norm)) continue;
                px[i] = x / norm; pyv[i] = y / norm; pz[i] = z / norm; pm[i] = m / norm;
                placed[i] = true;

                var rec = rows[i];
                if (rec.HasContentCoord)
                {
                    double r = Math.Sqrt((px[i] - rec.ContentX) * (px[i] - rec.ContentX)
                                       + (pyv[i] - rec.ContentY) * (pyv[i] - rec.ContentY)
                                       + (pz[i] - rec.ContentZ) * (pz[i] - rec.ContentZ)
                                       + (pm[i] - rec.ContentM) * (pm[i] - rec.ContentM));
                    resids[i] = r;
                    sumSq += r * r; nResid++;
                }
            }
            double arenaM = nResid > 0 ? Math.Sqrt(sumSq / nResid) : 0.0;

            var bb = new SubstrateChangeBuilder(_sourceId, "model/embed-s3-morph",
                entityCapacity: 0, physicalityCapacity: n, attestationCapacity: n);
            int emitted = 0, emittedAnchored = 0;
            for (int i = 0; i < n; i++)
            {
                if (!placed[i]) continue;
                var rec = rows[i];
                Hash128 entityId = rec.EntityId;
                Hilbert128 hb = Hilbert128.Encode(new[] { px[i], pyv[i], pz[i], pm[i] });
                Hash128 physId = PhysicalityId.Compute(
                    entityId, _sourceId, PhysicalityType.Projection,
                    px[i], pyv[i], pz[i], pm[i], ReadOnlySpan<double>.Empty);

                bb.AddPhysicality(new PhysicalityRow(
                    Id:                physId,
                    EntityId:          entityId,
                    SourceId:          _sourceId,
                    Type:              PhysicalityType.Projection,
                    CoordX:            px[i], CoordY: pyv[i], CoordZ: pz[i], CoordM: pm[i],
                    HilbertIndex:      hb,
                    TrajectoryXyzm:    null,
                    NConstituents:     0,
                    AlignmentResidual: resids[i],
                    SourceDim:         _dModel,
                    ObservedAtUnixUs:  nowUs));
                emitted++;

                if (resids[i] is double resid && arenaM > 0)
                {
                    bb.AddAttestation(RelationTypeRegistry.AttestWeighted(
                        _tokenizerEntityId, "TOKEN_MAPS_TO", entityId, _sourceId,
                        SourceTrust.AiModelProbe,
                        magnitude: arenaM - resid, arenaScale: arenaM));
                    emittedAnchored++;
                }
                else
                {
                    bb.AddAttestation(RelationTypeRegistry.Attest(
                        _tokenizerEntityId, "TOKEN_MAPS_TO", entityId, _sourceId,
                        SourceTrust.AiModelProbe));
                }
            }
            _log.LogInformation("phase=S3-morph: {Emitted} Projection placements + TOKEN_MAPS_TO matchups "
                + "({Anchored} residual-scored at M={M:F6} / {Vocab} vocab — specials + byte tokens categorical)",
                emitted, emittedAnchored, arenaM, _vocab);
            return new[] { bb.Build() };
        }
        finally
        {
            DynInterop.ProcrustesFree(T);
        }
    }
}
