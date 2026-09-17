using System.Globalization;
using Laplace.Decomposers.Abstractions.Tests;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class OpeningCatalogOrderingTests(LocalPgFixture pg)
{
    private sealed record Row(byte Position, byte Name, byte? Eco, long? Rank);

    [Fact]
    public async Task SelectedCatalogPairIsStableAcrossTiedEcoRowsAndInputOrder()
    {
        // Exercise the actual production projection/order in PostgreSQL over the
        // terminal relation it consumes. No game ingestion or fold is needed to
        // produce this ordering counterexample, and no persistent rows are written.
        string source = File.ReadAllText(Path.Combine(
            TypeIdLawTests.FindRepoRootPublic(), "app", "Laplace.Substrate",
            "Crud", "Npgsql", "NpgsqlSubstrateReads.cs"));
        var method = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "OpeningCatalogAsync");
        string query = method.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Select(node => node.Token.ValueText)
            .Single(value => value.Contains("WITH named AS MATERIALIZED", StringComparison.Ordinal));
        const string projection = "SELECT position_id, name_id, eco_id";
        int finalSelect = query.LastIndexOf(projection, StringComparison.Ordinal);
        Assert.True(finalSelect >= 0);
        string selectedQuery = query[finalSelect..];

        Row[] rows =
        [
            new(1, 1, 32, 100),
            new(1, 1, null, 100),
            new(1, 1, 16, 100),
            new(1, 2, 0, 100),  // Name priority precedes ECO.
            new(1, 0, 0, 50),   // Rank priority precedes name.
            new(1, 0, 48, null),
            new(2, 1, 0, 100),
            new(2, 2, 16, 200),
        ];
        string[] expected =
        [
            Key(1, 1, 16), Key(1, 1, 32), Key(1, 1, null),
            Key(1, 2, 0), Key(1, 0, 0), Key(1, 0, 48),
            Key(2, 2, 16), Key(2, 1, 0),
        ];
        foreach (var input in new[] { rows, rows.Reverse().ToArray() })
        {
            string values = string.Join(",\n", input.Select(row =>
                $"({SqlId(row.Position)}, {SqlId(row.Name)}, {SqlId(row.Eco)}, "
                + (row.Rank is { } rank ? rank.ToString(CultureInfo.InvariantCulture) : "NULL")
                + "::bigint)"));
            await using var command = pg.DataSource.CreateCommand(
                "WITH terminal(position_id,name_id,eco_id,rank) AS MATERIALIZED (VALUES "
                + values + ")\n" + selectedQuery);
            await using var reader = await command.ExecuteReaderAsync();
            var actual = new List<string>();
            while (await reader.ReadAsync())
                actual.Add(Convert.ToHexString((byte[])reader[0]) + "/"
                    + Convert.ToHexString((byte[])reader[1]) + "/"
                    + (reader.IsDBNull(2) ? "-" : Convert.ToHexString((byte[])reader[2])));
            Assert.Equal(expected, actual);

            // This is the first-wins lookup used by ChessOpeningIndex. Identical
            // rank/name must select the same non-null ECO regardless of scan order.
            Assert.Equal(Key(1, 1, 16), actual.First(value => value.StartsWith(Hex(1) + "/", StringComparison.Ordinal)));
            Assert.Equal(Key(2, 2, 16), actual.First(value => value.StartsWith(Hex(2) + "/", StringComparison.Ordinal)));
        }
    }

    private static string Hex(byte value) => value.ToString("X2", CultureInfo.InvariantCulture).PadLeft(32, '0');
    private static string SqlId(byte? value) => value is { } id
        ? $"decode('{Hex(id)}','hex')" : "NULL::bytea";
    private static string Key(byte position, byte name, byte? eco) =>
        Hex(position) + "/" + Hex(name) + "/" + (eco is { } id ? Hex(id) : "-");
}
