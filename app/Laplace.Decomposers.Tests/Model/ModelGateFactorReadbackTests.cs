using System.Buffers.Binary;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model.Tests;

// THE readback gate (campaign doc 26 item A, gate 1): after `ingest model` in
// factors mode, factor trajectories pulled back OUT of the database must
// reconstruct the checkpoint's math. Kernel-direct reference is recomputed here
// from the checkpoint bytes through the same natives; slice ids are recomputed
// independently through the content law. Chain under test:
//   probe input -> projection -> pack -> COPY -> PostGIS -> WKB -> unpack.
// Factor storage is f32, so factor equality is BIT-exact; dot products compare
// f32-chain vs f64-direct at 1e-5 relative.
public sealed class ModelGateFactorReadbackTests
{
    private const string Snap =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";
    private const string Conn =
        "Host=localhost;Username=postgres;Password=postgres;Database=laplace;Search Path=laplace,public";

    private readonly ITestOutputHelper _out;
    public ModelGateFactorReadbackTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ModelGate_ATT_QPROJ_MiniLM_Readback_Matches_KernelDirect()
    {
        if (!File.Exists(Path.Combine(Snap, "model.safetensors")))
        { _out.WriteLine("SKIPPED: MiniLM snapshot missing"); return; }

        var refs = SafetensorsContainerParser.ParseModel(Snap);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;

        const int d = 384, H = 12, hd = 32, vocab = 30522;
        const string qName = "encoder.layer.0.attention.self.query.weight";
        const string kName = "encoder.layer.0.attention.self.key.weight";

        Hash128[] qSlices = ModelCheckpoint.HeadSliceIds(refMap[qName], H);
        Hash128[] kSlices = ModelCheckpoint.HeadSliceIds(refMap[kName], H);

        using var conn = new NpgsqlConnection(Conn);
        conn.Open();
        double[]? qTraj = FetchTrajectory(conn, qSlices[3]);
        if (qTraj is null)
        { _out.WriteLine("SKIPPED: no factor deposit found (run `ingest model` with LAPLACE_MODEL_PLANES=factors)"); return; }
        double[] kTraj = FetchTrajectory(conn, kSlices[3])
            ?? throw new InvalidOperationException("q slice deposited but k slice missing");

        // Kernel-direct reference: the true BERT layer-0 probe input, projected.
        var profile = ArchitectureProfile.Bert;
        float[] embed = WeightTensorETL.LoadTensorF32(refMap, profile.EmbedTokens, (long)vocab * d);
        int n = qTraj.Length / 4 / VPerTok(hd);
        _out.WriteLine($"deposited tokens per slice: {n}");
        Assert.Equal(kTraj.Length, qTraj.Length);

        var X = new double[(long)n * d];
        unsafe
        {
            fixed (float* pe = embed) fixed (double* px = X)
                Assert.Equal(0, DynInterop.F32ToF64(pe, (nuint)((long)n * d), px));
        }
        AddRow0(X, n, d, refMap, profile.PositionEmbeddings!);
        AddRow0(X, n, d, refMap, profile.TokenTypeEmbeddings!);
        float[] gamma = WeightTensorETL.LoadTensorF32(refMap, profile.EmbeddingNormWeight!, d);
        float[] beta = WeightTensorETL.LoadTensorF32(refMap, profile.EmbeddingNormBias!, d);
        unsafe
        {
            fixed (double* px = X) fixed (float* pg = gamma) fixed (float* pb = beta)
                Assert.Equal(0, DynInterop.LayerNormRowsD(px, (nuint)n, (nuint)d, pg, pb, profile.NormEps));
        }

        double[] qDirect = ProjectWithBias(X, n, d, refMap, qName, H * hd);
        double[] kDirect = ProjectWithBias(X, n, d, refMap, kName, H * hd);

        // Gate 1: factor readback is BIT-exact vs kernel-direct (f32 cast).
        int checkedVals = 0;
        var rng = new Random(11);
        for (int probe = 0; probe < 500; probe++)
        {
            int t = rng.Next(n);
            float[] qBack = UnpackToken(qTraj, t, hd);
            for (int j = 0; j < hd; j++, checkedVals++)
            {
                float direct = (float)qDirect[(long)t * H * hd + 3 * hd + j];
                Assert.Equal(BitConverter.SingleToUInt32Bits(direct),
                             BitConverter.SingleToUInt32Bits(qBack[j]));
            }
        }
        _out.WriteLine($"gate 1 PASS: {checkedVals} factor values bit-exact through the substrate");

        // Gate 2: pair scores q_h(A)·k_h(B) from DB-readback factors vs f64
        // kernel-direct, 1e-5 relative.
        double maxRel = 0;
        for (int probe = 0; probe < 2000; probe++)
        {
            int a = rng.Next(n), b = rng.Next(n);
            float[] qa = UnpackToken(qTraj, a, hd);
            float[] kb = UnpackToken(kTraj, b, hd);
            double dot = 0;
            for (int j = 0; j < hd; j++) dot += (double)qa[j] * kb[j];
            double direct = 0;
            for (int j = 0; j < hd; j++)
                direct += qDirect[(long)a * H * hd + 3 * hd + j] * kDirect[(long)b * H * hd + 3 * hd + j];
            double rel = Math.Abs(dot - direct) / Math.Max(Math.Abs(direct), 1e-9);
            maxRel = Math.Max(maxRel, rel);
        }
        _out.WriteLine($"gate 2: 2000 pair scores, max relative error {maxRel:E2}");
        Assert.True(maxRel < 1e-5, $"pair-score max rel err {maxRel:E2} exceeds 1e-5");
    }

