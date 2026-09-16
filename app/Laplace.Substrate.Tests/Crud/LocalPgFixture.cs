using System.Diagnostics;
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

    public const string DatabaseName = "laplace_substratecrud_test";

    private static readonly NpgsqlConnectionStringBuilder Conn =
        new(LaplaceInstall.PostgresConnectionString(DatabaseName));

    public static readonly string PgHost = Conn.Host!;
    public static readonly string PgUser = Conn.Username!;
    public static readonly string? PgPassword = Conn.Password;

    private NpgsqlDataSource? _ds;

    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");

    public string ConnectionString => Conn.ConnectionString;

    public async Task InitializeAsync()
    {
        await InitGate.WaitAsync();
        try
        {
            if (_ds is not null) return;
            if (_refCount == 0)
            {
                // New empty DB: forget any content-ladder skips from a prior fixture life.
                ContentLadderLedger.Reset();
                await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
                await RunPsqlAdminAsync("createdb", $"-h {PgHost} -U {PgUser} -O {PgUser} {DatabaseName}");
                // The database is shared across fixtures; its connection pool must
                // be shared too. Independent default-sized pools retained idle
                // sessions and exhausted the server during parallel CI tests.
                var candidate = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnectionString);
                try
                {
                    await using var conn = await candidate.OpenConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;
            SET search_path TO laplace, public;
        ";
                    await cmd.ExecuteNonQueryAsync();
                    await DeclareNativeFixtureFloorAsync(conn);
                    await DeclareNativeByteBasisAsync(candidate, conn);
                    _sharedDataSource = candidate;
                }
                catch
                {
                    await candidate.DisposeAsync();
                    throw;
                }
            }
            // Acquire ownership only after initialization has succeeded.
            _ds = _sharedDataSource ?? throw new InvalidOperationException("Shared fixture pool is unavailable");
            _refCount++;
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
                SELECT root_id, tier FROM converse.text_root_placements($1)
            )
            INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
            SELECT root_id,0,laplace.entity_type_id('Codepoint'),
                   realize.canonical_id('substrate/source/UnicodeDecomposer/v1')
            FROM basis WHERE tier=0
            ON CONFLICT DO NOTHING
            """;
        declare.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, symbols);
        await declare.ExecuteNonQueryAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            WITH basis AS MATERIALIZED (
                SELECT root_id, tier FROM converse.text_root_placements($1)
            )
            SELECT count(DISTINCT basis.root_id)
            FROM basis JOIN laplace.entities e ON e.id=basis.root_id
            WHERE basis.tier=0 AND e.tier=0
              AND e.type_id=laplace.entity_type_id('Codepoint')
              AND e.first_observed_by=realize.canonical_id('substrate/source/UnicodeDecomposer/v1')
            """;
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, symbols);
        if (await verify.ExecuteScalarAsync() is not long count || count != symbols.Length)
            throw new InvalidOperationException(
                "Fixture basis did not retain every declared actual native floor entity");
    }

    private static SubstrateChange NativeByteBasis()
    {
        // These are raw bytes 0x80..0xff, not their UTF-8 text encodings.
        // Use the same native basis and exact atomic Content body as UnicodeDecomposer.
        var source = UnicodeDecomposer.Source;
        var builder = new SubstrateChangeBuilder(source, "test-foundation/native-byte-basis/v1")
            .DeclareSourcePrior(SourceTrust.StandardsDerived)
            .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId, source);
        for (int index = 0; index < ByteAtoms.Count; index++)
        {
            byte value = checked((byte)(ByteAtoms.First + index));
            Hash128 id = ByteAtoms.Id(value);
            ReadOnlySpan<double> coordinate = ByteAtoms.Coord(value);
            builder.AddEntity(id, 0, ByteAtoms.TypeId, source);
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
            WHERE e.tier=0 AND e.type_id=$8 AND e.first_observed_by=$9
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
        verify.Parameters.AddWithValue(UnicodeDecomposer.Source.ToBytes());
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
                await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
            }
        }
        finally
        {
            InitGate.Release();
        }
    }

    private static async Task RunPsqlAdminAsync(string program, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePgTool(program),
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (PgPassword is not null) psi.Environment["PGPASSWORD"] = PgPassword;
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"{program} {args} exited {p.ExitCode}: {stderr}");
        }
    }

    private static string ResolvePgTool(string program)
    {
        if (!OperatingSystem.IsWindows()) return program;
        const string pgBin = @"C:\Program Files\PostgreSQL\18\bin";
        string exe = Path.Combine(pgBin, program + ".exe");
        return File.Exists(exe) ? exe : program;
    }
}
