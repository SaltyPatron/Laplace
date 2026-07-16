using System.Buffers.Binary;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model.Tests;

// THE readback gate (campaign doc 26 item A): after `ingest model` in factors
// mode, trajectories pulled OUT of the database — layout v2, FactorTrajectory
// law — must be self-describing (arena + token identity in-band) and must
// reconstruct the checkpoint's math. Kernel-direct references recomputed here
// through the same natives from the checkpoint bytes. Chain under test:
//   probe input -> project/bias -> pack -> COPY -> PostGIS -> WKB -> unpack.
// Tier 1 gates only: these prove OUR copy, not model quality (taxonomy law).
public sealed class ModelGateFactorReadbackTests
{
    private const string Snap =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";
    private const string Conn =
        "Host=localhost;Username=postgres;Password=postgres;Database=laplace;Search Path=laplace,public";

    private const int Vocab = 30522, D = 384, H = 12, Hd = 32, I = 1536;
    private const double CsTol = 2e-6; // sqrt(dim)*eps_f32 with headroom

    static ModelGateFactorReadbackTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.LoadDefault();
    }

    private readonly ITestOutputHelper _out;
    public ModelGateFactorReadbackTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ModelGate_FactorReadback_MiniLM_AllPlanes()
    {
        if (!File.Exists(Path.Combine(Snap, "model.safetensors")))
        { _out.WriteLine("SKIPPED: MiniLM snapshot missing"); return; }

        var refs = SafetensorsContainerParser.ParseModel(Snap);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;

        // ETL-identical token selection: parser order, first row per entity id.
        var toks = LlamaTokenizerParser.Parse(Path.Combine(Snap, "tokenizer.json"));
        var seen = new HashSet<Hash128>();
        var rowOfToken = new List<int>(Vocab);
        var tokenIds = new List<Hash128>(Vocab);
        foreach (var rec in toks)
        {
            if (rec.TokenId < 0 || rec.TokenId >= Vocab) continue;
            if (!seen.Add(rec.EntityId)) continue;
            rowOfToken.Add(rec.TokenId);
            tokenIds.Add(rec.EntityId);
        }
        int n = rowOfToken.Count;

        using var conn = new NpgsqlConnection(Conn);
        conn.Open();

        // Kernel-direct probe input, exactly the deposit path's math.
        double[] X = BuildProbeInput(refMap, rowOfToken, n);

        // ---- Plane 1: q/k head factors (L0.H3), + header identity + arena.
        const string qName = "encoder.layer.0.attention.self.query.weight";
        const string kName = "encoder.layer.0.attention.self.key.weight";
        double[]? qTraj = FetchTrajectory(conn, ModelCheckpoint.HeadSliceIds(refMap[qName], H)[3]);
        if (qTraj is null)
        { _out.WriteLine("SKIPPED: no v2 factor deposit (run ingest model, planes=factors)"); return; }
        double[] kTraj = FetchTrajectory(conn, ModelCheckpoint.HeadSliceIds(refMap[kName], H)[3])!;

        Assert.Equal(n, FactorTrajectory.TokenCount(qTraj.Length / 4, Hd));
        double qArena = FactorTrajectory.UnpackArena(qTraj);
        Assert.True(qArena > 0, "arena vertex missing or non-positive");
        _out.WriteLine($"tokens={n}, q-arena={qArena:F4}");

        double[] qDirect = ProjectWithBias(X, n, refMap, qName, H * Hd);
        double[] kDirect = ProjectWithBias(X, n, refMap, kName, H * Hd);

        var rng = new Random(11);
        int bitExact = 0;
        for (int probe = 0; probe < 500; probe++)
        {
            int t = rng.Next(n);
            (Hash128 tokId, long salFp) = FactorTrajectory.UnpackHeader(qTraj, t, Hd);
            Assert.Equal(tokenIds[t], tokId);            // in-DB identity law
            Assert.InRange(salFp, 0, 1_000_000_000);      // score-law range
            float[] qBack = FactorTrajectory.UnpackFactors(qTraj, t, Hd);
            for (int j = 0; j < Hd; j++, bitExact++)
                Assert.Equal(
                    BitConverter.SingleToUInt32Bits((float)qDirect[(long)t * H * Hd + 3 * Hd + j]),
                    BitConverter.SingleToUInt32Bits(qBack[j]));
        }
        _out.WriteLine($"gate qk-factors: {bitExact} values bit-exact + 500 header token ids exact");

        // Pair scores through readback vs f64 kernel-direct, CS-scaled.
        double maxScaled = 0;
        for (int probe = 0; probe < 2000; probe++)
        {
            int a = rng.Next(n), b = rng.Next(n);
            float[] qa = FactorTrajectory.UnpackFactors(qTraj, a, Hd);
            float[] kb = FactorTrajectory.UnpackFactors(kTraj, b, Hd);
            double dot = 0, qn = 0, kn = 0;
            for (int j = 0; j < Hd; j++)
            { dot += (double)qa[j] * kb[j]; qn += (double)qa[j] * qa[j]; kn += (double)kb[j] * kb[j]; }
            double direct = 0;
            for (int j = 0; j < Hd; j++)
                direct += qDirect[(long)a * H * Hd + 3 * Hd + j] * kDirect[(long)b * H * Hd + 3 * Hd + j];
            maxScaled = Math.Max(maxScaled,
                Math.Abs(dot - direct) / Math.Max(Math.Sqrt(qn * kn), 1e-30));
        }
        _out.WriteLine($"gate qk-pairs: 2000 scores, max CS-scaled err {maxScaled:E2}");
        Assert.True(maxScaled < CsTol);

        // ---- Plane 2: EMB — readback rows must equal the probe input rows.
        double[] embTraj = FetchTrajectory(conn,
            ModelCheckpoint.HeadSliceIds(refMap["embeddings.word_embeddings.weight"], 1)[0])!;
        for (int probe = 0; probe < 100; probe++)
        {
            int t = rng.Next(n);
            float[] back = FactorTrajectory.UnpackFactors(embTraj, t, D);
            for (int j = 0; j < D; j++)
                Assert.Equal(BitConverter.SingleToUInt32Bits((float)X[(long)t * D + j]),
                             BitConverter.SingleToUInt32Bits(back[j]));
        }
        _out.WriteLine("gate emb: 100 rows bit-exact vs probe input");

        // ---- Plane 3: MLP write vectors (L0), erf-GELU + up-bias.
        double[] mlpTraj = FetchTrajectory(conn,
            ModelCheckpoint.HeadSliceIds(refMap["encoder.layer.0.output.dense.weight"], 1)[0])!;
        double[] mlpDirect = MlpWriteVectors(X, n, refMap, 0);
        AssertPlaneBitExact(mlpTraj, mlpDirect, D, rng, 50, "mlp");

        // ---- Plane 4: OV write vectors (L0.H3) on the O column slice.
        double[] ovTraj = FetchTrajectory(conn, ModelCheckpoint.ColumnSliceIds(
            refMap["encoder.layer.0.attention.output.dense.weight"], D, H * Hd, H)[3])!;
        double[] ovDirect = OvWriteVectors(X, n, refMap, 0, 3);
        AssertPlaneBitExact(ovTraj, ovDirect, D, rng, 50, "ov");
    }

    private void AssertPlaneBitExact(
        double[] traj, double[] direct, int dim, Random rng, int probes, string name)
    {
        int n = FactorTrajectory.TokenCount(traj.Length / 4, dim);
        for (int p = 0; p < probes; p++)
        {
            int t = rng.Next(n);
            float[] back = FactorTrajectory.UnpackFactors(traj, t, dim);
            for (int j = 0; j < dim; j++)
                Assert.Equal(BitConverter.SingleToUInt32Bits((float)direct[(long)t * dim + j]),
                             BitConverter.SingleToUInt32Bits(back[j]));
        }
        _out.WriteLine($"gate {name}: {probes} write vectors bit-exact");
    }

    private static double[] BuildProbeInput(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        List<int> rowOfToken, int n)
    {
        float[] embed = WeightTensorETL.LoadTensorF32(refMap,
            "embeddings.word_embeddings.weight", (long)Vocab * D);
        var gathered = new float[(long)n * D];
        for (int i = 0; i < n; i++)
            Array.Copy(embed, (long)rowOfToken[i] * D, gathered, (long)i * D, D);
        var X = new double[(long)n * D];
        unsafe
        {
            fixed (float* pe = gathered) fixed (double* px = X)
                Assert.Equal(0, DynInterop.F32ToF64(pe, (nuint)((long)n * D), px));
        }
        AddRow0(X, n, refMap, "embeddings.position_embeddings.weight");
        AddRow0(X, n, refMap, "embeddings.token_type_embeddings.weight");
        float[] gamma = WeightTensorETL.LoadTensorF32(refMap, "embeddings.LayerNorm.weight", D);
        float[] beta = WeightTensorETL.LoadTensorF32(refMap, "embeddings.LayerNorm.bias", D);
        unsafe
        {
            fixed (double* px = X) fixed (float* pg = gamma) fixed (float* pb = beta)
                Assert.Equal(0, DynInterop.LayerNormRowsD(px, (nuint)n, D, pg, pb, 1e-12));
        }
        return X;
    }

    private static double[] MlpWriteVectors(
        double[] X, int n, Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, int L)
    {
        float[] up = WeightTensorETL.LoadTensorF32(refMap,
            $"encoder.layer.{L}.intermediate.dense.weight", (long)I * D);
        float[] upBias = WeightTensorETL.LoadTensorF32(refMap,
            $"encoder.layer.{L}.intermediate.dense.bias", I);
        float[] down = WeightTensorETL.LoadTensorF32(refMap,
            $"encoder.layer.{L}.output.dense.weight", (long)D * I);
        var Fv = new double[(long)n * D];
        unsafe
        {
            fixed (double* px = X) fixed (float* pu = up) fixed (float* pub = upBias)
            fixed (float* pd = down) fixed (double* po = Fv)
                Assert.Equal(0, DynInterop.FfnWriteVectorsD(px, (nuint)n, D, pu, pub, null,
                    (nuint)I, pd, D, 1, po));
        }
        return Fv;
    }

    private static double[] OvWriteVectors(
        double[] X, int n, Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        int L, int h)
    {
        double[] V = ProjectWithBias(X, n, refMap,
            $"encoder.layer.{L}.attention.self.value.weight", H * Hd);
        float[] Wo = WeightTensorETL.LoadTensorF32(refMap,
            $"encoder.layer.{L}.attention.output.dense.weight", (long)D * H * Hd);
        var Vh = new double[(long)n * Hd];
        unsafe
        {
            fixed (double* pf = V) fixed (double* ph = Vh)
                Assert.Equal(0, DynInterop.SliceHeadD(pf, ph, (nuint)n, (nuint)(H * Hd), (nuint)h, Hd));
        }
        var WoH = new float[(long)D * Hd];
        for (int row = 0; row < D; row++)
            Array.Copy(Wo, (long)row * H * Hd + (long)h * Hd, WoH, (long)row * Hd, Hd);
        var OVh = new double[(long)n * D];
        unsafe
        {
            fixed (double* pv = Vh) fixed (float* pw = WoH) fixed (double* po = OVh)
                Assert.Equal(0, DynInterop.ProjectEmbeddingD(pv, (nuint)n, Hd, pw, D, po));
        }
        return OVh;
    }

    private static double[] ProjectWithBias(
        double[] X, int n,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string wName, int outDim)
    {
        float[] W = WeightTensorETL.LoadTensorF32(refMap, wName, (long)outDim * D);
        var P = new double[(long)n * outDim];
        unsafe
        {
            fixed (double* px = X) fixed (float* pw = W) fixed (double* pp = P)
                Assert.Equal(0, DynInterop.ProjectEmbeddingD(px, (nuint)n, D, pw, (nuint)outDim, pp));
        }
        float[] bias = WeightTensorETL.LoadTensorF32(refMap, ArchitectureProfile.BiasOf(wName), outDim);
        unsafe
        {
            fixed (double* pp = P) fixed (float* pb = bias)
                Assert.Equal(0, DynInterop.AddRowVectorD(pp, (nuint)n, (nuint)outDim, pb));
        }
        return P;
    }

    private static void AddRow0(
        double[] X, int n, Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
    {
        var t = refMap[name];
        long rows = (t.AbsoluteDataEnd - t.AbsoluteDataStart) / 4 / D;
        float[] full = WeightTensorETL.LoadTensorF32(refMap, name, rows * D);
        var row0 = new float[D];
        Array.Copy(full, 0, row0, 0, D);
        unsafe
        {
            fixed (double* px = X) fixed (float* pv = row0)
                Assert.Equal(0, DynInterop.AddRowVectorD(px, (nuint)n, D, pv));
        }
    }

    private static double[]? FetchTrajectory(NpgsqlConnection conn, Hash128 slice)
    {
        var physId = PhysicalityId.Compute(slice, PhysicalityType.Projection);
        using var cmd = new NpgsqlCommand(
            "SELECT ST_AsBinary(trajectory) FROM laplace.physicalities WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", physId.ToBytes());
        var wkb = cmd.ExecuteScalar() as byte[];
        if (wkb is null) return null;

        // WKB LineStringZM LE: 1 byte order, 4 type, 4 count, then count*4 f64.
        Assert.Equal(1, wkb[0]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(5, 4));
        var xyzm = new double[count * 4];
        for (int i = 0; i < xyzm.Length; i++)
            xyzm[i] = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(wkb.AsSpan(9 + i * 8, 8)));
        return xyzm;
    }
}
