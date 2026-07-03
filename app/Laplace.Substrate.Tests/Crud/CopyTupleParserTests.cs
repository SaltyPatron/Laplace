using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The write protocol trusts CopyTupleParser to see exactly the rows the
/// native IntentStage staged, and to re-emit any subset byte-identically
/// (modulo the deliberate observation_count patch). These tests hold the
/// parser against the stage's own emission.
/// </summary>
public class CopyTupleParserTests
{
    private static Hash128 H(int seed) => Hash128.Blake3(BitConverter.GetBytes(seed));

    private static List<(IntPtr Ptr, long Len)> Blobs(IntentStage s, IntentStageTable t)
    {
        var (ptr, len) = s.TupleBuffer(t);
        return len > 0 ? new List<(IntPtr, long)> { (ptr, len) } : new List<(IntPtr, long)>();
    }

    private static async Task<byte[]> EmitAsync(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        IReadOnlyList<StagedRowRef> rows,
        long[]? patches = null,
        IReadOnlyList<int>? countOffs = null)
    {
        using var ms = new MemoryStream();
        await CopyTupleParser.WriteFilteredAsync(ms, blobs, rows, patches, countOffs);
        return ms.ToArray();
    }

    /// <summary>Strips the PGCOPY header/trailer and hands the body to a parse function.</summary>
    private static T ParseBody<T>(byte[] wire, Func<IReadOnlyList<(IntPtr, long)>, T> parse)
    {
        int headerLen = PgBinaryCopy.Header.Length;
        int bodyLen = wire.Length - headerLen - PgBinaryCopy.Trailer.Length;
        var body = new byte[bodyLen];
        Array.Copy(wire, headerLen, body, 0, bodyLen);
        var handle = GCHandle.Alloc(body, GCHandleType.Pinned);
        try
        {
            return parse(new List<(IntPtr, long)> { (handle.AddrOfPinnedObject(), bodyLen) });
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public async Task Entities_ParseAndFullEmit_MatchesStage()
    {
        using var stage = IntentStage.New(4);
        stage.AddEntity(H(1), 2, H(100), H(200));
        stage.AddEntity(H(2), 0, H(100), null);
        stage.AddEntity(H(3), 4, H(101), H(200));

        var blobs = Blobs(stage, IntentStageTable.Entities);
        var parsed = CopyTupleParser.ParseEntities(blobs);

        Assert.Equal(new[] { H(1), H(2), H(3) }, parsed.Ids);
        Assert.Equal(3, parsed.Rows.Count);

        // EmitCopyBinary is the native side's own full PGCOPY emission
        // (header + tuples + trailer) — the managed re-emission of all rows
        // must be byte-identical to it.
        var wire = await EmitAsync(blobs, parsed.Rows);
        Assert.Equal(stage.EmitCopyBinary(IntentStageTable.Entities), wire);
    }

    [Fact]
    public async Task Entities_FilteredEmit_KeepsOnlySelectedRows()
    {
        using var stage = IntentStage.New(4);
        stage.AddEntity(H(1), 2, H(100), null);
        stage.AddEntity(H(2), 2, H(100), null);
        stage.AddEntity(H(3), 2, H(100), null);

        var blobs = Blobs(stage, IntentStageTable.Entities);
        var parsed = CopyTupleParser.ParseEntities(blobs);
        var kept = new[] { parsed.Rows[0], parsed.Rows[2] };

        var wire = await EmitAsync(blobs, kept);
        var reparsed = ParseBody(wire, CopyTupleParser.ParseEntities);
        Assert.Equal(new[] { H(1), H(3) }, reparsed.Ids);
    }

    [Fact]
    public void Physicalities_ParseExtractsIdAndEntityRef()
    {
        using var stage = IntentStage.New(4);
        Span<double> coord = stackalloc double[4] { 0.1, 0.2, 0.3, 0.4 };
        var traj = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        stage.AddPhysicality(H(10), H(1), 1, coord, default, traj, 2, 0.5, 4, 1_000_000);
        stage.AddPhysicality(H(11), H(2), 1, coord, default, ReadOnlySpan<double>.Empty, 0, null, null, 2_000_000);

        var parsed = CopyTupleParser.ParsePhysicalities(Blobs(stage, IntentStageTable.Physicalities));
        Assert.Equal(new[] { H(10), H(11) }, parsed.Ids);
        Assert.Equal(new[] { H(1), H(2) }, parsed.EntityIds);
    }

    [Fact]
    public async Task Attestations_ParseAndCountPatch_RoundTrip()
    {
        long ts1 = 1_700_000_000_000_000; // unix µs
        long ts2 = 1_700_000_999_000_000;
        using var stage = IntentStage.New(4);
        stage.AddAttestation(H(20), H(1), H(2), H(3), H(4), null, 2, ts1, 3);
        stage.AddAttestation(H(21), H(1), H(2), null, H(4), H(5), 1, ts2, 7);

        var blobs = Blobs(stage, IntentStageTable.Attestations);
        var parsed = CopyTupleParser.ParseAttestations(blobs);

        Assert.Equal(new[] { H(20), H(21) }, parsed.Ids);
        Assert.Equal(new[] { 3L, 7L }, parsed.Counts);
        Assert.Equal(ts1 - IntentStage.PgEpochUnixUs, parsed.TimestampsPgUs[0]);
        Assert.Equal(ts2 - IntentStage.PgEpochUnixUs, parsed.TimestampsPgUs[1]);

        // Emit only the second row with its count patched to a group sum.
        var kept = new[] { parsed.Rows[1] };
        var wire = await EmitAsync(blobs, kept,
            patches: new[] { 42L },
            countOffs: new[] { parsed.CountValueOffsets[1] });

        var reparsed = ParseBody(wire, CopyTupleParser.ParseAttestations);
        Assert.Equal(new[] { H(21) }, reparsed.Ids);
        Assert.Equal(new[] { 42L }, reparsed.Counts);
        Assert.Equal(ts2 - IntentStage.PgEpochUnixUs, reparsed.TimestampsPgUs[0]);
    }

    [Fact]
    public async Task MultipleBlobs_EmitCoalescesAcrossBlobBoundaries()
    {
        using var s1 = IntentStage.New(2);
        using var s2 = IntentStage.New(2);
        s1.AddEntity(H(1), 2, H(100), null);
        s1.AddEntity(H(2), 2, H(100), null);
        s2.AddEntity(H(3), 2, H(100), null);

        var blobs = new List<(IntPtr, long)>();
        blobs.AddRange(Blobs(s1, IntentStageTable.Entities));
        blobs.AddRange(Blobs(s2, IntentStageTable.Entities));

        var parsed = CopyTupleParser.ParseEntities(blobs);
        Assert.Equal(new[] { H(1), H(2), H(3) }, parsed.Ids);
        Assert.Equal(0, parsed.Rows[0].Blob);
        Assert.Equal(1, parsed.Rows[2].Blob);

        var wire = await EmitAsync(blobs, parsed.Rows);
        var reparsed = ParseBody(wire, CopyTupleParser.ParseEntities);
        Assert.Equal(new[] { H(1), H(2), H(3) }, reparsed.Ids);
    }
}
