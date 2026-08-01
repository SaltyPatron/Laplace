using System.Buffers.Binary;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// One staged tuple inside a native <see cref="IntentStage"/> COPY blob:
/// which blob, where it starts, how many bytes. The blob memory is owned by
/// the stage — refs are only valid while the stage is alive.
/// </summary>
internal readonly record struct StagedRowRef(int Blob, long Offset, int Length);

/// <summary>
/// Reads the raw PostgreSQL COPY-binary tuple streams that
/// <see cref="IntentStage.TupleBuffer"/> exposes (no PGCOPY header/trailer —
/// <see cref="PgBinaryCopy"/> adds those on the wire), extracting exactly the
/// fields the write protocol's in-transaction verification needs: every row's
/// id, a physicality's entity reference, and an attestation's merge inputs
/// (last_observed_at, observation_count). Layout comes from intent_stage.c's
/// column lists; nullable fields (first_observed_by, object_id, context_id,
/// trajectory, highway_mask, ...) are length -1 and skipped like any other.
/// </summary>
internal static class CopyTupleParser
{
    internal sealed class EntityRows
    {
        public readonly List<Hash128> Ids = new();
        /// <summary>Partition key (LIST(tier), t2 further HASH(id)) — the
        /// keyed presence probe needs it because id alone cannot prune.</summary>
        public readonly List<short> Tiers = new();
        /// <summary>type_id — secondary-index contention key for parallel COPY.</summary>
        public readonly List<Hash128> TypeIds = new();
        public readonly List<StagedRowRef> Rows = new();
    }

    internal sealed class PhysicalityRows
    {
        public readonly List<Hash128> Ids = new();
        public readonly List<Hash128> EntityIds = new();
        /// <summary>128-bit Hilbert curve index of the row's coord (wire packing
        /// is 16 octets; value is the index, not a hash).</summary>
        public readonly List<Hilbert128> HilbertKeys = new();
        public readonly List<StagedRowRef> Rows = new();
    }

    internal sealed class AttestationRows
    {
        public readonly List<Hash128> Ids = new();
        /// <summary>Partition keys (LIST(type_id) -> HASH(subject_id)) — the
        /// keyed presence probe needs them because id alone cannot prune.</summary>
        public readonly List<Hash128> SubjectIds = new();
        public readonly List<Hash128> TypeIds = new();
        /// <summary>Remaining id-embedded entity references (attestation id =
        /// BLAKE3(subject‖type‖object‖source‖context)): a row whose subject,
        /// object, or context entity is novel in this batch is novel by
        /// construction and needs no presence probe. NULL columns parse to the
        /// zero hash — the same sentinel the id computation hashes for them —
        /// which can never collide with a real novel entity id.</summary>
        public readonly List<Hash128> ObjectIds = new();
        public readonly List<Hash128> ContextIds = new();
        /// <summary>last_observed_at as stored on the wire (µs since PG epoch 2000-01-01).</summary>
        public readonly List<long> TimestampsPgUs = new();
        public readonly List<long> Counts = new();
        /// <summary>Offset of the 8 observation_count value bytes, relative to row start.</summary>
        public readonly List<int> CountValueOffsets = new();
        /// <summary>sum_score_fp1e9 — the fold's exact score total for this row.</summary>
        public readonly List<long> SumScores = new();
        /// <summary>Offset of the 8 sum_score_fp1e9 value bytes, relative to row start.</summary>
        public readonly List<int> SumScoreValueOffsets = new();
        public readonly List<StagedRowRef> Rows = new();
    }

    private const int EntityFields = 4;
    private const int PhysicalityFields = 10;
    private const int AttestationFields = 12;

