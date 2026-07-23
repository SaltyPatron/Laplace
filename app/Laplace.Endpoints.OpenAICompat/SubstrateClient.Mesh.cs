using Laplace.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The semantic-mesh drill-down. A node's position in the factorization of
/// meaning — up to the hubs it plays for (senses, synsets, frames, classes,
/// rosets), down to its roster (members). Every read is scoped to one entity
/// through indexed relation partitions, so each is sub-second even under a seed.
/// </summary>
internal sealed partial class SubstrateClient
{
    // The mesh "up" arrows: how a node factors toward its hubs. Ordered from
    // finest (a word's senses) to coarsest (taxonomic parent). Scoped per entity.
    private static readonly string[] MeshUpRelations =
    [
        "IS_SENSE_OF", "HAS_SENSE", "EVOKES_FRAME", "MEMBER_OF_VERBNET_CLASS",
        "IS_INSTANCE_OF", "IS_A", "CORRESPONDS_TO", "HAS_SEMANTIC_ROLE",
    ];

    public async Task<MeshResponse?> MeshAsync(string idHex, CancellationToken ct)
    {
        if (!TryParseHex(idHex, out var id)) return null;

        var labelTask = ScalarAsync("SELECT laplace.label_or_hex(@id)", id, ct);
        var hubTypeTask = ScalarAsync("""
            SELECT laplace.label_or_hex(c.object_id)
            FROM laplace.consensus c
            WHERE c.subject_id = @id AND c.type_id = laplace.relation_type_id('IS_TYPED_AS')
            LIMIT 1
            """, id, ct);
        var belongsTask = BelongsToAsync(id, ct);
        var rosterTask = RosterAsync(id, ct);

        await Task.WhenAll(labelTask, hubTypeTask, belongsTask, rosterTask);

        var label = labelTask.Result ?? idHex;
        return new MeshResponse("mesh", idHex.ToLowerInvariant(), label,
            hubTypeTask.Result, belongsTask.Result, rosterTask.Result);
    }

    /// <summary>Up the ladder: the hubs this node plays for. A word's synsets
    /// come from senses(); every node's coarser hubs come from its outgoing
    /// mesh-up edges. Both scoped to the one entity.</summary>
    private async Task<IReadOnlyList<MeshLink>> BelongsToAsync(byte[] id, CancellationToken ct)
    {
        const string sql = """
            -- a bare surface/lemma's synsets: the teams it plays for. Gated to
            -- untyped nodes — for a synset, senses() roundtrips its own members,
            -- which belong in the roster (down), not belongs_to (up).
            SELECT encode(s.synset_id, 'hex'), laplace.label_or_hex(s.synset_id),
                   'has sense'::text AS relation,
                   'WordNet_Synset'::text AS hub_type, s.eff_mu, s.witnesses
            FROM laplace.senses(@id) s
            WHERE NOT EXISTS (
                SELECT 1 FROM laplace.consensus t
                WHERE t.subject_id = @id AND t.type_id = laplace.relation_type_id('IS_TYPED_AS'))
            UNION ALL
            -- coarser hubs the node connects up to (frames, classes, parents)
            SELECT encode(c.object_id, 'hex'), laplace.label_or_hex(c.object_id),
                   laplace.relation_canonical(c.type_id),
                   (SELECT laplace.label_or_hex(t.object_id)
                    FROM laplace.consensus t
                    WHERE t.subject_id = c.object_id
                      AND t.type_id = laplace.relation_type_id('IS_TYPED_AS') LIMIT 1),
                   laplace.eff_mu_display(c.rating, c.rd), c.witness_count
            FROM laplace.consensus c
            WHERE c.subject_id = @id
              AND c.type_id = ANY (SELECT laplace.relation_type_id(r) FROM unnest(@rels) r)
            ORDER BY eff_mu DESC NULLS LAST
            LIMIT 40
            """;
        return await ReadRowsAsync(sql, MeshRow, cmd =>
        {
            cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id;
            cmd.Parameters.Add("rels", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = MeshUpRelations;
        }, "mesh_belongs_to", ct);
    }

    /// <summary>Down the ladder: this hub's roster (its members).</summary>
    private Task<IReadOnlyList<MeshLink>> RosterAsync(byte[] id, CancellationToken ct) =>
        ReadRowsAsync("""
            SELECT encode(m.member, 'hex'), laplace.label_or_hex(m.member),
                   m.kind, NULL::text AS hub_type, m.mu, m.witnesses
            FROM laplace.concept_members(@id) m
            ORDER BY m.mu DESC NULLS LAST
            LIMIT 60
            """, MeshRow,
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "mesh_roster", ct);

    public async Task<TaxonomyResponse?> TaxonomyAsync(string idHex, CancellationToken ct)
    {
        if (!TryParseHex(idHex, out var id)) return null;

        // Taxonomy lives on concepts: a bare surface hops to its top synset,
        // a synset/frame stays itself (top_synset returns NULL for non-words).
        var root = await ReadRowsAsync("""
            SELECT encode(r.id, 'hex'), laplace.label_or_hex(r.id)
            FROM (SELECT COALESCE(laplace.top_synset(@id), @id) AS id) r
            """,
            static r => (Id: r.GetString(0), Label: r.IsDBNull(1) ? "" : r.GetString(1)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "taxonomy_root", ct);
        if (root.Count == 0) return null;
        var rootId = Convert.FromHexString(root[0].Id);

        var upTask = ReadRowsAsync("""
            SELECT encode(w.entity_id, 'hex'), laplace.label_or_hex(w.entity_id), w.eff_mu
            FROM laplace.walk_strongest(@id, laplace.relation_type_id('IS_A'), 10) w
            ORDER BY w.step
            """, TaxNode,
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = rootId,
            "taxonomy_up", ct, timeoutSeconds: 30);

        var childrenTask = ReadRowsAsync("""
            SELECT encode(c.subject_id, 'hex'), laplace.label_or_hex(c.subject_id),
                   laplace.eff_mu_display(c.rating, c.rd)
            FROM laplace.consensus c
            WHERE c.object_id = @id AND c.type_id = laplace.relation_type_id('IS_A')
            ORDER BY laplace.eff_mu_display(c.rating, c.rd) DESC
            LIMIT 24
            """, TaxNode,
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = rootId,
            "taxonomy_children", ct, timeoutSeconds: 30);

        await Task.WhenAll(upTask, childrenTask);
        return new TaxonomyResponse("taxonomy", root[0].Id, root[0].Label,
            upTask.Result, childrenTask.Result);
    }

    private static TaxonomyNode TaxNode(NpgsqlDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
        r.IsDBNull(2) ? null : r.GetDecimal(2));

    private static MeshLink MeshRow(NpgsqlDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetDecimal(4),
        r.IsDBNull(5) ? 0 : r.GetInt64(5));

    private async Task<string?> ScalarAsync(string sql, byte[] id, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id;
            var v = await cmd.ExecuteScalarAsync(ct);
            return v as string;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }
}
