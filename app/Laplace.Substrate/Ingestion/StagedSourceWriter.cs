using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using global::Npgsql;

namespace Laplace.Ingestion;

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct StagedReference
{
    public Hash128 Id;
    public long X, Y, Z, W;
    public byte Tier, Said, Outcome;
    public uint Position, Spare;
}

public readonly record struct StagedWriteResult(
    long Entities, long Physicalities, long UpdatedRows, int ProbeRounds, long ProbedIds);

/// <summary>
/// One bounded Native structural window. Native owns canonical composition,
/// carrier validation, subtree pruning and complete five-column COPY streams.
/// This transport neither mints source witnesses nor computes standings.
/// The caller controls the window transaction and source publication.
/// </summary>
public sealed partial class StagedSourceWriter : IDisposable
{
    private readonly StageHandle _stage;
    private int _access;

    public StagedSourceWriter(int maximumNodes, int maximumVertices, long maximumCopyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumVertices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCopyBytes);
        if (Native.Floor(LaplaceInstall.ResolveT0Perfcache()) == IntPtr.Zero)
            throw new InvalidOperationException("Cannot map the canonical Native scalar floor.");
        IntPtr highway = Native.Highway(LaplaceInstall.ResolveHighwayPerfcache());
        if (highway == IntPtr.Zero)
            throw new InvalidOperationException("Cannot map the canonical Native highway.");
        IntPtr stage = Native.New((nuint)maximumNodes, (nuint)maximumVertices,
            checked((nuint)maximumCopyBytes), highway);
        if (stage == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate the Native working window.");
        _stage = new StageHandle(stage);
    }

    public unsafe StagedReference Text(ReadOnlySpan<byte> utf8)
    {
        IntPtr stage = Enter();
        try
        {
            StagedReference result;
            fixed (byte* bytes = utf8)
                Check(Native.Text(stage, bytes, (nuint)utf8.Length, &result));
            return result;
        }
        finally { Leave(); }
    }

    public unsafe StagedReference Compose(ReadOnlySpan<StagedReference> children, byte tier,
        ulong entityMask = 0, ulong pathMask = 0, bool structuralSpine = false)
    {
        IntPtr stage = Enter();
        try
        {
            StagedReference result;
            fixed (StagedReference* input = children)
                Check(Native.Compose(stage, input, (nuint)children.Length, tier,
                    entityMask, pathMask, structuralSpine ? 1 : 0, &result));
            return result;
        }
        finally { Leave(); }
    }

    /// <summary>Supply an authenticated existing-entity read, including its exact Native coordinate.</summary>
    public unsafe void RecordedReference(in StagedReference recorded)
    {
        IntPtr stage = Enter();
        try { fixed (StagedReference* input = &recorded) Check(Native.Reference(stage, input)); }
        finally { Leave(); }
    }

    public void Reset()
    {
        IntPtr stage = Enter();
        try { Native.Reset(stage); }
        finally { Leave(); }
    }