    private static int VPerTok(int hd) =>
        (hd + FactorWalk.ValuesPerVertex - 1) / FactorWalk.ValuesPerVertex;

    private static float[] UnpackToken(double[] traj, int t, int hd)
    {
        int v = VPerTok(hd);
        float[] vals = FactorWalk.Unpack(traj.AsSpan(t * v * 4, v * 4));
        Assert.Equal(hd, vals.Length);
        return vals;
    }

    private double[]? FetchTrajectory(NpgsqlConnection conn, Hash128 slice)
    {
        var physId = PhysicalityId.Compute(slice, PhysicalityType.Content + 0);
        // PhysicalityType.Projection is the deposited type:
        physId = PhysicalityId.Compute(slice, PhysicalityType.Projection);
        using var cmd = new NpgsqlCommand(
            "SELECT ST_AsBinary(trajectory) FROM laplace.physicalities WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", physId.ToByteArray());
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

    private static double[] ProjectWithBias(double[] X, int n, int d,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string wName, int outDim)
    {
        float[] W = WeightTensorETL.LoadTensorF32(refMap, wName, (long)outDim * d);
        var P = new double[(long)n * outDim];
        unsafe
        {
            fixed (double* px = X) fixed (float* pw = W) fixed (double* pp = P)
                Assert.Equal(0, DynInterop.ProjectEmbeddingD(px, (nuint)n, (nuint)d, pw, (nuint)outDim, pp));
        }
        float[] bias = WeightTensorETL.LoadTensorF32(refMap, ArchitectureProfile.BiasOf(wName), outDim);
        unsafe
        {
            fixed (double* pp = P) fixed (float* pb = bias)
                Assert.Equal(0, DynInterop.AddRowVectorD(pp, (nuint)n, (nuint)outDim, pb));
        }
        return P;
    }

    private static void AddRow0(double[] X, int n, int d,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
    {
        var t = refMap[name];
        long rows = (t.AbsoluteDataEnd - t.AbsoluteDataStart) / 4 / d;
        float[] full = WeightTensorETL.LoadTensorF32(refMap, name, rows * d);
        var row0 = new float[d];
        Array.Copy(full, 0, row0, 0, d);
        unsafe
        {
            fixed (double* px = X) fixed (float* pv = row0)
                Assert.Equal(0, DynInterop.AddRowVectorD(px, (nuint)n, (nuint)d, pv));
        }
    }
}
