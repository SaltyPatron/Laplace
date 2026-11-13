using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Unicode;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class LocalPgFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static int _refCount;
    private static NpgsqlDataSource? _sharedDataSource;
    private static bool _nativeFoundationReady;

    // The deployed installation's database (LAPLACE_DB, which delivery's site.sh exports): tests run against what
    // was built, installed and migrated, never a database of their own. Every test writes only content it composes
    // and reads only what it wrote; nothing here creates, drops or reinstalls a database or an extension.
    private static readonly NpgsqlConnectionStringBuilder Conn =
        new(Environment.GetEnvironmentVariable("LAPLACE_DB") is { Length: > 0 } deployed
            ? deployed
            : throw new InvalidOperationException(
                "LAPLACE_DB must name the deployed database: these tests run against the installed Laplace, after delivery"));

    public static readonly string DatabaseName = Conn.Database!;

    public static readonly string PgHost = Conn.Host!;
    public static readonly string PgUser = Conn.Username!;
    public static readonly string? PgPassword = Conn.Password;

    private NpgsqlDataSource? _ds;

    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");

    public string ConnectionString => Conn.ConnectionString;

    public Task InitializeAsync() => InitializeAsync(requireNativeFoundation: true);

    internal async Task InitializeAsync(bool requireNativeFoundation)
    {
        await InitGate.WaitAsync();
        try
        {
            bool retained = _ds is not null;
            if (retained && (!requireNativeFoundation || _nativeFoundationReady)) return;
            if (_refCount == 0)
            {
                _nativeFoundationReady = false;
                // One pool for every fixture of this test host: independent default-sized pools retained idle
                // sessions and exhausted the server during parallel tests.
                var candidate = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnectionString);
                try
                {
                    await using var conn = await candidate.OpenConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    // The deployed database carries the extensions delivery installed; a missing one is a failed
                    // deployment, reported here rather than installed by a test.
                    cmd.CommandText = """
                        SELECT string_agg(name, ', ') FROM unnest(ARRAY['postgis','laplace','laplace_geom','laplace_substrate']) name
                        WHERE NOT EXISTS (SELECT FROM pg_catalog.pg_extension e WHERE e.extname = name)
                        """;
                    if (await cmd.ExecuteScalarAsync() is string missing)
                        throw new InvalidOperationException($"The deployed database lacks extensions: {missing}");
                    _sharedDataSource = candidate;
                }
                catch
                {
                    await candidate.DisposeAsync();
                    throw;
                }
            }
            try
            {
                if (requireNativeFoundation && !_nativeFoundationReady)
                {
                    await using var conn = await _sharedDataSource!.OpenConnectionAsync();
                    await DeclareNativeFixtureFloorAsync(conn);
                    await DeclareNativeByteBasisAsync(_sharedDataSource, conn);
                    _nativeFoundationReady = true;
                }
            }
            catch (Exception initializationFailure)
            {
                // A maintenance-only owner may already be using this pool.
                // An unsuccessful foundation upgrade cannot release its lease.
                if (_refCount == 0 && _sharedDataSource is { } failed)
                {
                    _sharedDataSource = null;
                    try { await failed.DisposeAsync(); }
                    catch (Exception cleanupFailure)
                    { throw new AggregateException(initializationFailure, cleanupFailure); }
                }
                throw;
            }
            // Acquire ownership only after initialization has succeeded.
            if (!retained)
            {
                _ds = _sharedDataSource ?? throw new InvalidOperationException("Shared fixture pool is unavailable");
                _refCount++;
            }
        }
        finally
        {
            InitGate.Release();
        }
    }

    private static async Task DeclareNativeFixtureFloorAsync(NpgsqlConnection connection)
    {
        // Loading the native floor makes its atoms resolvable; it does not
        // persist their entity rows. This finite basis covers descriptor vocabulary,
        // the 15 copied authority artifacts, and the accented/Greek fixture words.
        // It does not mark the full Unicode layer complete or invent atom IDs.
        string[] symbols = Enumerable.Range(32, 95).Select(char.ConvertFromUtf32)
            .Concat("\t\n\r³×éñóúαζκλ—“”→".Select(c => c.ToString())).ToArray();
        await using var declare = connection.CreateCommand();
        declare.CommandText = """
            WITH basis AS MATERIALIZED (
                SELECT public.blake3_send(public.laplace_text_id(t)) AS root_id, public.laplace_tier(t) AS tier FROM unnest($1::text[]) t
            )
            INSERT INTO laplace.entities(id,tier,type_id)
            SELECT root_id,0,laplace.entity_type_id('Codepoint')
            FROM basis WHERE tier=0
            ON CONFLICT DO NOTHING
            """;
        declare.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, symbols);
        await declare.ExecuteNonQueryAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            WITH basis AS MATERIALIZED (
                SELECT public.blake3_send(public.laplace_text_id(t)) AS root_id, public.laplace_tier(t) AS tier FROM unnest($1::text[]) t
            )
            SELECT count(DISTINCT basis.root_id)
            FROM basis JOIN laplace.entities e ON e.id=basis.root_id
            WHERE basis.tier=0 AND e.tier=0
              AND e.type_id=laplace.entity_type_id('Codepoint')
            """;
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, symbols);
        if (await verify.ExecuteScalarAsync() is not long count || count != symbols.Length)
            throw new InvalidOperationException(
                "Fixture basis did not retain every declared actual native floor entity");
    }

    private static SubstrateChange NativeByteBasis()
    {
        // These are raw bytes 0x80..0xff, not their UTF-8 text encodings.
        // The fixture's raw-byte authority is independent of corpus parsing.
        var source = UnicodeSource.SourceId;
        var builder = new SubstrateChangeBuilder(source, "test-foundation/native-byte-basis/v1")
            .DeclareSourcePrior(SourceTrust.StandardsDerived)
            .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId);
        for (int index = 0; index < ByteAtoms.Count; index++)
        {
            byte value = checked((byte)(ByteAtoms.First + index));
            Hash128 id = ByteAtoms.Id(value);
            ReadOnlySpan<double> coordinate = ByteAtoms.Coord(value);
            builder.AddEntity(id, 0, ByteAtoms.TypeId);
            builder.AddPhysicality(new PhysicalityRow(
                PhysicalityId.Compute(id, PhysicalityType.Content), id, source, PhysicalityType.Content,
                coordinate[0], coordinate[1], coordinate[2], coordinate[3], ByteAtoms.Hilbert(value),
                null, 0, null, null, 0));
        }
        return builder.Build();
    }

    private static async Task DeclareNativeByteBasisAsync(
        NpgsqlDataSource dataSource, NpgsqlConnection connection)
    {
        SubstrateChange basis = NativeByteBasis();
        // The normal native stage/admission/COPY owner retains these actual forms.
        // Setup remains incomplete until the exact persisted bodies are read back.
        await new NpgsqlSubstrateWriter(dataSource).ApplyAsync(basis);
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT count(*)
            FROM unnest($1::bytea[],$2::bytea[],$3::double precision[],
                        $4::double precision[],$5::double precision[],
                        $6::double precision[],$7::bytea[]) AS expected(e,p,x,y,z,m,h)
            JOIN laplace.entities e ON e.id=expected.e
            JOIN laplace.physicalities p ON p.id=expected.p
            WHERE e.tier=0 AND e.type_id=$8
              AND p.entity_id=expected.e AND p.type=1
              AND float8send(public.ST_X(p.coord))=float8send(expected.x)
              AND float8send(public.ST_Y(p.coord))=float8send(expected.y)
              AND float8send(public.ST_Z(p.coord))=float8send(expected.z)
              AND float8send(public.ST_M(p.coord))=float8send(expected.m)
              AND p.hilbert_index=expected.h AND p.trajectory IS NULL
              AND p.n_constituents=0 AND p.alignment_residual IS NULL AND p.source_dim IS NULL
            """;
        var rows = basis.Physicalities;
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, rows.Select(p => p.EntityId.ToBytes()).ToArray());
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, rows.Select(p => p.Id.ToBytes()).ToArray());
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Double, rows.Select(p => p.CoordX).ToArray());
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Double, rows.Select(p => p.CoordY).ToArray());
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Double, rows.Select(p => p.CoordZ).ToArray());
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Double, rows.Select(p => p.CoordM).ToArray());
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, rows.Select(p => p.HilbertIndex.ToByteArray()).ToArray());
        verify.Parameters.AddWithValue(ByteAtoms.TypeId.ToBytes());
        verify.Parameters.AddWithValue(UnicodeSource.SourceId.ToBytes());
        if (await verify.ExecuteScalarAsync() is not long count || count != ByteAtoms.Count)
            throw new InvalidOperationException("Fixture byte basis did not retain every exact native entity and Content body");
    }

    public async Task DisposeAsync()
    {
        await InitGate.WaitAsync();
        try
        {
            if (_ds is null) return;
            _ds = null;
            if (--_refCount == 0)
            {
                await _sharedDataSource!.DisposeAsync();
                _sharedDataSource = null;
                _nativeFoundationReady = false;
            }
        }
        finally
        {
            InitGate.Release();
        }
    }
}
