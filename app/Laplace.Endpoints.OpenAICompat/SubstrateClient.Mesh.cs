using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Mesh + taxonomy reads: thin callers over the installed structural.mesh_position() /
/// taxonomy.tree(). The set logic (hub gating, top-synset rooting, ranking)
/// lives in the extension — one implementation shared with the MCP server;
/// C# only splits the dir-tagged rows into the response shape. The SQL
/// itself lives in <see cref="NpgsqlSubstrateReads"/> (doc 41).
/// </summary>
internal sealed partial class SubstrateClient
{
    public async Task<MeshResponse?> MeshAsync(
        string idHex, int relationLimit, int memberLimit, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;

        var rows = await NpgsqlSubstrateReads.MeshPositionAsync(
            _dataSource, id, relationLimit, memberLimit, ct, TranslateSubstrateError);

        var self = rows.FirstOrDefault(r => r.Dir == "self");
        static MeshLink Link(NpgsqlSubstrateReads.MeshPositionRow r) =>
            new(r.IdHex, r.Label, r.Relation, r.HubType, r.EffMu, r.Witnesses);

        return new MeshResponse("mesh", idHex.ToLowerInvariant(),
            self.Label is { Length: > 0 } ? self.Label : idHex,
            self.HubType,
            [.. rows.Where(r => r.Dir == "up").Select(Link)],
            [.. rows.Where(r => r.Dir == "down").Select(Link)]);
    }

    public async Task<TaxonomyResponse?> TaxonomyAsync(
        string idHex, int depth, int childLimit, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;

        var rows = await NpgsqlSubstrateReads.TaxonomyTreeAsync(
            _dataSource, id, depth, childLimit, ct, TranslateSubstrateError);

        var self = rows.FirstOrDefault(r => r.Dir == "self");
        if (self.IdHex is null) return null;

        static TaxonomyNode Node(NpgsqlSubstrateReads.TaxonomyTreeRow r) => new(r.IdHex, r.Label, r.EffMu);

        return new TaxonomyResponse("taxonomy", self.IdHex, self.Label,
            [.. rows.Where(r => r.Dir == "up").Select(Node)],
            [.. rows.Where(r => r.Dir == "child").Select(Node)]);
    }
}
