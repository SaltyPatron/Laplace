using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record DeclaredPathStep(byte[] ClaimId, byte[] EntityId, string Relation, string Label,
        decimal EffMu, long Witnesses);
    public sealed record DeclaredPathResult(int RootIndex, int ProgramIndex, IReadOnlyList<DeclaredPathStep> Steps);
    private sealed record QueryFrontier(int Root, int Program, byte[] Node, DeclaredPathStep[] Steps);

    /// <summary>One set query per program depth; Native owns IDs, path matching and realization.</summary>
    public static async Task<IReadOnlyList<DeclaredPathResult>> DeclaredPathsAsync(
        NpgsqlConnection connection, IReadOnlyList<byte[]> roots, NativeQueryProgram program,
        CancellationToken ct = default)
    {
        const int maximumFrontier = 4096;
        var frontier = roots.SelectMany((root, index) => program.Paths.Select((_, path) =>
            new QueryFrontier(index, path, root, []))).ToList();
        var complete = new List<DeclaredPathResult>();
        for (int depth = 0; frontier.Count > 0; depth++)
        {
            if (frontier.Count > maximumFrontier)
                throw new InvalidDataException("Declared query paths exceed the admitted frontier; narrow the requested context.");
            var rows = await NpgsqlRead.ReadRowsAsync(connection, SqlCatalog.Get("query.declared_paths_step"),
                static r => (Slot: r.GetInt32(0), Step: new DeclaredPathStep(
                    r.GetFieldValue<byte[]>(1), r.GetFieldValue<byte[]>(2), r.GetString(3), r.GetString(4),
                    r.GetDecimal(5), r.GetInt64(6))),
                p =>
                {
                    p.AddWithValue("slots", Enumerable.Range(0, frontier.Count).ToArray());
                    p.AddWithValue("nodes", frontier.Select(f => f.Node).ToArray());
                    p.AddWithValue("relations", frontier.Select(f => program.Paths[f.Program][depth].Entity).ToArray());
                    p.AddWithValue("maximum", maximumFrontier + 1);
                }, ct: ct).ConfigureAwait(false);
            if (rows.Count > maximumFrontier)
                throw new InvalidDataException("Declared query paths exceed the admitted frontier; no partial comparison was returned.");
            var next = new List<QueryFrontier>();
            foreach (var row in rows)
            {
                var previous = frontier[row.Slot];
                DeclaredPathStep[] steps = [.. previous.Steps, row.Step];
                if (depth + 1 == program.Paths[previous.Program].Count)
                    complete.Add(new(previous.Root, previous.Program, steps));
                else next.Add(new(previous.Root, previous.Program, row.Step.EntityId, steps));
            }
            frontier = next;
        }
        return complete;
    }
}
