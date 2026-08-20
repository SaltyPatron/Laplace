using System.Runtime.CompilerServices;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Typed callers over a handful of installed substrate functions that used to be
/// hand-written independently in each consumer (SubstrateClient.Mesh/.Pulse/.Matchup —
/// see doc 41, SQL standardization). The SQL text now lives here, the one sanctioned
/// home (<see cref="ReadPathArchitectureGateTests"/> in Laplace.Substrate.Tests); a
/// consumer names the function and gets rows back, never a string to maintain.
///
/// Every method takes an optional <see cref="NpgsqlRead.ErrorTranslator"/> so a caller
/// keeps its own exception vocabulary (SubstrateQueryException, SubstrateUnavailableException,
/// ...) without this assembly needing to know it exists — see <see cref="NpgsqlRead"/>'s
/// own remark on why translation is a delegate and not a fixed type here.
/// </summary>
public static class NpgsqlSubstrateReads
{
    internal static int RequestedLimit(int limit) => Math.Max(0, limit);

    public readonly record struct MeshPositionRow(
        string Dir, string IdHex, string Label, string Relation, string? HubType,
        decimal? EffMu, long Witnesses);

    /// <summary><c>structural.mesh_position(id)</c> — hub gating and ranking live in the extension.</summary>
    public static Task<IReadOnlyList<MeshPositionRow>> MeshPositionAsync(
        NpgsqlDataSource dataSource, byte[] id, int relationLimit, int memberLimit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT dir, encode(id, 'hex'), label, relation, hub_type, eff_mu, witnesses
            FROM structural.mesh_position(@id, @relation_limit, @member_limit)
            """,
            static r => new MeshPositionRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                r.IsDBNull(6) ? 0L : r.GetInt64(6)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("relation_limit", RequestedLimit(relationLimit));
                p.AddWithValue("member_limit", RequestedLimit(memberLimit));
            },
            ct: ct, label: "mesh_position", onError: onError);

    public readonly record struct TaxonomyTreeRow(
        string Dir, int Ord, string IdHex, string Label, decimal? EffMu);

    /// <summary><c>taxonomy.tree(id)</c> — the IS_A climb/descent around a topic.</summary>
    public static Task<IReadOnlyList<TaxonomyTreeRow>> TaxonomyTreeAsync(
        NpgsqlDataSource dataSource, byte[] id, int depth, int childLimit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT dir, ord, encode(id, 'hex'), label, eff_mu
            FROM taxonomy.tree(@id, @depth, @child_limit)
            ORDER BY dir DESC, ord
            """,
            static r => new TaxonomyTreeRow(
                r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("depth", RequestedLimit(depth));
                p.AddWithValue("child_limit", RequestedLimit(childLimit));
            },
            timeoutSeconds: 30, ct: ct, label: "taxonomy_tree", onError: onError);