    public static unsafe EntityRows ParseEntities(IReadOnlyList<(IntPtr Ptr, long Len)> blobs)
    {
        var result = new EntityRows();
        for (int b = 0; b < blobs.Count; b++)
        {
            var (ptr, len) = blobs[b];
            byte* p = (byte*)ptr;
            long off = 0;
            while (off < len)
            {
                long rowStart = off;
                Hash128 id = default, typeId = default;
                short tier = 0;
                WalkRow(p, len, ref off, EntityFields, "entities", (field, valOff, valLen) =>
                {
                    if (field == 0) id = ReadHash(p, valOff, valLen, "entities.id");
                    else if (field == 1) tier = ReadInt16(p, valOff, valLen, "entities.tier");
                    else if (field == 2) typeId = ReadHash(p, valOff, valLen, "entities.type_id");
                });
                result.Ids.Add(id);
                result.Tiers.Add(tier);
                result.TypeIds.Add(typeId);
                result.Rows.Add(new StagedRowRef(b, rowStart, checked((int)(off - rowStart))));
            }
        }
        return result;
    }

    public static unsafe PhysicalityRows ParsePhysicalities(IReadOnlyList<(IntPtr Ptr, long Len)> blobs)
    {
        var result = new PhysicalityRows();
        for (int b = 0; b < blobs.Count; b++)
        {
            var (ptr, len) = blobs[b];
            byte* p = (byte*)ptr;
            long off = 0;
            while (off < len)
            {
                long rowStart = off;
                Hash128 id = default, entityId = default;
                Hilbert128 hilbert = default;
                WalkRow(p, len, ref off, PhysicalityFields, "physicalities", (field, valOff, valLen) =>
                {
                    if (field == 0) id = ReadHash(p, valOff, valLen, "physicalities.id");
                    else if (field == 1) entityId = ReadHash(p, valOff, valLen, "physicalities.entity_id");
                    else if (field == 4)
                        hilbert = ReadHilbert(p, valOff, valLen, "physicalities.hilbert_index");
                });
                result.Ids.Add(id);
                result.EntityIds.Add(entityId);
                result.HilbertKeys.Add(hilbert);
                result.Rows.Add(new StagedRowRef(b, rowStart, checked((int)(off - rowStart))));
            }
        }
        return result;
    }

    public static unsafe AttestationRows ParseAttestations(IReadOnlyList<(IntPtr Ptr, long Len)> blobs)
    {
        var result = new AttestationRows();
        for (int b = 0; b < blobs.Count; b++)
        {
            var (ptr, len) = blobs[b];
            byte* p = (byte*)ptr;
            long off = 0;
            while (off < len)
            {
                long rowStart = off;
                Hash128 id = default, subjectId = default, typeId = default;
                Hash128 objectId = default, contextId = default;
                long ts = 0, games = 0, sumScore = 0;
                long countValOff = -1, sumValOff = -1;
                WalkRow(p, len, ref off, AttestationFields, "attestations", (field, valOff, valLen) =>
                {
                    switch (field)
                    {
                        case 0: id = ReadHash(p, valOff, valLen, "attestations.id"); break;
                        case 1: subjectId = ReadHash(p, valOff, valLen, "attestations.subject_id"); break;
                        case 2: typeId = ReadHash(p, valOff, valLen, "attestations.type_id"); break;
                        case 3:
                            if (valLen == 16) objectId = ReadHash(p, valOff, valLen, "attestations.object_id");
                            break;
                        case 5:
                            if (valLen == 16) contextId = ReadHash(p, valOff, valLen, "attestations.context_id");
                            break;
                        case 7: ts = ReadInt64(p, valOff, valLen, "attestations.last_observed_at"); break;
                        case 8:
                            games = ReadInt64(p, valOff, valLen, "attestations.observation_count");
                            countValOff = valOff;
                            break;
                        case 9:
                            sumScore = ReadInt64(p, valOff, valLen, "attestations.sum_score_fp1e9");
                            sumValOff = valOff;
                            break;
                    }
                });
                if (countValOff < 0)
                    throw new InvalidOperationException("attestations row missing observation_count");
                if (sumValOff < 0)
                    throw new InvalidOperationException("attestations row missing sum_score_fp1e9");
                result.Ids.Add(id);
                result.SubjectIds.Add(subjectId);
                result.TypeIds.Add(typeId);
                result.ObjectIds.Add(objectId);
                result.ContextIds.Add(contextId);
                result.TimestampsPgUs.Add(ts);
                result.Counts.Add(games);
                result.CountValueOffsets.Add(checked((int)(countValOff - rowStart)));
                result.SumScores.Add(sumScore);
                result.SumScoreValueOffsets.Add(checked((int)(sumValOff - rowStart)));
                result.Rows.Add(new StagedRowRef(b, rowStart, checked((int)(off - rowStart))));
            }
        }
        return result;
    }

