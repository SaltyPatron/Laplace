using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record DeclaredPathStep(byte[] ClaimId, byte[] EntityId, string Relation, string Label,
        decimal EffMu, long Witnesses);
    public sealed record DeclaredPathResult(int RootIndex, int ProgramIndex, IReadOnlyList<DeclaredPathStep> Steps);
    /// <summary>Native owns the frontier, bounded set reads and complete path assembly.</summary>
    public static async Task<IReadOnlyList<DeclaredPathResult>> DeclaredPathsAsync(
        NpgsqlConnection connection, IReadOnlyList<byte[]> roots, NativeQueryProgram program,
        CancellationToken ct = default)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(connection, SqlCatalog.Get("query.declared_paths"),
            static r => (Root: r.GetInt32(0), Program: r.GetInt32(1), Path: r.GetInt64(2), Depth: r.GetInt32(3),
                Step: new DeclaredPathStep(r.GetFieldValue<byte[]>(4), r.GetFieldValue<byte[]>(5),
                    r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetInt64(9))),
            p =>
            {
                p.AddWithValue("roots", roots.ToArray());
                p.AddWithValue("program", global::NpgsqlTypes.NpgsqlDbType.Jsonb, program.ToJson());
                p.AddWithValue("capacity", 4096);
            }, ct: ct).ConfigureAwait(false);
        return rows.GroupBy(row => (row.Root,row.Program,row.Path)).Select(path =>
            new DeclaredPathResult(path.Key.Root,path.Key.Program,
                path.OrderBy(row => row.Depth).Select(row => row.Step).ToArray())).ToArray();
    }
}