    /// <summary><c>ops.modality_counts()</c> — corpus modality breakdown, one row.</summary>
    public static async Task<(long TextEvidence, long Chess, long Models, long Multilingual, long Documents)> ModalityCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT text_evidence, chess, models, multilingual, documents FROM ops.modality_counts()",
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)),
            timeoutSeconds: 20, ct: ct, label: "modality_counts", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0, 0, 0, 0, 0) : rows[0];
    }

    /// <summary><c>ops.substrate_pulse()</c> — the live scoreboard, one row.</summary>
    public static async Task<(long Entities, long Attestations, long Consensus, long Physicalities,
        long? LastFlushUnix, long FlushesLastMin, bool Folding)?> SubstratePulseAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT entities, attestations, consensus, physicalities,
                   extract(epoch FROM last_flush_at)::bigint, flushes_last_min, folding
            FROM ops.substrate_pulse()
            """,
            static r => (
                r.IsDBNull(0) ? 0L : r.GetInt64(0),
                r.IsDBNull(1) ? 0L : r.GetInt64(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2),
                r.IsDBNull(3) ? 0L : r.GetInt64(3),
                r.IsDBNull(4) ? (long?)null : r.GetInt64(4),
                r.IsDBNull(5) ? 0L : r.GetInt64(5),
                !r.IsDBNull(6) && r.GetBoolean(6)),
            ct: ct, label: "substrate_pulse", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>ops.entity_type_counts_approx()</c> — MCV×reltuples type census (GH #813).</summary>
    public static Task<IReadOnlyList<(string Type, long EntitiesApprox)>> EntityTypeCountsApproxAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT type, entities_approx
            FROM ops.entity_type_counts_approx()
            ORDER BY entities_approx DESC
            """,
            static r => (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? 0L : r.GetInt64(1)),
            ct: ct, label: "entity_type_counts_approx", onError: onError);

    /// <summary><c>ops.partition_pressure()</c> — partition skew via reltuples (GH #813).</summary>
    public static Task<IReadOnlyList<(string Parent, string Partition, decimal? Pct)>> PartitionPressureAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT parent, partition, pct_of_parent
            FROM ops.partition_pressure(NULL)
            ORDER BY pct_of_parent DESC NULLS LAST
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2)),
            ct: ct, label: "partition_pressure", onError: onError);

    /// <summary><c>laplace.atom_census()</c> — tier-0 window invariant (GH #813).</summary>
    public static async Task<(long Tier0, long Window, long Over, long Unresolvable)?> AtomCensusAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT tier0_count, atom_window, over_window, unresolvable_ids
            FROM laplace.atom_census()
            """,
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)),
            ct: ct, label: "atom_census", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>ops.source_tier_census(source)</c> — entities by tier for a lane (GH #813).</summary>
    public static Task<IReadOnlyList<(short Tier, long Entities)>> SourceTierCensusAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT tier, entities
            FROM ops.source_tier_census(@source)
            ORDER BY tier
            """,
            static r => (r.GetInt16(0), r.GetInt64(1)),
            p => p.AddWithValue("source", sourceId),
            ct: ct, label: "source_tier_census", onError: onError);

    /// <summary><c>ops.surface_sample(source, tier, limit)</c> — ranked surfaces (GH #813).</summary>
    public static Task<IReadOnlyList<(string Surface, string TypeName, long Observations)>> SurfaceSampleAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, short tier, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT surface, type_name, observations
            FROM ops.surface_sample(@source, @tier, @limit)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p =>
            {
                p.AddWithValue("source", sourceId);
                p.AddWithValue("tier", tier);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "surface_sample", onError: onError);

    /// <summary><c>ops.arena_counts()</c> — per-relation consensus mass (GH #764 callers).</summary>
    public static Task<IReadOnlyList<(string Type, long Relations, long Witnesses)>> ArenaCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT type, relations, witnesses
            FROM ops.arena_counts()
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0L : r.GetInt64(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            ct: ct, label: "arena_counts", onError: onError);

    /// <summary><c>ops.ingest_runs(limit)</c> — recent ingest journal rows.</summary>
    public static Task<IReadOnlyList<(string Source, string Status, DateTimeOffset Started)>> IngestRunsAsync(
        NpgsqlDataSource dataSource, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT source_name, status, started_at
            FROM ops.ingest_runs(@limit)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.GetFieldValue<DateTimeOffset>(2)),
            p => p.AddWithValue("limit", limit),
            ct: ct, label: "ingest_runs", onError: onError);

    /// <summary><c>ops.consensus_count(type)</c> — consensus row count, optional type filter.</summary>
    public static async Task<long> ConsensusCountAsync(
        NpgsqlDataSource dataSource, byte[]? typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT ops.consensus_count(@type)
            """,
            static r => r.IsDBNull(0) ? 0L : r.GetInt64(0),
            p => p.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)typeId ?? DBNull.Value }),
            ct: ct, label: "consensus_count", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <summary><c>laplace.compositional_tier_distribution()</c></summary>
    public static Task<IReadOnlyList<(short Tier, long N)>> CompositionalTierDistributionAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT tier, n FROM laplace.compositional_tier_distribution() ORDER BY tier
            """,
            static r => (r.GetInt16(0), r.GetInt64(1)),
            ct: ct, label: "compositional_tier_distribution", onError: onError);

    /// <summary><c>ops.consensus_tier_distribution()</c></summary>
    public static Task<IReadOnlyList<(short Tier, long Relations)>> ConsensusTierDistributionAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT subject_tier, relations FROM ops.consensus_tier_distribution() ORDER BY subject_tier
            """,
            static r => (r.GetInt16(0), r.GetInt64(1)),
            ct: ct, label: "consensus_tier_distribution", onError: onError);

    /// <summary><c>realize.render_gaps(limit)</c> — ids that consensus references but cannot render.</summary>
    public static Task<IReadOnlyList<(string IdHex, string Roles, long Refs)>> RenderGapsAsync(
        NpgsqlDataSource dataSource, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(id, 'hex'), roles, refs
            FROM realize.render_gaps(@limit)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p => p.AddWithValue("limit", limit),
            ct: ct, label: "render_gaps", onError: onError);

    /// <summary><c>ops.entity_type_counts()</c> — exact (slow); prefer EntityTypeCountsApproxAsync.</summary>
    public static Task<IReadOnlyList<(string Type, short Tier, long Entities)>> EntityTypeCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT type, tier, entities FROM ops.entity_type_counts()
            """,
            static r => (r.IsDBNull(0) ? "" : r.GetString(0), r.GetInt16(1), r.GetInt64(2)),
            ct: ct, label: "entity_type_counts", onError: onError);

    /// <summary><c>laplace.is_compositional_type(type_id)</c></summary>
    public static async Task<bool> IsCompositionalTypeAsync(
        NpgsqlDataSource dataSource, byte[] typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT laplace.is_compositional_type(@type)
            """,
            static r => !r.IsDBNull(0) && r.GetBoolean(0),
            p => p.AddWithValue("type", typeId),
            ct: ct, label: "is_compositional_type", onError: onError).ConfigureAwait(false);
        return rows.Count > 0 && rows[0];
    }

    /// <summary><c>ops.top_relations_readable(limit, type)</c></summary>
    public static Task<IReadOnlyList<(string Subject, string Type, string Object, decimal EffMu, long Witnesses)>> TopRelationsReadableAsync(
        NpgsqlDataSource dataSource, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT subject, type, object, eff_mu, witnesses
            FROM ops.top_relations_readable(@limit, NULL)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? 0m : r.GetDecimal(3),
                r.IsDBNull(4) ? 0L : r.GetInt64(4)),
            p => p.AddWithValue("limit", limit),
            ct: ct, label: "top_relations_readable", onError: onError);

    /// <summary><c>consensus.relation_rank(type_id)</c> — highway rank weight.</summary>
    public static async Task<double> RelationRankAsync(
        NpgsqlDataSource dataSource, byte[] typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT consensus.relation_rank(@type)
            """,
            static r => r.IsDBNull(0) ? 0d : r.GetDouble(0),
            p => p.AddWithValue("type", typeId),
            ct: ct, label: "relation_rank", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0d : rows[0];
    }

    /// <summary><c>consensus.effective_mu(rating, rd)</c> — integer μ−2·RD.</summary>
    public static async Task<long> EffectiveMuAsync(
        NpgsqlDataSource dataSource, long rating, long rd, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT consensus.effective_mu(@rating, @rd)
            """,
            static r => r.IsDBNull(0) ? 0L : r.GetInt64(0),
            p =>
            {
                p.AddWithValue("rating", rating);
                p.AddWithValue("rd", rd);
            }, ct: ct, label: "effective_mu", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <summary><c>consensus.relation_type_resolve(surface)</c> — name/alias → type id.</summary>
    public static async Task<byte[]?> RelationTypeResolveAsync(
        NpgsqlDataSource dataSource, string surface, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT consensus.relation_type_resolve(@surface)
            """,
            static r => r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0),
            p => p.AddWithValue("surface", surface),
            ct: ct, label: "relation_type_resolve", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>realize.register_canonical(name)</c></summary>
    public static async Task<byte[]?> RegisterCanonicalAsync(
        NpgsqlDataSource dataSource, string name, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT realize.register_canonical(@name)
            """,
            static r => r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0),
            p => p.AddWithValue("name", name),
            ct: ct, label: "register_canonical", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>laplace.intent_preflight(entity_ids, phys_ids, att_ids)</c></summary>
    public static async Task<byte[]?> IntentPreflightEntityBitmapAsync(
        NpgsqlDataSource dataSource, byte[][] entityIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT (laplace.intent_preflight(@entities, ARRAY[]::bytea[], ARRAY[]::bytea[])).entity_exists
            """,
            static r => r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0),
            p => p.AddWithValue("entities", entityIds),
            ct: ct, label: "intent_preflight", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>consensus.relation_family_type_ids(family)</c> / <c>relation_band_types(band)</c>.</summary>
    public static async Task<byte[][]> RelationFamilyTypeIdsAsync(
        NpgsqlDataSource dataSource, string family, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT consensus.relation_family_type_ids(@family)
            """,
            static r => r.IsDBNull(0) ? Array.Empty<byte[]>() : r.GetFieldValue<byte[][]>(0),
            p => p.AddWithValue("family", family),
            ct: ct, label: "relation_family_type_ids", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? [] : rows[0];
    }

    /// <summary><c>ops.attestation_response_type(...)</c> — typed attestation frontier.</summary>
    public static Task<IReadOnlyList<(string ObjectHex, double CombinedEffMu, int SourceCount)>> AttestationResponseTypeAsync(
        NpgsqlDataSource dataSource, byte[] subjectId, byte[] relationTypeId, int topK, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(object_id, 'hex'), combined_eff_mu, source_count
            FROM ops.attestation_response_type(@subject, @rel, NULL, NULL, @k)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0d : r.GetDouble(1),
                r.IsDBNull(2) ? 0 : r.GetInt32(2)),
            p =>
            {
                p.AddWithValue("subject", subjectId);
                p.AddWithValue("rel", relationTypeId);
                p.AddWithValue("k", topK);
            }, ct: ct, label: "attestation_response_type", onError: onError);

    /// <summary><c>ops.attestation_unary_response_type(...)</c></summary>
    public static Task<IReadOnlyList<(double CombinedEffMu, int SourceCount)>> AttestationUnaryResponseTypeAsync(
        NpgsqlDataSource dataSource, byte[] subjectId, byte[] relationTypeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT combined_eff_mu, source_count
            FROM ops.attestation_unary_response_type(@subject, @rel, NULL, NULL)
            """,
            static r => (
                r.IsDBNull(0) ? 0d : r.GetDouble(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1)),
            p =>
            {
                p.AddWithValue("subject", subjectId);
                p.AddWithValue("rel", relationTypeId);
            }, ct: ct, label: "attestation_unary_response_type", onError: onError);

    /// <summary><c>laplace.laplace_score(v, m)</c> / <c>laplace_score_inverse(score, m)</c>.</summary>
    public static async Task<(long Score, double Inverse)> LaplaceScorePairAsync(
        NpgsqlDataSource dataSource, double v, double m, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT s.score, laplace.laplace_score_inverse(s.score, @m)
            FROM (SELECT laplace.laplace_score(@v, @m) AS score) s
            """,
            static r => (r.IsDBNull(0) ? 0L : r.GetInt64(0), r.IsDBNull(1) ? 0d : r.GetDouble(1)),
            p =>
            {
                p.AddWithValue("v", v);
                p.AddWithValue("m", m);
            }, ct: ct, label: "laplace_score", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0L, 0d) : rows[0];
    }

    /// <summary><c>structural.entities_at_depth(tier)</c>.</summary>
    public static async Task<long> EntitiesAtDepthCountAsync(
        NpgsqlDataSource dataSource, short depth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT count(*)::bigint FROM structural.entities_at_depth(@d)
            """,
            static r => r.IsDBNull(0) ? 0L : r.GetInt64(0),
            p => p.AddWithValue("d", depth),
            ct: ct, label: "laplace_entities_at_depth", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <summary><c>ops.entity_attestations(subject)</c>.</summary>
    public static Task<IReadOnlyList<(string TypeHex, string ObjectHex, long EffMuRaw)>> EntityAttestationsAsync(
        NpgsqlDataSource dataSource, byte[] subjectId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(type_id, 'hex'), encode(object_id, 'hex'), eff_mu_raw
            FROM ops.entity_attestations(@subject, 0)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p => p.AddWithValue("subject", subjectId),
            ct: ct, label: "laplace_entity_attestations", onError: onError);

    /// <summary><c>taxonomy.ancestry(entity, band_mask)</c>.</summary>
    public static Task<IReadOnlyList<(string AncestorHex, int Depth)>> AncestryAsync(
        NpgsqlDataSource dataSource, byte[] entityId, byte[] bandMask, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(ancestor_id, 'hex'), depth
            FROM taxonomy.ancestry(@entity, @band, 4)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1)),
            p =>
            {
                p.AddWithValue("entity", entityId);
                p.AddWithValue("band", bandMask);
            }, ct: ct, label: "laplace_ancestry", onError: onError);

    /// <summary><c>lexical.translations(entity, band_mask)</c>.</summary>
    public static Task<IReadOnlyList<(string TranslationHex, string SharedObjectHex)>> TranslationsAsync(
        NpgsqlDataSource dataSource, byte[] entityId, byte[] bandMask, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(translation_id, 'hex'), encode(shared_object_id, 'hex')
            FROM lexical.translations(@entity, @band)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1)),
            p =>
            {
                p.AddWithValue("entity", entityId);
                p.AddWithValue("band", bandMask);
            }, ct: ct, label: "laplace_translations", onError: onError);

    /// <summary><c>generation.model_jitter_catalog(relation)</c>.</summary>
    public static Task<IReadOnlyList<(string SubjectHex, string TypeHex, long WitnessCount)>> ModelJitterCatalogAsync(
        NpgsqlDataSource dataSource, string? relation, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(subject_id, 'hex'), encode(type_id, 'hex'), witness_count
            FROM generation.model_jitter_catalog(@rel, 150000000000)
            """,
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p => p.Add(new NpgsqlParameter("rel", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relation ?? DBNull.Value }),
            ct: ct, label: "model_jitter_catalog", onError: onError);

    /// <summary><c>realize.vertex_tier(flags)</c> / <c>realize.vertex_atom(flags)</c>.</summary>
    public static async Task<(short Tier, int Atom)> VertexDecodeAsync(
        NpgsqlDataSource dataSource, long flags, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT realize.vertex_tier(@flags), realize.vertex_atom(@flags)
            """,
            static r => (r.GetInt16(0), r.GetInt32(1)),
            p => p.AddWithValue("flags", flags),
            ct: ct, label: "vertex_tier", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? ((short)0, 0) : rows[0];
    }

    public readonly record struct BandLeaderRow(
        int Band, string SubjectIdHex, string Subject, string Relation,
        string ObjectIdHex, string Object, decimal EffMu, long Witnesses);

    /// <summary><c>ops.band_leaders(bands, per_band)</c> — top edges per salience band.</summary>
    public static Task<IReadOnlyList<BandLeaderRow>> BandLeadersAsync(
        NpgsqlDataSource dataSource, int[] bands, int perBand, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT band, encode(subject_id, 'hex'), subject, relation,
                   encode(object_id, 'hex'), object, eff_mu, witnesses
            FROM ops.band_leaders(@bands, @per)
            """,
            static r => new BandLeaderRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5), r.GetDecimal(6), r.GetInt64(7)),
            p =>
            {
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                p.AddWithValue("per", perBand);
            }, ct: ct, label: "band_leaders", onError: onError);

    /// <summary><c>ops.entity_record(id)</c> — confirmed/contested/refuted/thin edge counts.</summary>
    public static async Task<(long Confirmed, long Contested, long Refuted, long Thin)> EntityRecordAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT confirmed, contested, refuted, thin FROM ops.entity_record(@id)",
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_record", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0, 0, 0, 0) : rows[0];
    }

    public readonly record struct SalientFactRow(string Type, string Fact, decimal EffMu, long Witnesses);

    /// <summary>
    /// <c>consensus.salient_facts(id, relation_type, limit)</c> — typed relations ranked by
    /// eff_mu. Shared by SubstrateClient.Matchup, the CLI's neighbors command and the MCP
    /// facts tool — the exact 9-function cluster doc 33/41 name as the highest-duplication
    /// read surface.
    /// </summary>
    public static Task<IReadOnlyList<SalientFactRow>> SalientFactsAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT f.type, f.fact, f.eff_mu, f.witnesses
            FROM consensus.salient_facts(@id, NULL, @limit) f
            """,
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "salient_facts", onError: onError);

    public readonly record struct TapeRow(string Holder, string Type, string Fact, decimal? Mu);

    /// <summary><c>converse.contrast(x, y, relation_type, limit)</c> — the head-to-head tape.</summary>
    public static Task<IReadOnlyList<TapeRow>> ContrastAsync(
        NpgsqlDataSource dataSource, byte[] x, byte[] y, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT c.holder, c.type, c.fact, c.mu
            FROM converse.contrast(@x, @y, NULL, @limit) c
            """,
            static r => new TapeRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDecimal(3)),
            p =>
            {
                p.Add("x", NpgsqlDbType.Bytea).Value = x;
                p.Add("y", NpgsqlDbType.Bytea).Value = y;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "contrast", onError: onError);

    public readonly record struct RelationSummaryRow(
        string? Relation, string? Plane, decimal? Mu, long? Usage, double? Geodesic, string? Verdict);

    /// <summary>
    /// <c>consensus.relation_summary(x, y)</c> — the slow path/verdict half of a matchup.
    /// Measured 6-14s under an active seed; give it a generous timeout rather than the
    /// default serving budget.
    /// </summary>
    public static async Task<RelationSummaryRow?> RelationSummaryAsync(
        NpgsqlDataSource dataSource, byte[] x, byte[] y, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT s.relation, s.plane, s.mu, s.usage, s.geodesic, s.verdict
            FROM consensus.relation_summary(@x, @y) s
            """,
            static r => new RelationSummaryRow(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetString(5)),
            p =>
            {
                p.Add("x", NpgsqlDbType.Bytea).Value = x;
                p.Add("y", NpgsqlDbType.Bytea).Value = y;
            }, timeoutSeconds: 120, ct: ct, label: "relation_summary", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct SourceRosterRow(
        string SubjectIdHex, string Subject, string Relation,
        string ObjectIdHex, string Object, long Observations);

    /// <summary><c>ops.source_roster(source_id, limit)</c> — a bounded sample of what a source witnessed.</summary>
    public static Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(subject_id, 'hex'), subject, relation,
                   encode(object_id, 'hex'), object, observations
            FROM ops.source_roster(@sid, @lim)
            """,
            static r => new SourceRosterRow(
                r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2),
                r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.GetInt64(5)),
            p =>
            {
                p.Add("sid", NpgsqlDbType.Bytea).Value = sourceId;
                p.AddWithValue("lim", limit);
            }, timeoutSeconds: 30, ct: ct, label: "source_roster", onError: onError);

    // --- Catalog / inventory (Cluster 2: substrate_counts, consensus_stats*, source_counts*) ---
    // Connection overloads exist because Audit/Explore open one connection and run several
    // of these in sequence (TEMP scopes, statement-budget fallbacks).

    public readonly record struct MetricCountRow(string Metric, long Value);

    /// <summary><c>ops.substrate_counts()</c> — inventory metrics (planner-labeled).</summary>
    public static Task<IReadOnlyList<MetricCountRow>> SubstrateCountsAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT metric, value FROM ops.substrate_counts()",
            static r => new MetricCountRow(r.GetString(0), r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "substrate_counts", onError: onError);

    /// <inheritdoc cref="SubstrateCountsAsync(NpgsqlConnection, CancellationToken, int, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<MetricCountRow>> SubstrateCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT metric, value FROM ops.substrate_counts()",
            static r => new MetricCountRow(r.GetString(0), r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "substrate_counts", onError: onError);

    public readonly record struct ConsensusStatsRow(
        long EvidenceRows, long ConsensusRows, decimal? DedupRatio,
        decimal? AvgWitnesses, long? MaxWitnesses);

    private static ConsensusStatsRow MapConsensusStats(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1),
        r.IsDBNull(2) ? null : r.GetDecimal(2),
        r.IsDBNull(3) ? null : r.GetDecimal(3),
        r.IsDBNull(4) ? null : r.GetInt64(4));

    private const string ConsensusStatsSelect =
        "SELECT evidence_rows, consensus_rows, dedup_ratio, avg_witnesses, max_witnesses FROM ";

    /// <summary><c>consensus.stats()</c> — exact full aggregates (minutes at scale).</summary>
    public static async Task<ConsensusStatsRow?> ConsensusStatsExactAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            ConsensusStatsSelect + "consensus.stats()",
            MapConsensusStats, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "consensus_stats", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>consensus.stats_approx()</c> — planner estimates; avg/max may be NULL.</summary>
    public static async Task<ConsensusStatsRow?> ConsensusStatsApproxAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            ConsensusStatsSelect + "consensus.stats_approx()",
            MapConsensusStats, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "consensus_stats_approx", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct SourceCountRow(
        string Source, long Evidence, long? Content, string? IdHex);

    /// <summary><c>ops.source_counts()</c> — exact per-source evidence+content (unbounded).</summary>
    public static Task<IReadOnlyList<SourceCountRow>> SourceCountsAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT source, evidence, content, encode(source_id, 'hex') FROM ops.source_counts()",
            static r => new SourceCountRow(
                r.GetString(0), r.GetInt64(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "source_counts", onError: onError);

    /// <summary><c>ops.source_counts_approx()</c> — partition-stats evidence; content unknown.</summary>
    public static Task<IReadOnlyList<SourceCountRow>> SourceCountsApproxAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT source, evidence_approx, encode(source_id, 'hex') FROM ops.source_counts_approx()",
            static r => new SourceCountRow(
                r.GetString(0), r.GetInt64(1), null,
                r.IsDBNull(2) ? null : r.GetString(2)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "source_counts_approx", onError: onError);

    /// <summary><c>ops.multi_source_entity_count()</c> — subjects with ≥2 sources.</summary>
    public static Task<long?> MultiSourceEntityCountAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<long?>(conn,
            "SELECT ops.multi_source_entity_count()",
            timeoutSeconds: timeoutSeconds, ct: ct, label: "multi_source_entity_count", onError: onError);

    /// <summary>
    /// Connection-scoped <c>consensus.salient_facts</c> — same SQL as the datasource overload;
    /// Explore keeps one open connection across entity facets.
    /// </summary>
    public static Task<IReadOnlyList<SalientFactRow>> SalientFactsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT f.type, f.fact, f.eff_mu, f.witnesses
            FROM consensus.salient_facts(@id, NULL, @limit) f
            """,
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "salient_facts", onError: onError);

    public readonly record struct EntityPlacementRow(
        short Type, double X, double Y, double Z, double M, double Radius, int Constituents);

    private static EntityPlacementRow MapPlacement(NpgsqlDataReader r) => new(
        r.GetInt16(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
        r.GetDouble(4), r.GetDouble(5), r.GetInt32(6));

    private const string EntityPhysicalitiesSelect =
        "SELECT p.type, p.x, p.y, p.z, p.m, p.radius, p.n_constituents FROM ops.entity_physicalities(@id) p";

    /// <summary>
    /// <c>ops.entity_physicalities(id)</c> — every form for one entity, ordered by type.
    /// The shared <c>entity_form</c> reader Cluster 5 of doc 41 consolidates onto.
    /// </summary>
    public static Task<IReadOnlyList<EntityPlacementRow>> EntityPhysicalitiesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, EntityPhysicalitiesSelect + " ORDER BY p.type",
            MapPlacement,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_physicalities", onError: onError);

    /// <inheritdoc cref="EntityPhysicalitiesAsync(NpgsqlConnection, byte[], CancellationToken, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<EntityPlacementRow>> EntityPhysicalitiesAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, EntityPhysicalitiesSelect + " ORDER BY p.type",
            MapPlacement,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_physicalities", onError: onError);

    /// <summary>Lowest-type form only — the embedding / visualization anchor.</summary>
    public static async Task<EntityPlacementRow?> EntityPrimaryFormAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            EntityPhysicalitiesSelect + " ORDER BY p.type LIMIT 1",
            MapPlacement,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_primary_form", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct OrdinalPlacementRow(
        long Ordinal, double X, double Y, double Z, double M, double Radius, int Constituents);

    /// <summary>
    /// First form per id for a batch — one round-trip via <c>unnest … LATERAL</c>.
    /// Ordinals are 1-based (Postgres WITH ORDINALITY).
    /// </summary>
    public static Task<IReadOnlyList<OrdinalPlacementRow>> EntityPrimaryFormsBatchAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT u.ord, f.x, f.y, f.z, f.m, f.radius, f.n_constituents
            FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord)
            JOIN LATERAL (
                SELECT x, y, z, m, radius, n_constituents
                FROM ops.entity_physicalities(u.id)
                ORDER BY type
                LIMIT 1
            ) f ON true
            """,
            static r => new OrdinalPlacementRow(
                r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
                r.GetDouble(4), r.GetDouble(5), r.GetInt32(6)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "entity_primary_forms_batch", onError: onError);

    /// <summary><c>ops.evidence_count(NULL, NULL, id)</c> — attestation rows for a subject.</summary>
    public static Task<long?> EvidenceCountAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<long?>(conn,
            "SELECT ops.evidence_count(NULL, NULL, @id)",
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "evidence_count", onError: onError);

    public readonly record struct ConsensusOutLabeledRow(
        string TypeLabel, string ObjectLabel, string ObjectIdHex, decimal EffMu, long Witnesses);

    /// <summary><c>ops.consensus_out_labeled(id, limit)</c>.</summary>
    public static Task<IReadOnlyList<ConsensusOutLabeledRow>> ConsensusOutLabeledAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT c.type_label, c.object_label, encode(c.object_id, 'hex'),
                   c.eff_mu, c.witnesses
            FROM ops.consensus_out_labeled(@id, @limit) c
            """,
            static r => new ConsensusOutLabeledRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "consensus_out_labeled", onError: onError);

    public readonly record struct EvidenceReceiptRow(
        string TypeIdHex, string TypeLabel, string ObjectIdHex, string ObjectLabel,
        string? SourceLabels, long WitnessCount, decimal EffMu);

    /// <summary><c>ops.evidence_receipt(id, limit)</c>.</summary>
    public static Task<IReadOnlyList<EvidenceReceiptRow>> EvidenceReceiptAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(e.type_id, 'hex'), e.type_label,
                   encode(e.object_id, 'hex'), e.object_label,
                   e.source_labels, e.witness_count, e.eff_mu
            FROM ops.evidence_receipt(@id, @limit) e
            """,
            static r => new EvidenceReceiptRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetDecimal(6)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "evidence_receipt", onError: onError);

    /// <summary>
    /// Eval ingest-fidelity positives — synonym-grounded pairs scored on a plane relation.
    /// Vocab via installed <c>subjects_of_type</c>; score cells via <c>consensus_id</c> PK
    /// (same shape as <see cref="NpgsqlConsensusCell"/>).
    /// </summary>
    public static Task<IReadOnlyList<double>> IngestFidelityPositiveScoresAsync(
        NpgsqlDataSource dataSource, string relation, string groundTruth, int n,
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT score FROM ops.eval_ingest_fidelity_positives(@rel, @gt, @n)",
            static r => r.GetDouble(0),
            p =>
            {
                p.AddWithValue("rel", relation);
                p.AddWithValue("gt", groundTruth);
                p.AddWithValue("n", n);
            }, ct: ct, label: "eval_ingest_fidelity_pos", onError: onError);

    /// <summary>Eval ingest-fidelity negatives — random half-vocab pairs on the same plane.</summary>
    public static Task<IReadOnlyList<double>> IngestFidelityNegativeScoresAsync(
        NpgsqlDataSource dataSource, string relation, string groundTruth, int n,
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT score FROM ops.eval_ingest_fidelity_negatives(@rel, @n)",
            static r => r.GetDouble(0),
            p =>
            {
                p.AddWithValue("rel", relation);
                p.AddWithValue("n", n);
            }, ct: ct, label: "eval_ingest_fidelity_neg", onError: onError);

    public readonly record struct ConverseReplyRow(string Reply, decimal? EffMu, long? Witnesses);

    private const string RecallSessionSelect = """
        SELECT reply, eff_mu, witnesses
        FROM converse.recall_session(@p, @session)
        """;

    private static void BindRecallSession(NpgsqlParameterCollection p, string prompt, byte[]? session)
    {
        p.AddWithValue("p", prompt);
        var sessionParam = p.Add("session", NpgsqlDbType.Bytea);
        sessionParam.Value = session is null ? DBNull.Value : session;
    }

    private static ConverseReplyRow MapConverseReply(NpgsqlDataReader r) => new(
        r.IsDBNull(0) ? "" : r.GetString(0),
        r.IsDBNull(1) ? null : r.GetDecimal(1),
        r.IsDBNull(2) ? null : r.GetInt64(2));

    /// <summary><c>converse.recall_session(prompt, session)</c> — session-resident converse.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlConnection conn, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, RecallSessionSelect, MapConverseReply,
            p => BindRecallSession(p, prompt, session),
            ct: ct, label: "recall_session", onError: onError);

    /// <inheritdoc cref="RecallSessionAsync(NpgsqlConnection, string, byte[]?, CancellationToken, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlDataSource dataSource, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, RecallSessionSelect, MapConverseReply,
            p => BindRecallSession(p, prompt, session),
            ct: ct, label: "recall_session", onError: onError);

    /// <summary><c>converse.recall_intent(shape, topic, topic2, type, lang, ctx)</c>.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallIntentAsync(
        NpgsqlDataSource dataSource, string shape, byte[] topic, byte[]? topic2,
        string? relationType, string? lang, byte[][]? contextIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT reply, eff_mu, witnesses
            FROM converse.recall_intent(@shape, @topic, @topic2, @type, @lang, @ctx)
            """,
            MapConverseReply,
            p =>
            {
                p.AddWithValue("shape", shape);
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add("topic2", NpgsqlDbType.Bytea).Value = (object?)topic2 ?? DBNull.Value;
                p.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relationType ?? DBNull.Value });
                p.Add(new NpgsqlParameter("lang", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)lang ?? DBNull.Value });
                p.Add("ctx", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value =
                    (object?)contextIds ?? DBNull.Value;
            }, ct: ct, label: "recall_intent", onError: onError);

    /// <summary><c>converse.recall(prompt)</c> — intent-routed converse without a session.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallAsync(
        NpgsqlConnection conn, string prompt, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT reply, eff_mu, witnesses FROM converse.recall(@p)",
            MapConverseReply,
            p => p.AddWithValue("p", prompt),
            ct: ct, label: "recall", onError: onError);

    /// <summary><c>consensus.gaps(converse.resolve_last_word(prompt))</c>.</summary>
    public static Task<IReadOnlyList<string>> GapsForPromptAsync(
        NpgsqlConnection conn, string prompt, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT missing_arena FROM consensus.gaps(converse.resolve_last_word(@p))",
            static r => r.IsDBNull(0) ? "" : r.GetString(0),
            p => p.AddWithValue("p", prompt),
            ct: ct, label: "gaps", onError: onError);

    /// <summary><c>converse.first_placed_topic(word)</c>.</summary>
    public static Task<byte[]?> FirstPlacedTopicAsync(
        NpgsqlConnection conn, string word, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(conn,
            "SELECT converse.first_placed_topic(@w)",
            p => p.AddWithValue("w", word),
            ct: ct, label: "first_placed_topic", onError: onError);

    public readonly record struct Neighbor4dRow(string Neighbor, double Geodesic, double? Frechet);

    /// <summary><c>structural.nearest_neighbors_4d(word, k)</c>.</summary>
    public static Task<IReadOnlyList<Neighbor4dRow>> NearestNeighbors4dAsync(
        NpgsqlConnection conn, string word, int k, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT neighbor, geodesic, frechet FROM structural.nearest_neighbors_4d(@w, @k)",
            static r => new Neighbor4dRow(
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0 : r.GetDouble(1),
                r.IsDBNull(2) ? null : r.GetDouble(2)),
            p =>
            {
                p.AddWithValue("w", word);
                p.AddWithValue("k", k);
            }, ct: ct, label: "nearest_neighbors_4d", onError: onError);

    public readonly record struct ConsensusOutReadableRow(
        long Ordinal, string Type, string? Object, decimal EffMu, long Witnesses);

    /// <summary>
    /// Batch <c>ops.consensus_out_readable(id, limit)</c> over an id array, keyed by ordinal.
    /// </summary>
    public static Task<IReadOnlyList<ConsensusOutReadableRow>> ConsensusOutReadableBatchAsync(
        NpgsqlConnection conn, byte[][] ids, int perId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT u.ord, r.type, r.object, r.eff_mu, r.witnesses
            FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord)
            CROSS JOIN LATERAL ops.consensus_out_readable(u.id, @lim)
                WITH ORDINALITY AS r(type, object, eff_mu, witnesses, rord)
            ORDER BY u.ord, r.rord
            """,
            static r => new ConsensusOutReadableRow(
                r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.AddWithValue("lim", perId);
            }, ct: ct, label: "consensus_out_readable_batch", onError: onError);

    public readonly record struct ConsensusEdgeRenderedRow(
        string TypeLabel, string? PeerLabel, long Rating, long Rd, long Volatility, long WitnessCount);

    /// <summary><c>consensus.consensus_out(id)</c> with rendered type/object labels.</summary>
    public static Task<IReadOnlyList<ConsensusEdgeRenderedRow>> ConsensusOutRenderedAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT realize.render(c.type_id), realize.render(c.object_id),
                   c.rating, c.rd, c.volatility, c.witness_count
            FROM consensus.consensus_out(@id) c
            """,
            static r => new ConsensusEdgeRenderedRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "consensus_out_rendered", onError: onError);

    /// <summary><c>consensus.consensus_in(id)</c> with rendered subject/type labels.</summary>
    public static Task<IReadOnlyList<ConsensusEdgeRenderedRow>> ConsensusInRenderedAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT realize.render(c.type_id), realize.render(c.subject_id),
                   c.rating, c.rd, c.volatility, c.witness_count
            FROM consensus.consensus_in(@id) c
            """,
            static r => new ConsensusEdgeRenderedRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "consensus_in_rendered", onError: onError);

    public readonly record struct AttestationRenderedRow(
        string TypeLabel, string? PeerLabel, string SourceLabel, byte[]? ContextId,
        short Outcome, long ObservationCount);

    /// <summary><c>ops.attestations_out(id)</c> with rendered labels.</summary>
    public static Task<IReadOnlyList<AttestationRenderedRow>> AttestationsOutRenderedAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT realize.render(a.type_id), realize.render(a.object_id),
                   realize.render(a.source_id), a.context_id, a.outcome, a.observation_count
            FROM ops.attestations_out(@id) a
            """,
            static r => new AttestationRenderedRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : (byte[])r[3], r.GetInt16(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "attestations_out_rendered", onError: onError);

    /// <summary><c>ops.attestations_in(id)</c> with rendered labels (peer = subject).</summary>
    public static Task<IReadOnlyList<AttestationRenderedRow>> AttestationsInRenderedAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT realize.render(a.type_id), realize.render(a.subject_id),
                   realize.render(a.source_id), a.context_id, a.outcome, a.observation_count
            FROM ops.attestations_in(@id) a
            """,
            static r => new AttestationRenderedRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : (byte[])r[3], r.GetInt16(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "attestations_in_rendered", onError: onError);

    /// <summary><c>converse.resolve_ref(text)</c> — word, concept, or hex → entity id.</summary>
    public static Task<byte[]?> ResolveRefAsync(
        NpgsqlDataSource dataSource, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource,
            "SELECT converse.resolve_ref(@ref)",
            p => p.AddWithValue("ref", reference),
            ct: ct, label: "resolve_ref", onError: onError);

    /// <summary><c>laplace.word_id(surface)</c> — surface spelling → word entity id.</summary>
    public static Task<byte[]?> WordIdAsync(
        NpgsqlDataSource dataSource, string surface, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource,
            "SELECT laplace.word_id(@w)",
            p => p.AddWithValue("w", surface),
            ct: ct, label: "word_id", onError: onError);

    /// <summary>
    /// <c>converse.chat(prompt, session, …)</c> — the conversational entry point.
    /// Optional shape/bands/elaborate match the MCP/HTTP dials; omit for the CLI two-arg form.
    /// </summary>
    public static Task<string?> ChatAsync(
        NpgsqlConnection conn, string prompt, byte[]? session, CancellationToken ct,
        string? shape = null, int[]? bands = null, bool elaborate = false,
        byte[]? language = null,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(conn, """
            SELECT converse.chat(@p, @s, @lang, @shape, @bands, NULL, NULL, NULL, @elab)
            """,
            p =>
            {
                p.AddWithValue("p", prompt);
                var sessionParam = p.Add("s", NpgsqlDbType.Bytea);
                sessionParam.Value = session is null ? DBNull.Value : session;
                p.Add(new NpgsqlParameter("lang", NpgsqlDbType.Bytea)
                    { Value = (object?)language ?? DBNull.Value });
                p.Add(new NpgsqlParameter("shape", NpgsqlDbType.Text)
                    { Value = (object?)shape ?? DBNull.Value });
                p.Add(new NpgsqlParameter("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                    { Value = (object?)bands ?? DBNull.Value });
                p.AddWithValue("elab", elaborate);
            }, ct: ct, label: "chat", onError: onError);

    /// <inheritdoc cref="ChatAsync(NpgsqlConnection, string, byte[]?, CancellationToken, string?, int[]?, bool, byte[]?, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<string?> ChatAsync(
        NpgsqlDataSource dataSource, string prompt, byte[]? session, CancellationToken ct,
        string? shape = null, int[]? bands = null, bool elaborate = false,
        byte[]? language = null,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(dataSource, """
            SELECT converse.chat(@p, @s, @lang, @shape, @bands, NULL, NULL, NULL, @elab)
            """,
            p =>
            {
                p.AddWithValue("p", prompt);
                var sessionParam = p.Add("s", NpgsqlDbType.Bytea);
                sessionParam.Value = session is null ? DBNull.Value : session;
                p.Add(new NpgsqlParameter("lang", NpgsqlDbType.Bytea)
                    { Value = (object?)language ?? DBNull.Value });
                p.Add(new NpgsqlParameter("shape", NpgsqlDbType.Text)
                    { Value = (object?)shape ?? DBNull.Value });
                p.Add(new NpgsqlParameter("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                    { Value = (object?)bands ?? DBNull.Value });
                p.AddWithValue("elab", elaborate);
            }, ct: ct, label: "chat", onError: onError);

    public readonly record struct ExploreResolveRow(byte[] Id, string Label, string RefKind, bool Exists);

    /// <summary>Resolves a concept, word, or hexadecimal entity reference for the explorer.</summary>
    public static async Task<ExploreResolveRow?> ExploreResolveAsync(
        NpgsqlConnection conn, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, """
            WITH resolved AS (
                SELECT CASE
                    WHEN @ref ~ '^[0-9a-f]{32}$' THEN decode(@ref, 'hex')
                    ELSE COALESCE(lexical.concept_ref(@ref), laplace.word_id(@ref))
                END AS id,
                CASE
                    WHEN @ref ~ '^[0-9a-f]{32}$' THEN 'hex'
                    WHEN lexical.concept_ref(@ref) IS NOT NULL THEN 'concept'
                    WHEN laplace.word_id(@ref) IS NOT NULL THEN 'word'
                    ELSE 'not_found'
                END AS ref_kind
            )
            SELECT r.id, converse.label_or_hex(r.id), r.ref_kind,
                   consensus.entity_exists(r.id) AS exists
            FROM resolved r
            WHERE r.id IS NOT NULL
            """,
            static r => new ExploreResolveRow(
                r.GetFieldValue<byte[]>(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            p => p.AddWithValue("ref", reference.Trim()),
            ct: ct, label: "explore_resolve", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct AnchorNeighborRow(
        string Axis, string IdHex, string? Label, short? Tier, double? Geodesic, double? Frechet);

    /// <summary>Nearest stored entities to a computed explorer anchor.</summary>
    public static Task<IReadOnlyList<AnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        NpgsqlConnection conn, double cx, double cy, double cz, double cm, string? trajectoryWkt,
        int geodesicK, int frechetK, double frechetMax, int timeoutSeconds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT n.axis, encode(n.entity_id, 'hex'), n.label, n.tier, n.geodesic, n.frechet
            FROM structural.explore_anchor_neighbors(
                @cx, @cy, @cz, @cm, @traj, @gk, @fk, @fmax) n
            """,
            static r => new AnchorNeighborRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt16(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5)),
            p =>
            {
                p.AddWithValue("cx", cx);
                p.AddWithValue("cy", cy);
                p.AddWithValue("cz", cz);
                p.AddWithValue("cm", cm);
                p.Add(new NpgsqlParameter("traj", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)trajectoryWkt ?? DBNull.Value });
                p.AddWithValue("gk", geodesicK);
                p.AddWithValue("fk", frechetK);
                p.AddWithValue("fmax", frechetMax);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_anchor_neighbors", onError: onError);

    public readonly record struct WitnessedWordRow(string Surface, string IdHex, long Witnesses);

    /// <summary>Returns candidate word surfaces that resolve to witnessed entities.</summary>
    public static Task<IReadOnlyList<WitnessedWordRow>> WitnessedWordsAsync(
        NpgsqlConnection conn, string[] surfaces, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            WITH c AS (
                SELECT s, laplace.word_id(s) AS id
                FROM unnest(@surfaces) AS s
            )
            SELECT c.s, encode(c.id, 'hex'),
                   ops.evidence_count(NULL, NULL, c.id)
            FROM c
            WHERE consensus.entity_exists(c.id)
            """,
            static r => new WitnessedWordRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p =>
            {
                var parameter = p.AddWithValue("surfaces", surfaces);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            }, ct: ct, label: "witnessed_words", onError: onError);

    public readonly record struct StructuralNeighborRow(
        string? IdHex, string? Label, double Geodesic, double? Frechet,
        double? X, double? Y, double? Z, double? M, double? Radius);

    /// <summary>Structural neighbours for one stored entity.</summary>
    public static Task<IReadOnlyList<StructuralNeighborRow>> StructuralNeighborsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(n.neighbor_id, 'hex'), n.neighbor,
                   n.geodesic, n.frechet, n.x, n.y, n.z, n.m, n.radius
            FROM structural.neighbors_of(@id, @k) n
            """,
            static r => new StructuralNeighborRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("k", limit);
            }, ct: ct, label: "structural_neighbors", onError: onError);

    public readonly record struct ConceptMemberRow(
        string IdHex, string Kind, string Label, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<ConceptMemberRow>> ConceptMembersAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(m.member, 'hex'), m.kind,
                   COALESCE(NULLIF(realize.render_text_fast(m.member, 8), ''),
                            converse.label_or_hex(m.member)), m.mu, m.witnesses
            FROM lexical.concept_members(@id) m
            ORDER BY m.mu DESC NULLS LAST, m.member
            LIMIT @limit
            """,
            static r => new ConceptMemberRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "concept_members", onError: onError);

    public readonly record struct ConceptPeerRow(string Peer, string Kind, double Strength);

    public static Task<IReadOnlyList<ConceptPeerRow>> ConceptPeersAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            "SELECT peer, kind, strength FROM lexical.concept_peers(@id, @limit)",
            static r => new ConceptPeerRow(r.GetString(0), r.GetString(1), r.GetDouble(2)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "concept_peers", onError: onError);

    public readonly record struct ContainerRow(string IdHex, short Tier, string Type, int Hops, string Label);

    public static Task<IReadOnlyList<ContainerRow>> ContainersAsync(
        NpgsqlConnection conn, byte[] id, int hops, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(c.entity_id, 'hex'), c.tier,
                   COALESCE(NULLIF(realize.render_text_fast(c.type_id, 4), ''),
                            converse.label_or_hex(c.type_id)), c.hops,
                   COALESCE(NULLIF(realize.render_text_fast(c.entity_id, 8), ''),
                            converse.label_or_hex(c.entity_id))
            FROM structural.containers_of(@id, @hops, @limit) c
            """,
            static r => new ContainerRow(
                r.GetString(0), r.GetInt16(1), r.GetString(2), r.GetInt32(3), r.GetString(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("hops", hops);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "containers", onError: onError);

    public readonly record struct CompletionRow(
        string ObjectIdHex, string TypeIdHex, decimal EffectiveMu, long Witnesses, string ObjectLabel);

    /// <summary>Ranked continuation candidates for a resolved prompt.</summary>
    public static Task<IReadOnlyList<CompletionRow>> CompletionsAsync(
        NpgsqlConnection conn, string prompt, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT
                encode(c.object_id, 'hex') AS object_id_hex,
                encode(c.type_id, 'hex') AS type_id_hex,
                c.eff_mu,
                c.witnesses,
                converse.label_or_hex(c.object_id) AS object_label
            FROM consensus.completions(converse.resolve(@prompt), @limit) c
            ORDER BY c.eff_mu DESC
            """,
            static r => new CompletionRow(
                r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3), r.GetString(4)),
            p =>
            {
                p.AddWithValue("prompt", prompt);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "completions", onError: onError);

    public readonly record struct WalkTextStepRow(int Step, string Entity, int StrideUsed);

    /// <summary>
    /// <c>generation.walk_text(...)</c> — streams as it reads, so it stays outside
    /// NpgsqlRead's buffer-then-return shape; the caller's IAsyncEnumerable pass-through
    /// is the whole point of this endpoint.
    /// </summary>
    public static async IAsyncEnumerable<WalkTextStepRow> WalkTextAsync(
        NpgsqlDataSource dataSource, string prompt, int steps, int maxOrder, double temperature, int topK,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT step, entity, stride_used FROM generation.walk_text(@p, @steps, @order, @temp, @topk);", conn);
        cmd.Parameters.AddWithValue("p", prompt);
        cmd.Parameters.AddWithValue("steps", steps);
        cmd.Parameters.AddWithValue("order", maxOrder);
        cmd.Parameters.AddWithValue("temp", temperature);
        cmd.Parameters.AddWithValue("topk", topK);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new WalkTextStepRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
        }
    }

    public readonly record struct OrdinalCountRow(long Ordinal, long? Count);

    /// <summary>Batch <c>ops.evidence_count(NULL, NULL, id)</c> over an array, keyed by ordinal.</summary>
    public static Task<IReadOnlyList<OrdinalCountRow>> EvidenceCountsBatchAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT u.ord, ops.evidence_count(NULL, NULL, u.id)
            FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord)
            """,
            static r => new OrdinalCountRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "evidence_counts_batch", onError: onError);

    public readonly record struct WalkBranchStepRow(
        int Depth, string[] PathHex, string[] TypePathHex, string EntityIdHex, string EntityLabel,
        decimal EffMu, decimal PathMu, long Witnesses);

    /// <summary><c>consensus.walk_branches(converse.resolve(prompt), NULL, depth, beam)</c> — the explain trace.</summary>
    public static Task<IReadOnlyList<WalkBranchStepRow>> ExplainTraceStepsAsync(
        NpgsqlConnection conn, string prompt, int depth, int beam, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT
                gt.depth,
                ARRAY(SELECT encode(x, 'hex') FROM unnest(gt.path) AS u(x)) AS path_hex,
                ARRAY(SELECT encode(x, 'hex') FROM unnest(gt.types) AS u(x)) AS type_path_hex,
                encode(gt.entity_id, 'hex') AS entity_id_hex,
                converse.label_or_hex(gt.entity_id) AS entity_label,
                gt.eff_mu,
                gt.path_mu,
                gt.witnesses
            FROM consensus.walk_branches(converse.resolve(@prompt), NULL, @depth, @beam) gt
            ORDER BY gt.depth, gt.path_mu DESC
            """,
            static r => new WalkBranchStepRow(
                r.GetInt32(0), r.GetFieldValue<string[]>(1), r.GetFieldValue<string[]>(2),
                r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetInt64(7)),
            p =>
            {
                p.AddWithValue("prompt", prompt);
                p.AddWithValue("depth", depth);
                p.AddWithValue("beam", beam);
            }, ct: ct, label: "explain_trace_steps", onError: onError);

    public readonly record struct OrdinalAttestationRow(
        long Ordinal, string TypeIdHex, string ObjectIdHex, string SourceIdHex, string? ContextIdHex,
        short Outcome, long ObservationCount);

    /// <summary>Batch <c>ops.attestations_out(id, perId)</c> over an array, keyed by ordinal.</summary>
    public static Task<IReadOnlyList<OrdinalAttestationRow>> AttestationsOutBatchAsync(
        NpgsqlConnection conn, byte[][] ids, int perId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT u.ord,
                encode(a.type_id, 'hex'),
                encode(a.object_id, 'hex'),
                encode(a.source_id, 'hex'),
                CASE WHEN a.context_id IS NULL THEN NULL ELSE encode(a.context_id, 'hex') END,
                a.outcome,
                a.observation_count
            FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord)
            CROSS JOIN LATERAL ops.attestations_out(u.id, @per_id)
                WITH ORDINALITY AS a(type_id, object_id, source_id, context_id, outcome, observation_count, aord)
            ORDER BY u.ord, a.aord
            """,
            static r => new OrdinalAttestationRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt16(5), r.GetInt64(6)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.AddWithValue("per_id", perId);
            }, ct: ct, label: "attestations_out_batch", onError: onError);

    public readonly record struct TargetEvidenceRow(
        byte[]? EntityId, string? EntityLabel, string? TypeIdHex, string? TypeLabel,
        string? ObjectIdHex, string? ObjectLabel, string? SourceLabels, long? WitnessCount, decimal? EffMu);

    /// <summary>
    /// Resolve a free-form target, then its evidence receipts — one round trip. A
    /// resolvable-but-unwitnessed target returns exactly one anchor row with every
    /// evidence column NULL (the LEFT JOIN LATERAL), which the caller filters out.
    /// </summary>
    public static Task<IReadOnlyList<TargetEvidenceRow>> EvidenceForTargetAsync(
        NpgsqlConnection conn, string target, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            WITH resolved AS (
                SELECT converse.resolve_ref(@target) AS id
            )
            SELECT
                r.id,
                CASE
                    WHEN @target ~ '^[0-9a-f]{32}$' THEN COALESCE(
                        NULLIF(realize.render_text_fast(r.id, 8), ''),
                        left(encode(r.id, 'hex'), 16))
                    ELSE @target
                END,
                encode(e.type_id, 'hex'),
                e.type_label,
                encode(e.object_id, 'hex'),
                e.object_label,
                e.source_labels,
                e.witness_count,
                e.eff_mu
            FROM resolved r
            LEFT JOIN LATERAL ops.evidence_receipt(r.id, @limit) e ON true
            WHERE r.id IS NOT NULL
            """,
            static r => new TargetEvidenceRow(
                r.IsDBNull(0) ? null : (byte[])r[0],
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8)),
            p =>
            {
                p.AddWithValue("target", target);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "evidence_for_target", onError: onError);

    /// <summary>
    /// Readiness probe via <c>ops.substrate_counts()</c> — no raw table EXISTS.
    /// Estimates can lag; a freshly empty DB still reports zero.
    /// </summary>
    public static async Task<(bool EntitiesExist, bool ConsensusExist)> EntitiesAndConsensusExistAsync(
        NpgsqlConnection conn, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await SubstrateCountsAsync(conn, ct, timeoutSeconds: 30, onError: onError)
            .ConfigureAwait(false);
        bool entities = false, consensus = false;
        foreach (var r in rows)
        {
            if (r.Metric.Equals("entities(ESTIMATE)", StringComparison.Ordinal))
                entities = r.Value > 0;
            else if (r.Metric.Equals("consensus(ESTIMATE)", StringComparison.Ordinal))
                consensus = r.Value > 0;
        }
        return (entities, consensus);
    }

    /// <summary>
    /// <c>laplace.word_id('the')</c> as a T0 perfcache liveness probe. No error
    /// translation: the caller catches the specific ObjectNotInPrerequisiteState
    /// PostgresException to mean "not loaded yet", which is not a failure at this layer.
    /// </summary>
    public static Task<object?> PerfCacheProbeAsync(NpgsqlConnection conn, CancellationToken ct) =>
        NpgsqlRead.ExecuteScalarAsync<object>(conn,
            "SELECT laplace.word_id('the');", ct: ct, label: "perfcache_probe");

    public readonly record struct EmbeddingLookupRow(
        int Kind, long Ord, byte[]? EntityId,
        double? X, double? Y, double? Z, double? M, double? Radius, int? Constituents,
        string? Relation, string? ObjectLabel, decimal? EffMu, long? Witnesses);

    /// <summary>
    /// One round trip: the resolve CTE feeds both the physical form (kind=0 anchor row)
    /// and the meaning neighbors (kind=1 rows, gated by <paramref name="includeMeaning"/>).
    /// </summary>
    public static Task<IReadOnlyList<EmbeddingLookupRow>> EmbeddingLookupAsync(
        NpgsqlConnection conn, string target, int meaningLimit, bool includeMeaning, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            WITH resolved AS (
                SELECT converse.resolve_ref(@target) AS id
            )
            SELECT 0 AS kind, 0::bigint AS ord, r.id AS eid,
                   f.x, f.y, f.z, f.m, f.radius, f.n_constituents,
                   NULL::text, NULL::text, NULL::numeric, NULL::bigint
            FROM resolved r
            LEFT JOIN LATERAL (
                SELECT x, y, z, m, radius, n_constituents
                FROM ops.entity_physicalities(r.id)
                ORDER BY type
                LIMIT 1
            ) f ON true
            UNION ALL
            SELECT 1 AS kind, m.ord, r.id,
                   NULL::float8, NULL::float8, NULL::float8, NULL::float8, NULL::float8, NULL::int,
                   m.type, m.object, m.eff_mu, m.witnesses
            FROM resolved r
            CROSS JOIN LATERAL ops.consensus_out_readable(r.id, @limit)
                WITH ORDINALITY AS m(type, object, eff_mu, witnesses, ord)
            WHERE @include
            ORDER BY kind, ord
            """,
            static r => new EmbeddingLookupRow(
                r.GetInt32(0), r.GetInt64(1), r.IsDBNull(2) ? null : (byte[])r[2],
                r.IsDBNull(3) ? null : r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetDecimal(11),
                r.IsDBNull(12) ? null : r.GetInt64(12)),
            p =>
            {
                p.AddWithValue("target", target);
                p.AddWithValue("limit", meaningLimit);
                p.AddWithValue("include", includeMeaning);
            }, ct: ct, label: "embedding_lookup", onError: onError);

    public readonly record struct TopRelationEdgeRow(
        string SubjectIdHex, string Subject, string TypeIdHex, string Type,
        string ObjectIdHex, string Object, decimal EffMu, long Witnesses);

    /// <summary>
    /// The exact top-k edges by salience band x eff_mu. This is the LABELING layer
    /// over <c>consensus.top_relations</c> and nothing else.
    ///
    /// It used to hand-roll the ranking instead, on the stated grounds that
    /// top_relations ran "full-table consensus.edge_rank() measured &gt;9 minutes live". That
    /// defect was fixed extension-side (Issue 52: exact indexed edge rank via
    /// consensus_edge_rank_btree) and the copy here was
    /// never retired — so the API kept serving the superseded shape, with a scalar
    /// label_or_hex per row on top of it. On 2026-08-06 that query held AccessShareLock
    /// for 2h08m, queued an ALTER EXTENSION behind it, and wedged the whole read
    /// surface. Rank in the installed core, label ONCE after the limit — the same
    /// rank-then-label rule consensus.edges() follows.
    /// </summary>
    public static Task<IReadOnlyList<TopRelationEdgeRow>> TopRelationsAsync(
        NpgsqlConnection conn, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            WITH t AS MATERIALIZED (
                SELECT r.subject_id, r.type_id, r.object_id, r.eff_mu, r.witnesses,
                       row_number() OVER () AS ord
                FROM consensus.top_relations(@limit) r
            ),
            ids AS MATERIALIZED (
                SELECT array_agg(v.id ORDER BY t.ord, v.slot) AS a
                FROM t
                CROSS JOIN LATERAL (VALUES (t.subject_id, 1), (t.object_id, 2)) AS v(id, slot)
            ),
            rel AS MATERIALIZED (
                SELECT d.type_id,
                       COALESCE(NULLIF(consensus.relation_canonical(d.type_id), ''),
                                converse.label_or_hex(d.type_id)) AS relation
                FROM (SELECT DISTINCT t.type_id FROM t) d
            ),
            lbl AS MATERIALIZED (
                SELECT realize.batch(ids.a, NULL) AS l FROM ids
            )
            SELECT
                encode(t.subject_id, 'hex') AS subject_id_hex,
                COALESCE(lbl.l[(t.ord - 1) * 2 + 1], converse.label_or_hex(t.subject_id)) AS subject_label,
                encode(t.type_id, 'hex') AS type_id_hex,
                rel.relation AS type_label,
                encode(t.object_id, 'hex') AS object_id_hex,
                COALESCE(lbl.l[(t.ord - 1) * 2 + 2], converse.label_or_hex(t.object_id)) AS object_label,
                t.eff_mu,
                t.witnesses
            FROM t
            JOIN rel ON rel.type_id = t.type_id
            CROSS JOIN lbl
            ORDER BY t.ord
            """,
            static r => new TopRelationEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetInt64(7)),
            p => p.AddWithValue("limit", limit), ct: ct, label: "top_relations", onError: onError);

    /// <summary><c>render_text_fast</c> with a <c>label_or_hex</c> fallback for one id.</summary>
    public static Task<string?> LabelOrHexAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(conn, """
            SELECT COALESCE(
                NULLIF(realize.render_text_fast(@id, 8), ''),
                converse.label_or_hex(@id));
            """,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "label_or_hex", onError: onError);

    public readonly record struct EntityFacetRow(short Tier, string Type, string Label, bool Exists);

    /// <summary>
    /// <c>ops.entity_facets(id)</c> — no rows for an unwitnessed id, which the caller
    /// falls back to <see cref="LabelOrHexAsync"/> for (safe to call after: the reader here
    /// is fully drained and disposed before this returns, so there is no Npgsql MARS conflict).
    /// </summary>
    public static async Task<EntityFacetRow?> EntityFacetsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, """
            SELECT f.tier,
                   COALESCE(NULLIF(realize.render_text_fast(f.type_id, 4), ''), converse.label_or_hex(f.type_id)),
                   COALESCE(NULLIF(realize.render_text_fast(@id, 8), ''), converse.label_or_hex(@id)),
                   consensus.entity_exists(@id)
            FROM ops.entity_facets(@id) f
            """,
            static r => new EntityFacetRow(r.GetInt16(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_facets", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct MatchupSenseRow(
        string SenseIdHex, string SynsetIdHex, string SynsetLabel, decimal EffMu, long Witnesses);

    /// <summary><c>lexical.senses(id)</c>.</summary>
    public static Task<IReadOnlyList<MatchupSenseRow>> SensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(s.sense_id, 'hex'), encode(s.synset_id, 'hex'),
                   COALESCE(
                       NULLIF(realize._synset_lemma(s.synset_id, converse.word_language(@id)), ''),
                       NULLIF(realize.render_text_fast(s.synset_id, 8), ''),
                       left(encode(s.synset_id, 'hex'), 16)),
                   s.eff_mu, s.witnesses
            FROM lexical.senses(@id) s
            """,
            static r => new MatchupSenseRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "senses", onError: onError);

    public readonly record struct EntityConstituentRow(
        int Ordinal, string ChildIdHex, int RunLength, long Flags, string ChildLabel);

    /// <summary><c>realize.constituents(id)</c>, lossless for every constituent.</summary>
    public static Task<IReadOnlyList<EntityConstituentRow>> ConstituentsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT c.ordinal, encode(c.child_id, 'hex'), c.run_length, c.flags,
                   COALESCE(
                       NULLIF(realize.render_text_fast(c.child_id, 8), ''),
                       left(encode(c.child_id, 'hex'), 16))
            FROM realize.constituents(@id) c
            """,
            static r => new EntityConstituentRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "constituents", onError: onError);

    /// <summary>
    /// Packed trajectory vertices: ST_DumpPoints XYZM + mantissa_unpack.
    /// Identity-space fold for the Packed glome pane — not geometry for Frechet.
    /// </summary>
    public readonly record struct PackedTrajectoryVertexRow(
        int Ordinal, double X, double Y, double Z, double M,
        string ChildIdHex, int RunLength, long Flags);

    public static Task<IReadOnlyList<PackedTrajectoryVertexRow>> PackedTrajectoryVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT u.ordinal,
                   public.ST_X(dp.geom), public.ST_Y(dp.geom),
                   public.ST_Z(dp.geom), public.ST_M(dp.geom),
                   encode(u.entity_id, 'hex'),
                   GREATEST(u.run_length, 1),
                   u.flags
            FROM (
                SELECT w.trajectory
                FROM laplace.v_word_points w
                WHERE w.id = @id AND w.trajectory IS NOT NULL
                LIMIT 1
            ) t,
                 LATERAL public.ST_DumpPoints(t.trajectory) dp,
                 LATERAL public.laplace_mantissa_unpack(dp.geom) u
            ORDER BY u.ordinal
            """,
            static r => new PackedTrajectoryVertexRow(
                r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                r.GetString(5), r.GetInt32(6), r.GetInt64(7)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "packed_trajectory_vertices", onError: onError);

    /// <summary>
    /// Realized curve vertices — same join as word_curve / entity_curve
    /// (child live coords by constituent ordinal). Placement glome ribbon.
    /// </summary>
    public readonly record struct RealizedTrajectoryVertexRow(
        int Ordinal, double X, double Y, double Z, double M,
        string ChildIdHex, string ChildLabel, double Radius);

    public static Task<IReadOnlyList<RealizedTrajectoryVertexRow>> RealizedTrajectoryVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT c.ordinal,
                   public.ST_X(w.coord), public.ST_Y(w.coord),
                   public.ST_Z(w.coord), public.ST_M(w.coord),
                   encode(c.child_id, 'hex'),
                   COALESCE(
                       NULLIF(realize.render_text_fast(c.child_id, 8), ''),
                       left(encode(c.child_id, 'hex'), 16)),
                   w.radius_origin
            FROM realize.constituents(@id) c
            JOIN laplace.v_word_points w ON w.id = c.child_id
            WHERE w.coord IS NOT NULL
            ORDER BY c.ordinal
            """,
            static r => new RealizedTrajectoryVertexRow(
                r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                r.GetString(5), r.GetString(6), r.GetDouble(7)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "realized_trajectory_vertices", onError: onError);

    public readonly record struct ConsensusInLabeledRow(
        string SubjectIdHex, string TypeLabel, string SubjectLabel, decimal EffMu, long Witnesses);

    /// <summary><c>consensus.consensus_in(id, limit)</c> — the inbound half of a matchup.</summary>
    public static Task<IReadOnlyList<ConsensusInLabeledRow>> ConsensusInLabeledAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(c.subject_id, 'hex'),
                   lexical.type_label(c.type_id),
                   COALESCE(
                       NULLIF(realize._synset_lemma(c.subject_id, converse.word_language(@id)), ''),
                       NULLIF(realize.render_text_fast(c.subject_id, 8), ''),
                       left(encode(c.subject_id, 'hex'), 16)),
                   consensus.eff_mu_display(c.rating, c.rd), c.witness_count
            FROM consensus.consensus_in(@id, @limit) c
            """,
            static r => new ConsensusInLabeledRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "consensus_in_labeled", onError: onError);

    public readonly record struct ExploreWebEdgeRow(
        string SourceIdHex, string TypeIdHex, string ObjectIdHex, short Hop, decimal EffMu, long WitnessCount);

    /// <summary>
    /// Native SPI beam (pg_laplace_explore_web) — one connection, undirected consensus
    /// probe, at most <paramref name="fanout"/> new nodes per hop, all tiers.
    /// </summary>
    public static Task<IReadOnlyList<ExploreWebEdgeRow>> ExploreWebAsync(
        NpgsqlConnection conn, byte[] seed, int hops, int fanout, int maxNodes, int timeoutSeconds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(w.source_id, 'hex'), encode(w.type_id, 'hex'), encode(w.object_id, 'hex'),
                   w.hop, consensus.eff_mu(w.rating, w.rd), w.witness_count
            FROM consensus.explore_web(@seed, @hops, @fanout, @max_nodes) w
            """,
            static r => new ExploreWebEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt16(3), r.GetDecimal(4), r.GetInt64(5)),
            p =>
            {
                p.Add("seed", NpgsqlDbType.Bytea).Value = seed;
                p.AddWithValue("hops", hops);
                p.AddWithValue("fanout", fanout);
                p.AddWithValue("max_nodes", maxNodes);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_web", onError: onError);

    public readonly record struct FastLabelRow(string IdHex, string? Label, short? Tier);

    /// <summary>Batch label + tier via <c>render_text_fast</c>/<c>label_or_hex</c>, one round trip.</summary>
    public static Task<IReadOnlyList<FastLabelRow>> LabelsFastAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT encode(x.id, 'hex'),
                   COALESCE(
                       NULLIF(realize.render_text_fast(x.id, 8), ''),
                       converse.label_or_hex(x.id)),
                   e.tier
            FROM unnest(@ids::bytea[]) AS x(id)
            LEFT JOIN laplace.entities e ON e.id = x.id
            """,
            static r => new FastLabelRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "labels_fast", onError: onError);

    public readonly record struct EdgesRawRow(
        string Direction, byte[] TypeId, byte[] NeighbourId,
        long Rating, long Rd, long WitnessCount);

    /// <summary>
    /// <c>consensus.edges_raw(subject, direction, types, limit, refuted, rank)</c> — the one
    /// consensus edge scan. Prefer this (or labeled <c>consensus.edges()</c>) over hand-joining
    /// <c>laplace.consensus</c>.
    /// </summary>
    public static Task<IReadOnlyList<EdgesRawRow>> EdgesRawAsync(
        NpgsqlDataSource dataSource, byte[] subject,
        string direction = "both", byte[][]? types = null, int limit = 40,
        bool refuted = false, string rank = "edge_rank",
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT e.direction, e.type_id, e.neighbour_id,
                   e.rating, e.rd, e.witness_count
            FROM consensus.edges_raw(@subject, @direction, @types, @limit, @refuted, @rank) e
            """,
            static r => new EdgesRawRow(
                r.GetString(0), (byte[])r[1], (byte[])r[2],
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)),
            p =>
            {
                p.Add("subject", NpgsqlDbType.Bytea).Value = subject;
                p.AddWithValue("direction", direction);
                var typesParam = p.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea);
                typesParam.Value = (object?)types ?? DBNull.Value;
                p.AddWithValue("limit", limit);
                p.AddWithValue("refuted", refuted);
                p.AddWithValue("rank", rank);
            }, ct: ct, label: "edges_raw", onError: onError);

    public readonly record struct BestOutboundRow(
        byte[] SubjectId, byte[] ObjectId, decimal EffMu, long WitnessCount);

    /// <summary>
    /// Per-subject strongest outbound edge of one type — <c>unnest</c> ×
    /// <c>consensus.edges_raw(..., limit 1, rank eff_mu)</c>. Replaces
    /// <c>FROM consensus WHERE subject_id = ANY(...) AND type_id = …</c> batch scans
    /// that then max-by-eff_mu in the client (ProvenanceExtractor circuit ENCODES).
    /// </summary>
    public static Task<IReadOnlyList<BestOutboundRow>> BestOutboundBySubjectsAsync(
        NpgsqlDataSource dataSource, byte[][] subjects, byte[] typeId,
        bool refuted = true, int timeoutSeconds = 120,
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT s.id, e.neighbour_id,
                   consensus.eff_mu_display(e.rating, e.rd),
                   e.witness_count
            FROM unnest(@ids::bytea[]) AS s(id)
            CROSS JOIN LATERAL consensus.edges_raw(
                s.id, 'out', ARRAY[@type]::bytea[], 1, @refuted, 'eff_mu') e
            """,
            static r => new BestOutboundRow(
                (byte[])r[0], (byte[])r[1], r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                var ids = p.AddWithValue("ids", subjects);
                ids.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("type", NpgsqlDbType.Bytea).Value = typeId;
                p.AddWithValue("refuted", refuted);
            }, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "edges_raw_best_outbound", onError: onError);

    public readonly record struct TranslationRow(
        string Translation, string Language, decimal EffMu, long Witnesses);

    /// <summary><c>converse.translations(converse.resolve_ref(term), limit)</c>.</summary>
    public static Task<IReadOnlyList<TranslationRow>> TranslationsAsync(
        NpgsqlDataSource dataSource, string term, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT t.translation, t.language, t.eff_mu, t.witnesses
            FROM converse.translations(converse.resolve_ref(@term), @limit) t
            """,
            static r => new TranslationRow(
                r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.AddWithValue("term", term);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "translations", onError: onError);

    public readonly record struct WalkBranchRow(
        int Depth, string Path, decimal EffMu, decimal PathMu, long Witnesses);

    /// <summary><c>consensus.walk_branches</c> over converse.resolve(prompt) or a hex entity id.</summary>
    public static Task<IReadOnlyList<WalkBranchRow>> WalkBranchesAsync(
        NpgsqlDataSource dataSource, string? prompt, string? entityHex, string? relationType,
        int depth, int breadth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH node AS (SELECT CASE WHEN @e IS NULL THEN converse.resolve(@p)
                                      ELSE decode(@e, 'hex') END AS id)
            SELECT w.depth,
                   realize.path(w.path, w.types) AS path,
                   w.eff_mu, w.path_mu, w.witnesses
            FROM node, consensus.walk_branches(
                     node.id,
                     CASE WHEN @t IS NULL THEN NULL ELSE laplace.relation_type_id(@t) END,
                     @depth, @breadth) w
            ORDER BY w.depth, w.path_mu DESC
            """,
            static r => new WalkBranchRow(
                r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                // Explicit types: a DBNull with no NpgsqlDbType leaves the wire type
                // undetermined and the whole statement fails with 42P08 ("could not
                // determine data type of parameter $1") the moment any of the three
                // is null — which is every call, since prompt and entity are
                // mutually exclusive. The surface never worked; measured live
                // 2026-08-13 via the MCP walk tool.
                p.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)prompt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)entityHex ?? DBNull.Value });
                p.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relationType ?? DBNull.Value });
                p.AddWithValue("depth", depth);
                p.AddWithValue("breadth", breadth);
            }, ct: ct, label: "walk_branches", onError: onError);

    /// <summary>
    /// One installed operation. <paramref name="Kind"/> is load-bearing, not
    /// descriptive: a procedure is invoked with CALL and a function with SELECT, so
    /// a caller that cannot tell them apart issues the wrong statement.
    /// </summary>
    public readonly record struct ApiCatalogRow(string Name, string? Args, string? Returns, string? Kind);

    /// <summary><c>ops.api(query)</c> — installed-function catalog search.</summary>
    public static Task<IReadOnlyList<ApiCatalogRow>> ApiCatalogAsync(
        NpgsqlDataSource dataSource, string query, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT name, args, returns, kind FROM ops.api(@q) ORDER BY name
            """,
            static r => new ApiCatalogRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)),
            p => p.AddWithValue("q", query),
            ct: ct, label: "api_catalog", onError: onError);

    /// <summary>
    /// A health metric. <paramref name="Value"/> is NULLABLE on purpose: a metric the
    /// health pass did not measure reports null, which is a different fact from zero and
    /// must survive to the caller rather than throwing or defaulting.
    /// </summary>
    public readonly record struct HealthMetricRow(string Metric, string? Value);

    /// <summary>One source's ingest state — see <c>ops.source_status()</c>.</summary>
    public readonly record struct SourceStatusRow(
        string Source, byte[] SourceId, bool Known, bool Ingested,
        long EvidenceApprox, bool HasEntities, string? LastRunStatus, DateTime? LastRunAt);

    /// <summary>
    /// <c>ops.source_status()</c> — is a source ingested, and how do we know.
    ///
    /// Exists so that no caller ever assembles this again. Every hand-rolled version got
    /// it wrong differently: an evidence test reports the content-only document lane as
    /// absent, a typed source name returns zero rows when the spelling is off, and the run
    /// journal is ops metadata that does not survive a restore. Asking with a name always
    /// returns exactly one row, so absence is an answer instead of an empty result.
    /// </summary>
    public static Task<IReadOnlyList<SourceStatusRow>> SourceStatusAsync(
        NpgsqlDataSource dataSource, string? source, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT source, source_id, known, ingested, evidence_approx, has_entities, "
            + "last_run_status, last_run_at FROM ops.source_status(@s)",
            static r => new SourceStatusRow(
                r.GetString(0), (byte[])r[1], r.GetBoolean(2), r.GetBoolean(3),
                r.GetInt64(4), r.GetBoolean(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetDateTime(7)),
            p => p.Add("s", NpgsqlDbType.Text).Value = (object?)source ?? DBNull.Value,
            ct: ct, label: "source_status", onError: onError);

    /// <summary><c>laplace.substrate_health()</c> flattened to metric/value rows.</summary>
    public static Task<IReadOnlyList<HealthMetricRow>> SubstrateHealthAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT x.metric, x.value
            FROM laplace.substrate_health() h,
                 LATERAL (VALUES ('ok', h.ok::text),
                                 ('fake_tier_bands', h.fake_tier_bands::text),
                                 ('identity_violations', h.identity_violations::text),
                                 -- WITHOUT THIS, identity_violations IS UNREADABLE. It is NULL
                                 -- whenever the deep pass was skipped, and a consumer that
                                 -- cannot see deep_checked reads that NULL as "no violations".
                                 -- That is unattested collapsed into attested-false, in the one
                                 -- query that reports substrate integrity.
                                 ('deep_checked', h.deep_checked::text),
                                 ('bootstrap_entities', h.bootstrap_entities::text)) x(metric, value)
            """,
            // GetString on a NULL column THROWS. identity_violations is null by design when
            // deep_checked is false, so `laplace health` did not report a skipped deep check
            // -- it died with "Column 'value' is null". A null metric is an answer ("not
            // measured"), never an error.
            static r => new HealthMetricRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)),
            ct: ct, label: "substrate_health", onError: onError);

    public readonly record struct QueryShapeRow(
        string Shape, string Summary, bool NeedsTopic2, bool NeedsType, bool AcceptsLang);

    /// <summary><c>converse.query_shapes()</c> — live recall_intent shape catalog.</summary>
    public static Task<IReadOnlyList<QueryShapeRow>> QueryShapesAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT shape, summary, needs_topic2, needs_type, accepts_lang
            FROM converse.query_shapes()
            """,
            static r => new QueryShapeRow(
                r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4)),
            ct: ct, label: "query_shapes", onError: onError);

    public readonly record struct RelationBandRow(
        int Band, string Name, double Rank, long RelationTypes, long ConsensusRows);

    /// <summary><c>converse.relation_bands()</c> — salience bands with live consensus counts.</summary>
    public static Task<IReadOnlyList<RelationBandRow>> RelationBandsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT band, name, rank, relation_types, consensus_rows
            FROM converse.relation_bands()
            """,
            static r => new RelationBandRow(
                r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)),
            ct: ct, label: "relation_bands", onError: onError);

    /// <summary>
    /// Band-gated edges via <c>edges_raw</c> + <c>relation_band_catalog</c> (both directions).
    /// </summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> BandFactsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int[]? bands, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH requested_bands AS MATERIALIZED (
                SELECT b.band, b.name, b.rank,
                       laplace.relation_band_types(b.band) AS type_ids
                FROM converse.relation_band_catalog() b
                WHERE @bands::int[] IS NULL OR b.band = ANY(@bands::int[])
            ), ranked AS MATERIALIZED (
                SELECT b.name, b.rank, z.*
                FROM requested_bands b
                CROSS JOIN LATERAL consensus.edges_raw(
                    @topic, 'both', b.type_ids, @limit, true, 'eff_mu') z
            )
            SELECT z.name || ' · ' || consensus.relation_canonical(z.type_id) || ' → '
                     || converse.label_or_hex(z.neighbour_id) AS reply,
                   consensus.eff_mu_display(z.rating, z.rd) AS eff_mu,
                   z.witness_count
            FROM ranked z
            ORDER BY z.rank DESC,
                     consensus.eff_mu(z.rating, z.rd) DESC,
                     z.neighbour_id,
                     z.type_id,
                     CASE z.direction WHEN 'out' THEN 0 ELSE 1 END
            LIMIT @limit
            """,
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                    (object?)bands ?? DBNull.Value;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "band_facts", onError: onError);

    /// <summary><c>consensus.walk_strongest(topic, NULL, depth)</c> — greedy single chain.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> WalkStrongestAsync(
        NpgsqlDataSource dataSource, byte[] topic, int depth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT converse.label_or_hex(w.entity_id)
                     || ' (' || consensus.relation_canonical(w.type_id) || ')' AS reply,
                   w.eff_mu, NULL::bigint
            FROM consensus.walk_strongest(@topic, NULL, @depth) w
            ORDER BY w.step
            """,
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.AddWithValue("depth", depth);
            }, ct: ct, label: "walk_strongest", onError: onError);

    /// <summary>
    /// <c>consensus.walk_branches</c> gated by a highway band mask and/or named relation type.
    /// </summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> WalkBranchesBeamAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? relationType, int[]? bands,
        int depth, int breadth, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH mask AS (
                SELECT CASE WHEN @bands::int[] IS NULL THEN NULL
                            ELSE consensus.highway_mask_from_bits(
                                     (SELECT array_agg(DISTINCT bit)
                                      FROM unnest(@bands::int[]) AS b(band),
                                           LATERAL unnest(consensus.highway_mask_bits(
                                               consensus.highway_band_mask(b.band))) AS t(bit)))
                       END AS m
            )
            SELECT repeat('  ', w.depth) || realize.path(w.path, w.types) AS reply,
                   w.path_mu, w.witnesses
            FROM mask, consensus.walk_branches(
                     @topic,
                     CASE WHEN @type::text IS NULL THEN NULL
                          ELSE laplace.relation_type_id(@type::text) END,
                     @depth, @breadth, mask.m) w
            ORDER BY w.path_mu DESC
            LIMIT @limit
            """,
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relationType ?? DBNull.Value });
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                    (object?)bands ?? DBNull.Value;
                p.AddWithValue("depth", depth);
                p.AddWithValue("breadth", breadth);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "walk_branches", onError: onError);

    /// <summary><c>converse.astar_path(topic, ARRAY[topic2], depth, NULL, directed, geometry)</c>.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> AstarPathAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[] topic2, int depth,
        bool directed, bool useGeometry, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT repeat('  ', p.step) || converse.label_or_hex(p.entity_id) AS reply,
                   p.g::numeric, NULL::bigint
            FROM converse.astar_path(@topic, ARRAY[@topic2]::bytea[], @depth, NULL,
                                    @directed, @geometry) p
            ORDER BY p.step
            """,
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add("topic2", NpgsqlDbType.Bytea).Value = topic2;
                p.AddWithValue("depth", depth);
                p.AddWithValue("directed", directed);
                p.AddWithValue("geometry", useGeometry);
            }, ct: ct, label: "astar_path", onError: onError);

    /// <summary><c>generation.walk_continuations</c> — seeded trajectory descent.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> WalkContinuationsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int steps, int maxStride,
        double spread, int breadth, long? seed, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT converse.label_or_hex(w.entity) AS reply, NULL::numeric, NULL::bigint
            FROM generation.walk_continuations(ARRAY[@topic]::bytea[], @steps, @stride,
                                            @spread, @breadth, @seed) w
            ORDER BY w.step
            """,
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.AddWithValue("steps", steps);
                p.AddWithValue("stride", maxStride);
                p.AddWithValue("spread", spread);
                p.AddWithValue("breadth", breadth);
                p.Add(new NpgsqlParameter("seed", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)seed ?? DBNull.Value });
            }, ct: ct, label: "walk_continuations", onError: onError);

    /// <inheritdoc cref="StructuralNeighborsAsync(NpgsqlConnection, byte[], int, CancellationToken, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<StructuralNeighborRow>> StructuralNeighborsAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(n.neighbor_id, 'hex'), n.neighbor,
                   n.geodesic, n.frechet, n.x, n.y, n.z, n.m, n.radius
            FROM structural.neighbors_of(@id, @k) n
            """,
            static r => new StructuralNeighborRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("k", limit);
            }, ct: ct, label: "structural_neighbors", onError: onError);

    public readonly record struct ChessRankedPlayerRow(
        long Rank, string IdHex, string Name, long Games, double Rating, double Rd, double EffMu);

    /// <summary><c>chess.ranked(limit, offset)</c>.</summary>
    public static Task<IReadOnlyList<ChessRankedPlayerRow>> ChessRankedAsync(
        NpgsqlDataSource dataSource, int limit, int offset, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        ChessRankedAsync(dataSource, limit, offset, "strength", "desc", ct, onError);

    public static Task<IReadOnlyList<ChessRankedPlayerRow>> ChessRankedAsync(
        NpgsqlDataSource dataSource, int limit, int offset, string sort, string direction,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT rank, encode(player_id, 'hex'), name, games, rating, rd, eff_mu
            FROM chess.ranked(@limit, @offset, @sort, @direction)
            """,
            static r => new ChessRankedPlayerRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)),
            p =>
            {
                p.AddWithValue("limit", RequestedLimit(limit));
                p.AddWithValue("offset", Math.Max(0, offset));
                p.AddWithValue("sort", sort);
                p.AddWithValue("direction", direction);
            }, ct: ct, label: "chess_ranked", onError: onError);

    public readonly record struct ChessPlayerStrengthRow(
        string IdHex, string Name, long Games, double Rating, double Rd, double EffMu);

    /// <summary><c>chess.players_by_initial(initial, limit, offset)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessPlayersByInitialAsync(
        NpgsqlDataSource dataSource, string initial, int limit, int offset, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        ChessPlayersByInitialAsync(
            dataSource, initial, limit, offset, "strength", "desc", ct, onError);

    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessPlayersByInitialAsync(
        NpgsqlDataSource dataSource, string initial, int limit, int offset,
        string sort, string direction, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(player_id, 'hex'), name, games, rating, rd, eff_mu
            FROM chess.players_by_initial(@initials, @limit, @offset, @sort, @direction)
            """,
            static r => new ChessPlayerStrengthRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p =>
            {
                p.Add("initials", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    PlayerCaseForms(initial);
                p.AddWithValue("limit", RequestedLimit(limit));
                p.AddWithValue("offset", Math.Max(0, offset));
                p.AddWithValue("sort", sort);
                p.AddWithValue("direction", direction);
            }, ct: ct, label: "chess_players_by_initial", onError: onError,
            timeoutSeconds: 60);

    /// <summary>
    /// Bounded partial/fuzzy candidates found through the trajectory constituent GIN.
    /// Human-name ranking remains in the endpoint client, where punctuation and edit
    /// distance can be expressed without teaching the substrate a second text identity.
    /// </summary>
    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessPlayerSearchCandidatesAsync(
        NpgsqlDataSource dataSource, string query, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(player_id, 'hex'), name, games, rating, rd, eff_mu
            FROM chess.player_search_candidates(@queries, @limit)
            """,
            static r => new ChessPlayerStrengthRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p =>
            {
                p.Add("queries", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    PlayerCaseForms(query);
                p.AddWithValue("limit", RequestedLimit(limit));
            }, ct: ct, label: "chess_player_search_candidates", onError: onError,
            timeoutSeconds: 30);

    internal static string[] PlayerCaseForms(string value)
    {
        var exact = value.Trim();
        if (exact.Length == 0) return [];

        var lower = exact.ToLowerInvariant();
        var title = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);
        return [.. new[] { exact, lower, title }.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Name → player via <c>chess_player_id</c> + OUTCOME cell through <c>edges_raw</c>.
    /// </summary>
    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessFindPlayerAsync(
        NpgsqlDataSource dataSource, string name, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH p AS (SELECT chess.player_id(@name) AS id)
            SELECT encode(p.id, 'hex'), converse.label_or_hex(p.id),
                   e.witness_count,
                   round((e.rating / 1e9)::numeric, 3)::double precision,
                   round((e.rd / 1e9)::numeric, 3)::double precision,
                   consensus.eff_mu_display(e.rating, e.rd)::double precision
            FROM p
            CROSS JOIN LATERAL consensus.edges_raw(
                p.id, 'out',
                ARRAY[laplace.relation_type_id('OUTCOME')],
                1, true, 'eff_mu') e
            """,
            static r => new ChessPlayerStrengthRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p => p.AddWithValue("name", name),
            ct: ct, label: "chess_player_id", onError: onError);

    public readonly record struct ChessPlayerRecordRow(
        bool? AsWhite, long Games, long Wins, long Draws, long Losses, long Unscored, double? Score);

    /// <summary><c>chess.player_record(id)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerRecordRow>> ChessPlayerRecordAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT as_white, games, wins, draws, losses, unscored, score
            FROM chess.player_record(@id)
            """,
            static r => new ChessPlayerRecordRow(
                r.IsDBNull(0) ? null : r.GetBoolean(0),
                r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                r.GetInt64(5), r.IsDBNull(6) ? null : r.GetDouble(6)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_player_record", onError: onError);

    public readonly record struct ChessPlayerRatingRow(int Rating, long Games);

    /// <summary><c>chess.player_ratings(id)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerRatingRow>> ChessPlayerRatingsAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT rating, games FROM chess.player_ratings(@id)
            """,
            static r => new ChessPlayerRatingRow(r.GetInt32(0), r.GetInt64(1)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_player_ratings", onError: onError);

    public readonly record struct ChessHeadToHeadRow(
        string OpponentIdHex, string Opponent, long Games, double Rating, double Rd, double EffMu);

    /// <summary><c>chess.head_to_head(id, limit)</c>.</summary>
    public static Task<IReadOnlyList<ChessHeadToHeadRow>> ChessHeadToHeadAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(opponent_id, 'hex'), opponent, games, rating, rd, eff_mu
            FROM chess.head_to_head(@id, @limit)
            """,
            static r => new ChessHeadToHeadRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", RequestedLimit(limit));
            }, ct: ct, label: "chess_head_to_head", onError: onError);

    public readonly record struct ChessPlayerGameRow(
        string EventIdHex, string? PlayedOn, string? Event, string? Eco, bool AsWhite,
        string? OpponentIdHex, string Opponent, string? Result, short? Outcome);

    /// <summary><c>chess.player_games(id, limit, offset)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerGameRow>> ChessPlayerGamesAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, int offset, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(event_id, 'hex'), played_on, event, eco, as_white,
                   encode(opponent_id, 'hex'), opponent, result, outcome
            FROM chess.player_games(@id, @limit, @offset)
            """,
            static r => new ChessPlayerGameRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetBoolean(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt16(8)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", RequestedLimit(limit));
                p.AddWithValue("offset", Math.Max(0, offset));
            }, ct: ct, label: "chess_player_games", onError: onError,
            timeoutSeconds: 60);

    public readonly record struct ChessGameDetailRow(
        string? WhiteIdHex, string White, string? BlackIdHex, string Black,
        string? Result, string? PlayedOn, string? Event, string? Eco,
        string? Termination, string? TimeControl, string? TcClass, string? Movetext);

    /// <summary><c>chess.game(id)</c>.</summary>
    public static Task<IReadOnlyList<ChessGameDetailRow>> ChessGameAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT encode(white_id, 'hex'), white, encode(black_id, 'hex'), black,
                   result, played_on, event, eco, termination, time_control,
                   tc_class, movetext
            FROM chess.game(@id)
            """,
            static r => new ChessGameDetailRow(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_game", onError: onError,
            timeoutSeconds: 60);

    public readonly record struct ModelRecipeRow(byte[] RecipeId, string RecipeJson);

    /// <summary><c>structural.model_recipes()</c>.</summary>
    public static Task<IReadOnlyList<ModelRecipeRow>> ModelRecipesAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT recipe_id, recipe_json FROM structural.model_recipes()
            """,
            static r => new ModelRecipeRow((byte[])r[0], r.GetString(1)),
            ct: ct, label: "model_recipes", onError: onError,
            timeoutSeconds: 30);

    public readonly record struct EntityTypeCountRow(byte[] TypeId, long Count);

    /// <summary>
    /// Count entities per requested type_id through the installed
    /// <c>entity_counts_by_types</c>. Every requested type comes back, including
    /// the ones with no rows — a 0 count and an absent row are different answers
    /// and the installed function keeps them apart.
    /// </summary>
    public static Task<IReadOnlyList<EntityTypeCountRow>> EntityCountsByTypesAsync(
        NpgsqlDataSource dataSource, byte[][] typeIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT type_id, entities FROM ops.entity_counts_by_types(@types)
            """,
            static r => new EntityTypeCountRow((byte[])r[0], r.GetInt64(1)),
            p =>
            {
                var parameter = p.AddWithValue("types", typeIds);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            },
            ct: ct, label: "entity_counts_by_types", onError: onError,
            timeoutSeconds: 30);

    public readonly record struct TrajectoryDumpPointRow(
        byte[] Id, int NConstituents, int PathIndex, double X, double Y, double Z, double M);

    /// <summary>
    /// Recursive constituent walk of a document via <c>v_word_points</c> +
    /// <c>laplace_trajectory_constituent_ids</c>, dumping trajectory vertices.
    /// </summary>
    public static Task<IReadOnlyList<TrajectoryDumpPointRow>> TrajectoryTreeDumpPointsAsync(
        NpgsqlDataSource dataSource, byte[] documentId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH RECURSIVE tree(id) AS (
                SELECT @doc
                UNION
                SELECT unnest(public.laplace_trajectory_constituent_ids(w.trajectory))
                FROM tree t
                JOIN laplace.v_word_points w
                  ON w.id = t.id AND w.trajectory IS NOT NULL
            )
            SELECT w.id, w.n_constituents, (g.path)[1],
                   ST_X(g.geom), ST_Y(g.geom), ST_Z(g.geom), ST_M(g.geom)
            FROM laplace.v_word_points w
            JOIN tree t ON t.id = w.id
            CROSS JOIN LATERAL ST_DumpPoints(w.trajectory) AS g
            WHERE w.trajectory IS NOT NULL
            """,
            static r => new TrajectoryDumpPointRow(
                (byte[])r[0], r.GetInt32(1), r.GetInt32(2),
                r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)),
            p => p.AddWithValue("doc", documentId),
            ct: ct, label: "trajectory_tree_dump", onError: onError);

    public readonly record struct ChessMoveRow(
        byte[] NextPosition, double EffMu, double Rd, long WitnessCount);

    public readonly record struct ChessTrajectorySuccessorRow(
        byte[] NextPosition, long Seen);

    /// <summary>
    /// Exact next-position candidates projected from ordered line trajectories. This is
    /// structure, not a second MOVE testimony population.
    /// </summary>
    public static Task<IReadOnlyList<ChessTrajectorySuccessorRow>> ChessTrajectorySuccessorsAsync(
        NpgsqlDataSource dataSource, byte[] rootId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT successor_id, seen
            FROM structural.geometry_successors(@root, @limit, 1, false)
            """,
            static r => new ChessTrajectorySuccessorRow(
                (byte[])r[0], r.GetInt64(1)),
            p =>
            {
                p.Add("root", NpgsqlDbType.Bytea).Value = rootId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_trajectory_successors", onError: onError);

    /// <summary><c>chess.moves(root, limit)</c>.</summary>
    public static Task<IReadOnlyList<ChessMoveRow>> ChessMovesAsync(
        NpgsqlDataSource dataSource, byte[] rootId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT next_position, eff_mu, rd, witness_count
            FROM chess.moves(@root, @limit)
            """,
            static r => new ChessMoveRow(
                (byte[])r[0], r.GetDouble(1), r.GetDouble(2), r.GetInt64(3)),
            p =>
            {
                p.Add("root", NpgsqlDbType.Bytea).Value = rootId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_moves", onError: onError);

    public readonly record struct ChessPlayerMoveRow(
        byte[] NextPosition, long Games, double Score);

    /// <summary><c>chess.player_moves(root, player, white, limit)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerMoveRow>> ChessPlayerMovesAsync(
        NpgsqlDataSource dataSource, byte[] rootId, byte[] playerId, bool whiteToMove,
        int limit, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT next_position, games, score
            FROM chess.player_moves(@root, @player, @white, @limit)
            """,
            static r => new ChessPlayerMoveRow((byte[])r[0], r.GetInt64(1), r.GetDouble(2)),
            p =>
            {
                p.Add("root", NpgsqlDbType.Bytea).Value = rootId;
                p.Add("player", NpgsqlDbType.Bytea).Value = playerId;
                p.AddWithValue("white", whiteToMove);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_player_moves", onError: onError);

    /// <summary>First attestation subject for (object_id, type_id).</summary>
    public static Task<byte[]?> FirstAttestationSubjectAsync(
        NpgsqlDataSource dataSource, byte[] objectId, byte[] typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource, """
            SELECT subject_id FROM laplace.attestations
            WHERE object_id = @obj AND type_id = @type
            LIMIT 1
            """,
            p =>
            {
                p.Add("obj", NpgsqlDbType.Bytea).Value = objectId;
                p.Add("type", NpgsqlDbType.Bytea).Value = typeId;
            }, ct: ct, label: "attestation_subject", onError: onError);

    /// <summary>Exact <c>realize.render_text_batch(ids)</c>.</summary>
    public static async Task<string[]?> RenderTextBatchAsync(
        NpgsqlDataSource dataSource, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var scalar = await NpgsqlRead.ExecuteScalarAsync<object>(dataSource, """
            SELECT realize.render_text_batch(@ids)
            """,
            p =>
            {
                var parameter = p.AddWithValue("ids", ids);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "render_text_batch", onError: onError).ConfigureAwait(false);
        return scalar as string[];
    }

    /// <summary>
    /// Every (terminal position, name, eco) from the openings catalog's named line
    /// trajectories, for the board-identity opening matcher. One bounded read of a few
    /// thousand rows at Initialize, not a query per record — the caller probes the result
    /// in memory. The terminal position is structure recovered from the ordered line; the
    /// catalog does not duplicate OPENING_NAME/HAS_ECO testimony onto that board.
    ///
    /// Lives here rather than in the chess lane because ReadPathArchitectureGateTests
    /// forbids hand-written SQL in a consumer, and it is right to: one implementation, one
    /// place, every caller the same. It caught this exact query inline in
    /// ChessOpeningIndex.cs.
    /// </summary>
    public static Task<IReadOnlyList<(byte[] Position, byte[] Name, byte[]? Eco)>>
        OpeningCatalogAsync(
            NpgsqlDataSource dataSource, byte[] openingNameType, byte[] hasEcoType,
            byte[] sourceId, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        // ORDER BY is load-bearing, not cosmetic. The consumer takes FIRST-WINS when a
        // position carries several names, so without an ordering the name a position gets
        // is whatever the plan happened to emit — it could change after a replan, a vacuum
        // or a parallel scan, which is non-deterministic naming in a content-addressed
        // system. Rank by what the fold produced (eff_mu over the name cell), with the
        // object id as a total tiebreak so the result is reproducible even for unattested
        // names. consensus.eff_mu() is called, never inlined as `rating - 2*rd` — that literal is
        // what g1_weight_literalism exists to reject.
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH named AS MATERIALIZED (
                SELECT n.subject_id AS line_id,
                       n.object_id AS name_id,
                       e.object_id AS eco_id,
                       consensus.eff_mu(c.rating, c.rd) AS rank
                FROM laplace.attestations n
                LEFT JOIN laplace.attestations e
                       ON e.subject_id = n.subject_id
                      AND e.type_id    = @eco
                      AND e.source_id  = @src
                LEFT JOIN laplace.consensus c
                       ON c.subject_id = n.subject_id
                      AND c.type_id    = n.type_id
                      AND c.object_id  = n.object_id
                WHERE n.type_id   = @name
                  AND n.source_id = @src
            ), terminal AS MATERIALIZED (
                SELECT n.name_id, n.eco_id, n.rank, p.entity_id AS position_id
                FROM named n
                CROSS JOIN LATERAL (
                    SELECT u.entity_id
                    FROM generation.trajectory_unpacked_points(n.line_id)
                         WITH ORDINALITY AS u(entity_id, run_length, ctier, ord)
                    ORDER BY u.ord DESC
                    LIMIT 1
                ) p
            )
            SELECT position_id, name_id, eco_id
            FROM terminal
            ORDER BY position_id, rank DESC NULLS LAST, name_id
            """,
            r => ((byte[])r[0], (byte[])r[1], r.IsDBNull(2) ? null : (byte[])r[2]),
            p =>
            {
                p.Add("name", NpgsqlDbType.Bytea).Value = openingNameType;
                p.Add("eco", NpgsqlDbType.Bytea).Value = hasEcoType;
                p.Add("src", NpgsqlDbType.Bytea).Value = sourceId;
            },
            ct: ct, label: "opening_catalog", onError: onError);

    /// <summary>
    /// The ORDERED CONSTITUENT SURFACES of composed documents, rejoined with a space —
    /// one query for the whole batch.
    ///
    /// <c>render_text_batch</c> concatenates a composition's constituents with NO
    /// separator, which is lossy for any content whose tokens were split ON a separator:
    /// a chess movetext reads back as <c>1.d4d52.c4dxc43…</c> and no parser can tokenize
    /// it. This returns each document rebuilt from its trajectory, the way its own
    /// tokenizer split it, which is the only join that round-trips.
    ///
    /// BATCHED ON PURPOSE. The first cut took one round trip per document; hydrating a
    /// 6,365-line corpus meant 6,365 queries and did not finish in ten minutes. The token
    /// ids of every document in the chunk are gathered, realized ONCE over the DISTINCT
    /// set — ply tokens repeat ferociously, there are only a few thousand distinct SAN
    /// strings in all of chess — and rejoined per document. Render is the last operation
    /// and it runs once.
    /// </summary>
    public static async Task<Dictionary<string, string>> TrajectoryTokenTextBatchAsync(
        NpgsqlDataSource dataSource, byte[][] entityIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entityIds.Length == 0) return result;

        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH doc AS (
                SELECT id FROM unnest(@ids) AS x(id)
            ),
            tok AS (
                -- WITH ORDINALITY, not row_number() OVER (): the function ORDERs BY its
                -- internal ordinal, but that ordering is not guaranteed to survive into an
                -- outer window over a lateral join. Measured — the same document rebuilt to
                -- 101 tokens on one run and 107 on the next.
                --
                -- run_length is RUN-LENGTH ENCODING, floored at 1: a token repeated N times
                -- consecutively packs into ONE point. Dropping it silently shortens every
                -- document that repeats a token — which a chess movetext does constantly.
                SELECT d.id AS doc_id, p.entity_id, p.ord AS tord, rep.n AS repeat_ix
                FROM doc d
                CROSS JOIN LATERAL (
                    SELECT t.entity_id, t.run_length, o AS ord
                    FROM generation.trajectory_unpacked_points(d.id) WITH ORDINALITY AS t(entity_id, run_length, ctier, o)
                ) p
                CROSS JOIN LATERAL generate_series(1, GREATEST(p.run_length, 1)) AS rep(n)
            ),
            distinct_ids AS (
                SELECT COALESCE(array_agg(DISTINCT entity_id), '{}'::bytea[]) AS a FROM tok
            ),
            label AS (
                SELECT p.id AS eid, t.txt
                FROM distinct_ids b,
                     LATERAL unnest(b.a) WITH ORDINALITY AS p(id, i),
                     LATERAL unnest(realize.batch(b.a, NULL)) WITH ORDINALITY AS t(txt, j)
                WHERE p.i = t.j
            )
            SELECT tok.doc_id, string_agg(label.txt, ' ' ORDER BY tok.tord, tok.repeat_ix)
            FROM tok JOIN label ON label.eid = tok.entity_id
            GROUP BY tok.doc_id
            """,
            r => (Id: Convert.ToHexString((byte[])r[0]), Text: r.IsDBNull(1) ? null : r.GetString(1)),
            p =>
            {
                var parameter = p.AddWithValue("ids", entityIds);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            },
            ct: ct, label: "trajectory_token_text_batch", onError: onError).ConfigureAwait(false);
        foreach (var (id, text) in rows)
            if (text is not null) result[id] = text;
        return result;
    }

    /// <summary><c>generation.recall_trajectories(word_id(w), k)</c> — first answer.</summary>
    public static Task<string?> RecallTrajectoryAnswerAsync(
        NpgsqlDataSource dataSource, string word, int k, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(dataSource, """
            SELECT answer FROM generation.recall_trajectories(laplace.word_id(@w), @k) LIMIT 1
            """,
            p =>
            {
                p.AddWithValue("w", word);
                p.AddWithValue("k", k);
            }, ct: ct, label: "recall_trajectories", onError: onError);

    /// <summary>Top <c>consensus.salient_facts(word_id(w), NULL, k)</c> fact by eff_mu.</summary>
    public static Task<string?> SalientFactForWordAsync(
        NpgsqlDataSource dataSource, string word, int k, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(dataSource, """
            SELECT fact FROM consensus.salient_facts(laplace.word_id(@w), NULL, @k)
            ORDER BY eff_mu DESC LIMIT 1
            """,
            p =>
            {
                p.AddWithValue("w", word);
                p.AddWithValue("k", k);
            }, ct: ct, label: "salient_facts_word", onError: onError);

    /// <summary>Count distinct event entities with PLAYS_LINE under given sources.</summary>
    public static async Task<long> CountChessEventsWithPlaysLineAsync(
        NpgsqlDataSource dataSource, byte[] eventTypeId, byte[] playsLineTypeId,
        byte[][] sourceIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var n = await NpgsqlRead.ExecuteScalarAsync<long>(dataSource, """
            SELECT count(DISTINCT e.id)
            FROM laplace.entities e
            JOIN laplace.attestations pl
              ON pl.subject_id = e.id
             AND pl.type_id = @plays
             AND pl.source_id = ANY(@sources)
            WHERE e.type_id = @event_type
            """,
            p =>
            {
                p.Add("event_type", NpgsqlDbType.Bytea).Value = eventTypeId;
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "chess_count_events", onError: onError).ConfigureAwait(false);
        return n;
    }

    /// <summary>Count distinct PLAYS_LINE objects under given sources.</summary>
    public static async Task<long> CountChessLinesWithPlaysLineAsync(
        NpgsqlDataSource dataSource, byte[] playsLineTypeId, byte[][] sourceIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var n = await NpgsqlRead.ExecuteScalarAsync<long>(dataSource, """
            SELECT count(DISTINCT pl.object_id)
            FROM laplace.attestations pl
            WHERE pl.type_id = @plays
              AND pl.source_id = ANY(@sources)
            """,
            p =>
            {
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "chess_count_lines", onError: onError).ConfigureAwait(false);
        return n;
    }

    /// <summary>Page of recorded event ids (keyset on e.id).</summary>
    public static Task<IReadOnlyList<byte[]>> ChessEventIdPageAsync(
        NpgsqlDataSource dataSource, byte[] eventTypeId, byte[] playsLineTypeId,
        byte[][] sourceIds, byte[] afterId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT DISTINCT e.id
            FROM laplace.entities e
            JOIN laplace.attestations pl
              ON pl.subject_id = e.id
             AND pl.type_id = @plays
             AND pl.source_id = ANY(@sources)
            WHERE e.type_id = @event_type
              AND (octet_length(@after) = 0 OR e.id > @after)
            ORDER BY e.id
            LIMIT @limit
            """,
            static r => (byte[])r[0],
            p =>
            {
                p.Add("event_type", NpgsqlDbType.Bytea).Value = eventTypeId;
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("after", NpgsqlDbType.Bytea).Value =
                    afterId.Length == 0 ? Array.Empty<byte>() : afterId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_event_id_page", onError: onError);

    /// <summary>Page of recorded line ids (keyset on PLAYS_LINE object_id).</summary>
    public static Task<IReadOnlyList<byte[]>> ChessLineIdPageAsync(
        NpgsqlDataSource dataSource, byte[] playsLineTypeId, byte[][] sourceIds,
        byte[] afterId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT DISTINCT pl.object_id
            FROM laplace.attestations pl
            WHERE pl.type_id = @plays
              AND pl.source_id = ANY(@sources)
              AND (octet_length(@after) = 0 OR pl.object_id > @after)
            ORDER BY pl.object_id
            LIMIT @limit
            """,
            static r => (byte[])r[0],
            p =>
            {
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("after", NpgsqlDbType.Bytea).Value =
                    afterId.Length == 0 ? Array.Empty<byte>() : afterId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_line_id_page", onError: onError);

    public readonly record struct AttestationEdgeRow(
        byte[] SubjectId, byte[] ObjectId);

    /// <summary>Attestation (subject, object) for subjects + one type.</summary>
    public static Task<IReadOnlyList<AttestationEdgeRow>> AttestationsBySubjectsAndTypeAsync(
        NpgsqlDataSource dataSource, byte[][] subjectIds, byte[] typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT a.subject_id, a.object_id
            FROM laplace.attestations a
            WHERE a.subject_id = ANY(@subjects)
              AND a.type_id = @type
            """,
            static r => new AttestationEdgeRow((byte[])r[0], (byte[])r[1]),
            p =>
            {
                var subjects = p.AddWithValue("subjects", subjectIds);
                subjects.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("type", NpgsqlDbType.Bytea).Value = typeId;
            }, ct: ct, label: "attestations_by_subjects_type", onError: onError);

    public readonly record struct AttestationQuadRow(
        byte[] SubjectId, byte[] TypeId, byte[]? ObjectId, byte[]? ContextId);

    /// <summary>Attestation quads for subjects filtered by type set.</summary>
    public static Task<IReadOnlyList<AttestationQuadRow>> AttestationsBySubjectsAndTypesAsync(
        NpgsqlDataSource dataSource, byte[][] subjectIds, byte[][] typeIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT a.subject_id, a.type_id, a.object_id, a.context_id
            FROM laplace.attestations a
            WHERE a.subject_id = ANY(@subjects)
              AND a.type_id = ANY(@types)
            """,
            static r => new AttestationQuadRow(
                (byte[])r[0], (byte[])r[1],
                r.IsDBNull(2) ? null : (byte[])r[2],
                r.IsDBNull(3) ? null : (byte[])r[3]),
            p =>
            {
                var subjects = p.AddWithValue("subjects", subjectIds);
                subjects.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                var types = p.AddWithValue("types", typeIds);
                types.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "attestations_by_subjects_types", onError: onError);
}