    // The connection and transaction belong to the caller; this operation never
    // commits another owner's transaction or registers an incomplete source.
    internal async Task<StagedWriteResult> WriteAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Hash128[] roots, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IntPtr stage = Enter();
        try
        {
            Begin(stage, roots);
            int rounds = 0;
            long probed = 0;
            for (Hash128[] frontier = Frontier(stage); frontier.Length != 0; frontier = Frontier(stage))
            {
                ct.ThrowIfCancellationRequested();
                await using var probe = NpgsqlCatalog.Command(connection, transaction,
                    "write.canonical_recorded", frontier);
                var present = (bool[]?)await probe.ExecuteScalarAsync(ct)
                    ?? throw new InvalidDataException("Canonical presence probe returned no bitmap.");
                if (present.Length != frontier.Length)
                    throw new InvalidDataException("Canonical presence bitmap has the wrong length.");
                Answer(stage, present);
                rounds++;
                probed += frontier.Length;
            }
            Check(Native.Finish(stage));
            Native.Copy(stage, 1, out _, out nuint rows);
            if (rows == 0) return new(0, 0, 0, rounds, probed);

            await using var identity = NpgsqlCatalog.Command(connection, transaction, "write.canonical_stage_id");
            string suffix = (string)(await identity.ExecuteScalarAsync(ct)
                ?? throw new InvalidDataException("Missing transaction identity for canonical staging."));
            if (suffix.Length == 0 || suffix.Any(c => c is < '0' or > '9'))
                throw new InvalidDataException("Invalid canonical staging transaction identity.");
            var tables = new Dictionary<string, string>
            {
                ["entity_stage"] = "stage.lp_entity_" + suffix,
                ["path_stage"] = "stage.lp_path_" + suffix
            };
            await using (var create = NpgsqlCatalog.Command(connection, transaction,
                "write.canonical_stage_create", identifiers: tables))
                await create.ExecuteNonQueryAsync(ct);
            await CopyAsync(connection, transaction, stage, 1,
                "write.canonical_copy_entities", "id,tier,coord,hilbert,mask", tables, ct);
            await CopyAsync(connection, transaction, stage, 2,
                "write.canonical_copy_paths", "entity,tier,hilbert,path,mask", tables, ct);
            StagedWriteResult result;
            await using (var merge = NpgsqlCatalog.Command(connection, transaction,
                "write.canonical_merge", tables["entity_stage"], tables["path_stage"]))
            await using (var reader = await merge.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new InvalidDataException("Native stage merge returned no receipt.");
                result = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), rounds, probed);
            }
            await using (var drop = NpgsqlCatalog.Command(connection, transaction,
                "write.canonical_stage_drop", identifiers: tables))
                await drop.ExecuteNonQueryAsync(ct);
            return result;
        }
        finally { Leave(); }
    }

    private static async Task CopyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IntPtr stage, int table, string query, string columns,
        IReadOnlyDictionary<string, string> tables, CancellationToken ct)
    {
        if (Marshal.PtrToStringUTF8(Native.Columns(table)) != columns)
            throw new InvalidDataException("Native canonical COPY columns disagree with the transport contract.");
        IntPtr bytes = Native.Copy(stage, table, out nuint length, out _);
        if (bytes == IntPtr.Zero || length < 21)
            throw new InvalidDataException("Native canonical COPY stream is unavailable.");
        // Native already supplies the complete PostgreSQL header and trailer.
        // Stream it in bounded protocol windows without decoding any tuples.
        int window = checked((int)Math.Min((ulong)length, (ulong)PgBinaryCopy.WriteWindowBytes));
        string slot = table == 1 ? "entity_stage" : "path_stage";
        var identifiers = new Dictionary<string, string> { [slot] = tables[slot] };
        byte[] buffer = ArrayPool<byte>.Shared.Rent(window);
        NpgsqlRawCopyStream? stream = null;
        Exception? failure = null;
        try
        {
            stream = await NpgsqlCatalog.BeginBinaryCopyAsync(connection, transaction, query, identifiers, ct);
            long total = checked((long)length);
            for (long offset = 0; offset < total;)
            {
                int count = (int)Math.Min(window, total - offset);
                Marshal.Copy(bytes + checked((nint)offset), buffer, 0, count);
                await stream.WriteAsync(buffer.AsMemory(0, count), ct);
                offset += count;
            }
        }
        catch (Exception primary)
        {
            failure = primary;
            if (stream is not null)
                try { await stream.CancelAsync(); }
                catch (Exception cleanup) { failure = new AggregateException(primary, cleanup); }
        }
        finally
        {
            if (stream is not null)
                try { await stream.DisposeAsync(); }
                catch (Exception cleanup)
                {
                    failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                }
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static unsafe void Begin(IntPtr stage, Hash128[] roots)
    {
        fixed (Hash128* input = roots) Check(Native.Begin(stage, input, (nuint)roots.Length));
    }

    private static unsafe Hash128[] Frontier(IntPtr stage)
    {
        IntPtr data = Native.Frontier(stage, out nuint count);
        if (count == 0) return [];
        if (data == IntPtr.Zero) throw new InvalidDataException("Missing Native frontier.");
        return new ReadOnlySpan<Hash128>((void*)data, checked((int)count)).ToArray();
    }

    private static unsafe void Answer(IntPtr stage, bool[] present)
    {
        byte[] bitmap = ArrayPool<byte>.Shared.Rent(present.Length);
        try
        {
            for (int i = 0; i < present.Length; i++) bitmap[i] = present[i] ? (byte)1 : (byte)0;
            fixed (byte* bytes = bitmap) Check(Native.Answer(stage, bytes, (nuint)present.Length));
        }
        finally { ArrayPool<byte>.Shared.Return(bitmap); }
    }

    private IntPtr Enter()
    {
        if (Interlocked.CompareExchange(ref _access, 1, 0) != 0)
            throw new InvalidOperationException("The Native window is disposed or already in use.");
        bool retained = false;
        try { _stage.DangerousAddRef(ref retained); return _stage.DangerousGetHandle(); }
        catch { Interlocked.CompareExchange(ref _access, 0, 1); throw; }
    }
    private void Leave() { _stage.DangerousRelease(); Interlocked.CompareExchange(ref _access, 0, 1); }
    public void Dispose() { Interlocked.Exchange(ref _access, 2); _stage.Dispose(); }
    private static void Check(int status)
    {
        if (status != 0) throw new InvalidDataException($"Native canonical working window refused operation ({status}).");
    }
    private sealed class StageHandle : SafeHandle
    {
        public StageHandle(IntPtr value) : base(IntPtr.Zero, true) => SetHandle(value);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() { Native.Free(handle); return true; }
    }

    private static partial class Native
    {
        [LibraryImport("laplace_core", EntryPoint = "text_identity_load_scalar_floor", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr Floor(string path);
        [LibraryImport("laplace_core", EntryPoint = "lp_highway_map", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr Highway(string path);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_new")]
        internal static partial IntPtr New(nuint nodes, nuint vertices, nuint bytes, IntPtr highway);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_free")]
        internal static partial void Free(IntPtr stage);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_reset")]
        internal static partial void Reset(IntPtr stage);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_text")]
        internal static unsafe partial int Text(IntPtr stage, byte* bytes, nuint count, StagedReference* result);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_compose")]
        internal static unsafe partial int Compose(IntPtr stage, StagedReference* children, nuint count, byte tier,
            ulong entityMask, ulong pathMask, int spine, StagedReference* result);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_reference")]
        internal static unsafe partial int Reference(IntPtr stage, StagedReference* recorded);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_begin")]
        internal static unsafe partial int Begin(IntPtr stage, Hash128* roots, nuint count);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_frontier")]
        internal static partial IntPtr Frontier(IntPtr stage, out nuint count);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_answer")]
        internal static unsafe partial int Answer(IntPtr stage, byte* present, nuint count);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_finish")]
        internal static partial int Finish(IntPtr stage);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_copy")]
        internal static partial IntPtr Copy(IntPtr stage, int table, out nuint bytes, out nuint rows);
        [LibraryImport("laplace_core", EntryPoint = "canonical_stage_columns")]
        internal static partial IntPtr Columns(int table);
    }
}