    private unsafe delegate void FieldVisitor(int field, long valueOffset, int valueLength);

    private static unsafe void WalkRow(
        byte* p, long len, ref long off, int expectedFields, string table, FieldVisitor visit)
    {
        if (off + 2 > len)
            throw Corrupt(table, off, "truncated field count");
        int fields = (p[off] << 8) | p[off + 1];
        if (fields != expectedFields)
            throw Corrupt(table, off, $"field count {fields}, expected {expectedFields}");
        off += 2;
        for (int f = 0; f < fields; f++)
        {
            if (off + 4 > len)
                throw Corrupt(table, off, $"truncated length prefix at field {f}");
            int flen = (p[off] << 24) | (p[off + 1] << 16) | (p[off + 2] << 8) | p[off + 3];
            off += 4;
            if (flen == -1) { visit(f, off, -1); continue; }
            if (flen < 0 || off + flen > len)
                throw Corrupt(table, off, $"field {f} length {flen} overruns blob");
            visit(f, off, flen);
            off += flen;
        }
    }

    private static unsafe Hash128 ReadHash(byte* p, long valOff, int valLen, string what)
    {
        if (valLen != 16)
            throw new InvalidOperationException($"{what}: expected 16-byte value, got {valLen}");
        return Hash128.FromBytes(new ReadOnlySpan<byte>(p + valOff, 16));
    }

    private static unsafe Hilbert128 ReadHilbert(byte* p, long valOff, int valLen, string what)
    {
        if (valLen != 16)
            throw new InvalidOperationException($"{what}: expected 128-bit hilbert index (16 octets), got {valLen}");
        return Hilbert128.FromBytes(new ReadOnlySpan<byte>(p + valOff, 16));
    }

    private static unsafe long ReadInt64(byte* p, long valOff, int valLen, string what)
    {
        if (valLen != 8)
            throw new InvalidOperationException($"{what}: expected 8-byte value, got {valLen}");
        return BinaryPrimitives.ReadInt64BigEndian(new ReadOnlySpan<byte>(p + valOff, 8));
    }

    private static unsafe short ReadInt16(byte* p, long valOff, int valLen, string what)
    {
        if (valLen != 2)
            throw new InvalidOperationException($"{what}: expected 2-byte value, got {valLen}");
        return BinaryPrimitives.ReadInt16BigEndian(new ReadOnlySpan<byte>(p + valOff, 2));
    }

    private static InvalidOperationException Corrupt(string table, long off, string why) =>
        new($"COPY tuple stream corrupt in '{table}' at offset {off}: {why}");

