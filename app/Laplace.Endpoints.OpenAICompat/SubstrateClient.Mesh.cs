using Laplace.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Mesh + taxonomy reads: thin callers over the installed mesh_position() /
/// taxonomy_tree(). The set logic (hub gating, top-synset rooting, ranking)
/// lives in the extension — one implementation shared with the MCP server;
/// C# only splits the dir-tagged rows into the response shape.
/// </summary>
internal sealed partial class SubstrateClient
{
    public async Task<MeshResponse?> MeshAsync(string idHex, CancellationToken ct)
    {
        if (!TryParseHex(idHex, out var id)) return null;

        var rows = await ReadRowsAsync("""
            SELECT dir, encode(id, 'hex'), label, relation, hub_type, eff_mu, witnesses
            FROM laplace.mesh_position(@id)
            """,
            static r => (
                Dir: r.GetString(0),
                Id: r.GetString(1),
                Label: r.IsDBNull(2) ? "" : r.GetString(2),
                Relation: r.IsDBNull(3) ? "" : r.GetString(3),
                HubType: r.IsDBNull(4) ? null : r.GetString(4),
                EffMu: r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                Witnesses: r.IsDBNull(6) ? 0L : r.GetInt64(6)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "mesh_position", ct);

        var self = rows.FirstOrDefault(r => r.Dir == "self");
        MeshLink Link((string Dir, string Id, string Label, string Relation, string? HubType, decimal? EffMu, long Witnesses) r) =>
            new(r.Id, r.Label, r.Relation, r.HubType, r.EffMu, r.Witnesses);

        return new MeshResponse("mesh", idHex.ToLowerInvariant(),
            self.Label is { Length: > 0 } ? self.Label : idHex,
            self.HubType,
            [.. rows.Where(r => r.Dir == "up").Select(Link)],
            [.. rows.Where(r => r.Dir == "down").Select(Link)]);
    }

    public async Task<TaxonomyResponse?> TaxonomyAsync(string idHex, CancellationToken ct)
    {
        if (!TryParseHex(idHex, out var id)) return null;

        var rows = await ReadRowsAsync("""
            SELECT dir, ord, encode(id, 'hex'), label, eff_mu
            FROM laplace.taxonomy_tree(@id)
            ORDER BY dir DESC, ord
            """,
            static r => (
                Dir: r.GetString(0),
                Id: r.GetString(2),
                Label: r.IsDBNull(3) ? "" : r.GetString(3),
                EffMu: r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "taxonomy_tree", ct, timeoutSeconds: 30);

        var self = rows.FirstOrDefault(r => r.Dir == "self");
        if (self.Id is null) return null;

        TaxonomyNode Node((string Dir, string Id, string Label, decimal? EffMu) r) =>
            new(r.Id, r.Label, r.EffMu);

        return new TaxonomyResponse("taxonomy", self.Id, self.Label,
            [.. rows.Where(r => r.Dir == "up").Select(Node)],
            [.. rows.Where(r => r.Dir == "child").Select(Node)]);
    }
}
