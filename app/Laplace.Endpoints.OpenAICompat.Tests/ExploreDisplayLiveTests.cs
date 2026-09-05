using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Product receipt for the failure that was visible in the CILI consensus-web screenshot:
/// a valid 128-bit entity id was being promoted into the human label when realization
/// abstained. Tier=live is intentional because the proof needs the standing CILI estate and
/// its real HAS_DEFINITION provenance; the seed-independent pg_regress fixture owns the
/// mechanical display-law proof.
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

        // Keep the live receipt bounded. The bug did not depend on a 1024-node crawl; it was
        // the post-election label projection. Two hops are enough to force mixed relation/
        // reference/content nodes through the same graph response used by the 8-hop UI.
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

        // The old UI also truncated a raw hash for sprites. Treat an ellipsized/prefix form
        // as the same identity leak while leaving an actual user string of a few hex digits
        // alone. Sixteen hex digits is already 64 bits of unmistakable internal identity.
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