    /// <summary>
    /// Streams the kept rows as one PGCOPY payload: header, then each row's
    /// bytes out of the native blobs into a large write buffer, then the
    /// trailer. Contiguous same-blob runs memcpy in one shot; after Apply
    /// sorts kept rows by id/hilbert for index locality, offsets scatter and
    /// the old path issued one <c>WriteAsync</c> per row — that is what this
    /// buffer kills. When <paramref name="patchedCounts"/> is non-null, a row
    /// with patchedCounts[i] >= 0 has its observation_count / sum_score_fp1e9
    /// rewritten on the way out.
    /// </summary>
    public static async Task WriteFilteredAsync(
        Stream stream,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts = null,
        IReadOnlyList<int>? countValueOffsets = null,
        long[]? patchedSums = null,
        IReadOnlyList<int>? sumValueOffsets = null,
        CancellationToken ct = default)
    {
        if (patchedCounts is not null && countValueOffsets is null)
            throw new ArgumentNullException(nameof(countValueOffsets));
        if (patchedCounts is not null && (patchedSums is null || sumValueOffsets is null))
            throw new ArgumentNullException(nameof(patchedSums));

        await stream.WriteAsync(PgBinaryCopy.Header, ct).ConfigureAwait(false);

        // 4 MiB flush cadence — large enough that sorted (non-contiguous) kept
        // rows stop paying a syscall/await per tuple on the Npgsql COPY stream.
        const int FlushBytes = 4 << 20;
        byte[] buf = new byte[FlushBytes];
        int filled = 0;

        async ValueTask FlushAsync()
        {
            if (filled == 0) return;
            await stream.WriteAsync(buf.AsMemory(0, filled), ct).ConfigureAwait(false);
            filled = 0;
        }

        async ValueTask AppendBytesAsync(byte[] src)
        {
            int off = 0;
            while (off < src.Length)
            {
                if (filled == buf.Length) await FlushAsync().ConfigureAwait(false);
                int n = Math.Min(src.Length - off, buf.Length - filled);
                src.AsSpan(off, n).CopyTo(buf.AsSpan(filled));
                filled += n;
                off += n;
            }
        }

        int i = 0;
        while (i < rows.Count)
        {
            if (patchedCounts is not null && patchedCounts[i] >= 0)
            {
                var row = rows[i];
                // Patch into a scratch that rides the flush buffer when small,
                // otherwise a one-off window (observation-count rewrite path).
                if (row.Length <= buf.Length - filled)
                {
                    unsafe
                    {
                        new ReadOnlySpan<byte>((void*)(blobs[row.Blob].Ptr + (nint)row.Offset), row.Length)
                            .CopyTo(buf.AsSpan(filled));
                    }
                    BinaryPrimitives.WriteInt64BigEndian(
                        buf.AsSpan(filled + countValueOffsets![i], 8), patchedCounts[i]);
                    BinaryPrimitives.WriteInt64BigEndian(
                        buf.AsSpan(filled + sumValueOffsets![i], 8), patchedSums![i]);
                    filled += row.Length;
                }
                else
                {
                    await FlushAsync().ConfigureAwait(false);
                    var scratch = new byte[row.Length];
                    unsafe
                    {
                        new ReadOnlySpan<byte>((void*)(blobs[row.Blob].Ptr + (nint)row.Offset), row.Length)
                            .CopyTo(scratch);
                    }
                    BinaryPrimitives.WriteInt64BigEndian(
                        scratch.AsSpan(countValueOffsets![i], 8), patchedCounts[i]);
                    BinaryPrimitives.WriteInt64BigEndian(
                        scratch.AsSpan(sumValueOffsets![i], 8), patchedSums![i]);
                    await AppendBytesAsync(scratch).ConfigureAwait(false);
                }
                i++;
                continue;
            }

            // Contiguous same-blob run — one memcpy into the flush buffer.
            var first = rows[i];
            long runLen = first.Length;
            int j = i + 1;
            while (j < rows.Count
                   && (patchedCounts is null || patchedCounts[j] < 0)
                   && rows[j].Blob == first.Blob
                   && rows[j].Offset == first.Offset + runLen)
            {
                runLen += rows[j].Length;
                j++;
            }

            IntPtr basePtr = blobs[first.Blob].Ptr + (nint)first.Offset;
            for (long done = 0; done < runLen;)
            {
                if (filled == buf.Length) await FlushAsync().ConfigureAwait(false);
                int n = (int)Math.Min(runLen - done, buf.Length - filled);
                unsafe
                {
                    new ReadOnlySpan<byte>((void*)(basePtr + (nint)done), n)
                        .CopyTo(buf.AsSpan(filled));
                }
                filled += n;
                done += n;
            }
            i = j;
        }

        await FlushAsync().ConfigureAwait(false);
        await stream.WriteAsync(PgBinaryCopy.Trailer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
