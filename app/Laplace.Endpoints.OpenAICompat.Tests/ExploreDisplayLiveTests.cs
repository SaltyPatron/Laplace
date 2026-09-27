using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Live: for a CILI concept with a witnessed HAS_DEFINITION, the entity preview and its
/// consensus-web neighborhood never display an entity id (or a long id prefix) as a
/// label, and relation types never display as bare hashes. Identity and realized label
/// stay separate when realization abstains. Needs the standing CILI estate; the
/// pg_regress fixture proves the display law without a seed.
/// </summary>
[Trait("Tier", "live")]
public sealed class ExploreDisplayLiveTests
{
    [SkippableFact]
    public async Task CiliConsensusWeb_KeepsIdentitySeparateFromHumanLabels()
    {
        Skip.IfNot(CanReachSeededSubstrate(), "seeded Postgres substrate not reachable");

        string? idHex;
        await using (var conn = new NpgsqlConnection(LaplaceInstall.PostgresConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                SELECT encode(a.subject_id, 'hex')
                FROM laplace.attestations a
                JOIN laplace.entities e ON e.id = a.subject_id
                WHERE a.type_id = laplace.relation_type_id('HAS_DEFINITION')
                  AND e.type_id = laplace.entity_type_id('CILI_Concept')
                ORDER BY a.subject_id
                LIMIT 1
                """, conn);
            idHex = await cmd.ExecuteScalarAsync() as string;
        }

        Skip.If(string.IsNullOrWhiteSpace(idHex), "standing substrate has no CILI definition witness");

        await using var client = new SubstrateClient();
        var preview = await client.ExploreEntityPreviewAsync(idHex!, CancellationToken.None);
        Assert.NotNull(preview);
        Assert.False(LeaksIdentity(preview!.IdHex, preview.Label),
            $"CILI preview leaked its id as display text: {preview.IdHex} -> {preview.Label}");
        Assert.False(string.Equals(preview.Label, "Unrealized entity", StringComparison.OrdinalIgnoreCase),
            "a CILI concept with a witnessed definition should have a readable definition/name display");

        // Two hops already route relation, reference and content nodes through the same
        // label projection the neighborhood response uses at any depth.
        var graph = await client.ExploreConsensusGraphAsync(
            idHex!, hops: 2, fanout: 8, maxNodes: 64, CancellationToken.None);
        Assert.NotNull(graph);
        Assert.NotEmpty(graph!.Nodes);

        Assert.All(graph.Nodes, node =>
            Assert.False(LeaksIdentity(node.IdHex, node.Label),
                $"graph node leaked its id as display text: {node.IdHex} -> {node.Label}"));

        Assert.All(graph.Edges, edge =>
        {
            Assert.False(LooksLikeBareHash(edge.Type),
                $"graph relation leaked an internal type id as display text: {edge.Type}");
        });
    }

    private static bool LeaksIdentity(string idHex, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var text = label.Trim();
        if (string.Equals(text, idHex, StringComparison.OrdinalIgnoreCase)) return true;

        // An ellipsized or prefix form of the id is the same leak. Sixteen hex digits
        // (64 bits) is the threshold, so a short real string of hex digits is not flagged.
        var prefix = text.TrimEnd('…');
        if (prefix.EndsWith("...", StringComparison.Ordinal)) prefix = prefix[..^3];
        return prefix.Length >= 16
            && prefix.All(char.IsAsciiHexDigit)
            && idHex.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBareHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimEnd('…');
        if (text.EndsWith("...", StringComparison.Ordinal)) text = text[..^3];
        return text.Length >= 16 && text.All(char.IsAsciiHexDigit);
    }

    private static bool CanReachSeededSubstrate()
    {
        try
        {
            using var conn = new NpgsqlConnection(LaplaceInstall.PostgresConnectionString());
            conn.Open();
            using var cmd = new NpgsqlCommand(
                """
                SELECT 1
                FROM laplace.attestations a
                JOIN laplace.entities e ON e.id = a.subject_id
                WHERE a.type_id = laplace.relation_type_id('HAS_DEFINITION')
                  AND e.type_id = laplace.entity_type_id('CILI_Concept')
                LIMIT 1
                """, conn);
            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }
}
