using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct TaxonomyTreeRow(
        string Dir, int Ord, string IdHex, string Label, decimal? EffMu);

    public static Task<IReadOnlyList<TaxonomyTreeRow>> TaxonomyTreeAsync(
        NpgsqlDataSource dataSource, byte[] id, int depth, int childLimit,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        int cappedDepth = Math.Clamp(RequestedLimit(depth), 1, 5);
        int cappedChildren = Math.Max(1, RequestedLimit(childLimit));

        // Build exact variable-depth directed programs (1..cappedDepth) over governed taxonomy predicates.
        string[] predNames = ["hypernym", "instance_hypernym", "subclass of", "instance of", "IS_A"];
        var stepNames = new List<string>();
        var programs = new List<int>();
        var depths = new List<int>();
        var inverse = new List<bool>();
        for (int p = 1; p <= cappedDepth; p++)
        {
            for (int d = 0; d < p; d++)
            {
                foreach (var pred in predNames)
                {
                    stepNames.Add(pred);
                    programs.Add(p);
                    depths.Add(d);
                    inverse.Add(false);
                }
            }
        }

        const string sql = """
            WITH hypernym AS MATERIALIZED (
                SELECT ARRAY[
                    public.laplace_text_id('hypernym'),
                    public.laplace_text_id('instance_hypernym'),
                    public.laplace_text_id('subclass of'),
                    public.laplace_text_id('instance of'),
                    public.laplace_text_id('IS_A')
                ] AS rels
            ),
            hyponym AS MATERIALIZED (
                SELECT ARRAY[
                    public.laplace_text_id('hyponym'),
                    public.laplace_text_id('instance_hyponym')
                ] AS rels
            ),
            anchor_senses AS MATERIALIZED (
                SELECT pr.pts[cardinality(pr.pts)] AS syn_id
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) cs,
                     LATERAL (SELECT public.laplace_parts(cs.path) AS pts) ps,
                     public.laplace_claims_each(
                         ARRAY[ps.pts[cardinality(ps.pts)]], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) cr,
                     LATERAL (SELECT public.laplace_parts(cr.path) AS pts) pr
                WHERE cardinality(ps.pts) = 3 AND cardinality(pr.pts) = 3
                LIMIT 8
            ),
            anchors AS MATERIALIZED (
                SELECT public.blake3_from_bytea($1) AS aid
                UNION
                SELECT syn_id FROM anchor_senses
            ),
            self_row AS (
                SELECT 'self'::text AS dir, 0 AS ord, encode($1, 'hex') AS id_hex,
                       coalesce(public.laplace_label_text($1, false), encode($1, 'hex')) AS label,
                       NULL::numeric AS eff_mu
            ),
            up_chain AS MATERIALIZED (
                SELECT ch.entity,
                       min(ch.depth) + 1 AS ord,
                       max(public.laplace_confidence(ch.rating, ch.deviation, 2::double precision))::numeric AS eff_mu,
                       max(ch.matches) AS witnesses
                FROM public.laplace_chains(
                    ARRAY(SELECT aid FROM anchors),
                    ARRAY(SELECT public.laplace_text_id(s) FROM unnest($3::text[]) AS s),
                    $4::int4[],
                    $5::int4[],
                    $6::bool[],
                    8192::bigint) ch
                WHERE ch.entity <> ALL(ARRAY(SELECT aid FROM anchors))
                GROUP BY ch.entity
                ORDER BY min(ch.depth) ASC, max(ch.matches) DESC
                LIMIT greatest(1, $2) * 4
            ),
            up_rows AS (
                SELECT 'up'::text AS dir,
                       u.ord,
                       encode(public.blake3_send(u.entity), 'hex') AS id_hex,
                       coalesce(
                           nullif((
                               SELECT string_agg(m.lbl, ', ')
                               FROM (
                                   SELECT DISTINCT coalesce(public.laplace_label_text(public.blake3_send(mp.pts[2]), false), '') AS lbl
                                   FROM public.laplace_claims_each(
                                       ARRAY[u.entity], 12::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                                       NULL, 2::bigint) mc,
                                        LATERAL (SELECT public.laplace_parts(mc.path) AS pts) mp
                                   WHERE cardinality(mp.pts) = 2
                               ) m
                               WHERE m.lbl <> ''
                                 AND m.lbl !~ '^i[0-9]+$'
                                 AND m.lbl !~ '^(noun|verb|adj|adv)\.[a-z_]+$'
                                 AND m.lbl !~ '^(NOUN|VERB|ADJ|ADV|PROPN|PRON|DET|ADP|CONJ|SCONJ|CCONJ|NUM|PART|INTJ|AUX|SYM|PUNCT|X)$'
                                 AND length(m.lbl) <= 64
                           ), ''),
                           coalesce(public.laplace_label_text(public.blake3_send(u.entity), false), encode(public.blake3_send(u.entity), 'hex'))
                       ) AS label,
                       u.eff_mu
                FROM up_chain u
            ),
            down_raw AS (
                SELECT d.pts[1] AS child_id,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches
                FROM hypernym h,
                     public.laplace_claims_each(ARRAY(SELECT aid FROM anchors), greatest(1, $2)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 2::smallint, h.rels) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) d
                WHERE cardinality(d.pts) >= 2
                UNION ALL
                SELECT d.pts[cardinality(d.pts)] AS child_id,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches
                FROM hyponym hy,
                     public.laplace_claims_each(ARRAY(SELECT aid FROM anchors), greatest(1, $2)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint, hy.rels) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) d
                WHERE cardinality(d.pts) >= 2
            ),
            down_dedup AS (
                SELECT child_id,
                       max(eff_mu) AS eff_mu,
                       max(matches) AS matches
                FROM down_raw
                WHERE child_id <> ALL(ARRAY(SELECT aid FROM anchors))
                GROUP BY child_id
                ORDER BY max(eff_mu) DESC NULLS LAST, max(matches) DESC
                LIMIT greatest(0, $2)
            ),
            down_rows AS (
                SELECT 'down'::text AS dir,
                       row_number() OVER (ORDER BY d.eff_mu DESC NULLS LAST, d.matches DESC)::int AS ord,
                       encode(public.blake3_send(d.child_id), 'hex') AS id_hex,
                       coalesce(
                           nullif((
                               SELECT string_agg(m.lbl, ', ')
                               FROM (
                                   SELECT DISTINCT coalesce(public.laplace_label_text(public.blake3_send(mp.pts[2]), false), '') AS lbl
                                   FROM public.laplace_claims_each(
                                       ARRAY[d.child_id], 12::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                                       NULL, 2::bigint) mc,
                                        LATERAL (SELECT public.laplace_parts(mc.path) AS pts) mp
                                   WHERE cardinality(mp.pts) = 2
                               ) m
                               WHERE m.lbl <> ''
                                 AND m.lbl !~ '^i[0-9]+$'
                                 AND m.lbl !~ '^(noun|verb|adj|adv)\.[a-z_]+$'
                                 AND m.lbl !~ '^(NOUN|VERB|ADJ|ADV|PROPN|PRON|DET|ADP|CONJ|SCONJ|CCONJ|NUM|PART|INTJ|AUX|SYM|PUNCT|X)$'
                                 AND length(m.lbl) <= 64
                           ), ''),
                           coalesce(public.laplace_label_text(public.blake3_send(d.child_id), false), encode(public.blake3_send(d.child_id), 'hex'))
                       ) AS label,
                       d.eff_mu
                FROM down_dedup d
            )
            SELECT dir, ord, id_hex, label, eff_mu FROM self_row
            UNION ALL
            SELECT dir, ord, id_hex, label, eff_mu FROM up_rows
            UNION ALL
            SELECT dir, ord, id_hex, label, eff_mu FROM down_rows
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new TaxonomyTreeRow(
                r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetDecimal(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(cappedChildren);
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = stepNames.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer, Value = programs.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer, Value = depths.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean, Value = inverse.ToArray() });
            }, ct: ct, label: "taxonomy_tree", onError: onError);
    }

    public static Task<byte[]?> ResolveRefAsync(
        NpgsqlDataSource dataSource, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(
            dataSource,
            "SELECT public.laplace_resolve_ref($1)",
            p => p.AddWithValue(reference),
            ct: ct, label: "resolve_ref", onError: onError);

    public readonly record struct ConceptMemberRow(
        string IdHex, string Kind, string Label, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<ConceptMemberRow>> ConceptMembersAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS (
                SELECT public.laplace_parts(c.path) AS pts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 2::smallint) c
                LIMIT greatest(0, $2)
            )
            SELECT encode(public.blake3_send(pts[1]), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(pts[2]), false), 'member'),
                   coalesce(public.laplace_label_text(public.blake3_send(pts[1]), false), encode(public.blake3_send(pts[1]), 'hex')),
                   eff_mu,
                   matches
            FROM claims
            WHERE cardinality(pts) >= 2
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ConceptMemberRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "concept_members", onError: onError);
    }

    public readonly record struct ConceptPeerRow(string Peer, string Kind, double Strength);

    public static Task<IReadOnlyList<ConceptPeerRow>> ConceptPeersAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   coalesce(public.laplace_label_text(public.blake3_send(c.rel), false), 'peer'),
                   public.laplace_confidence(c.rating, c.deviation, 2::double precision)
            FROM public.laplace_couple(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, '{}'::public.blake3[], 0::smallint, 0.0, greatest(1, $2)) c
            ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC NULLS LAST
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ConceptPeerRow(r.GetString(0), r.GetString(1), r.GetDouble(2)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "concept_peers", onError: onError);
    }

    public readonly record struct WordPeerIdRow(byte[] WordId, byte[] PeerId, double Strength);

    public static Task<IReadOnlyList<WordPeerIdRow>> WordPeerIdsBatchAsync(
        NpgsqlConnection conn, byte[][] wordIds, int limitPerWord, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        if (wordIds.Length == 0)
            return Task.FromResult<IReadOnlyList<WordPeerIdRow>>(Array.Empty<WordPeerIdRow>());

        const string sql = """
            WITH inp AS MATERIALIZED (
                SELECT ord, public.blake3_from_bytea(raw_id) AS id, raw_id
                FROM unnest($1::bytea[]) WITH ORDINALITY AS u(raw_id, ord)
            )
            SELECT i.raw_id,
                   public.blake3_send(c.entity),
                   public.laplace_confidence(c.rating, c.deviation, 2)
            FROM inp i
            CROSS JOIN LATERAL public.laplace_couple(ARRAY[i.id], greatest(1, $2)::bigint, '{}'::public.blake3[], 0::smallint, 0.0, greatest(1, $2)) c
            WHERE c.route = 0
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new WordPeerIdRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = wordIds });
                p.AddWithValue(Math.Max(1, limitPerWord));
            }, ct: ct, label: "word_peer_ids_batch", onError: onError);
    }

    private static readonly ConcurrentDictionary<Hash128, Hash128[]> SemanticPeerCache = new();

    public static async Task<IReadOnlyDictionary<Hash128, Hash128[]>?> ResolveSemanticPeersAsync(
        NpgsqlDataSource? dataSource, IEnumerable<Hash128> wordIds,
        CancellationToken ct = default, int limitPerWord = 24)
    {
        ct.ThrowIfCancellationRequested();
        if (dataSource is null) return null;

        var result = new Dictionary<Hash128, Hash128[]>();
        var missing = new List<Hash128>();
        foreach (var wid in wordIds)
        {
            if (wid == default) continue;
            if (SemanticPeerCache.TryGetValue(wid, out var cached))
            {
                if (cached.Length > 0) result[wid] = cached;
            }
            else
            {
                missing.Add(wid);
            }
        }

        if (missing.Count > 0)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            var queryBytes = missing.Select(static id => id.ToBytes()).ToArray();
            var rows = await WordPeerIdsBatchAsync(conn, queryBytes, limitPerWord, ct).ConfigureAwait(false);
            var grouped = new Dictionary<Hash128, List<Hash128>>(missing.Count);
            foreach (var m in missing) grouped[m] = [];
            foreach (var row in rows)
            {
                if (row.WordId is { Length: 16 } && row.PeerId is { Length: 16 })
                {
                    var wid = Hash128.FromBytes(row.WordId);
                    var pid = Hash128.FromBytes(row.PeerId);
                    if (grouped.TryGetValue(wid, out var list)) list.Add(pid);
                }
            }
            if (SemanticPeerCache.Count >= 4096) SemanticPeerCache.Clear();
            foreach (var (wid, peers) in grouped)
            {
                var distinct = peers.Distinct().ToArray();
                SemanticPeerCache.TryAdd(wid, distinct);
                if (distinct.Length > 0) result[wid] = distinct;
            }
        }
        return result;
    }

    public readonly record struct MatchupSenseRow(
        string SenseIdHex, string SynsetIdHex, string SynsetLabel, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<MatchupSenseRow>> SensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS (
                SELECT public.laplace_parts(c.path) AS pts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], 24::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint) c
                LIMIT 24
            )
            SELECT encode(public.blake3_send(pts[1]), 'hex'),
                   encode(public.blake3_send(pts[cardinality(pts)]), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(pts[cardinality(pts)]), false), encode(public.blake3_send(pts[cardinality(pts)]), 'hex')),
                   eff_mu,
                   matches
            FROM claims
            WHERE cardinality(pts) >= 2
            ORDER BY eff_mu DESC, matches DESC
            LIMIT 12
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new MatchupSenseRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "senses", onError: onError);
    }

    public readonly record struct TranslationRow(
        string Translation, string Language, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<TranslationRow>> TranslationsAsync(
        NpgsqlDataSource dataSource, string term, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH anchor AS MATERIALIZED (
                SELECT coalesce(
                    public.blake3_from_bytea(public.laplace_resolve_ref($1)),
                    public.laplace_text_id($1)
                ) AS tid
            ),
            src_senses AS MATERIALIZED (
                SELECT sp.pts[1] AS src_lemma,
                       sp.pts[2] AS src_lang,
                       sp.pts[3] AS ili
                FROM anchor a,
                     public.laplace_claims_each(
                         ARRAY[a.tid], 64::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('lexical sense')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS cp) cp,
                     public.laplace_physicalities(ARRAY[cp.cp[cardinality(cp.cp)]]) ph,
                     LATERAL (SELECT public.laplace_parts(ph.path) AS pts) sp
                WHERE a.tid IS NOT NULL
                  AND cardinality(cp.cp) = 3
                  AND cardinality(sp.pts) = 3
            ),
            ili_list AS MATERIALIZED (
                SELECT ARRAY(SELECT DISTINCT ili FROM src_senses WHERE ili IS NOT NULL) AS ids
            ),
            holders AS MATERIALIZED (
                SELECT h.entity, h.path, h.rating, h.deviation, h.matches
                FROM ili_list il,
                     public.laplace_holders_with_standings(il.ids, '{}'::public.blake3[], true, -1::smallint, 512::bigint, 3::smallint) h
                WHERE cardinality(il.ids) > 0 AND NOT h.hub AND h.path IS NOT NULL
            ),
            tgt_senses AS (
                SELECT tp.pts[1] AS tgt_lemma,
                       tp.pts[2] AS tgt_lang,
                       coalesce(public.laplace_confidence(h.rating, h.deviation, 2::double precision), 0.5)::numeric AS eff_mu,
                       coalesce(h.matches, 1)::bigint AS witnesses
                FROM holders h,
                     LATERAL (SELECT public.laplace_parts(h.path) AS pts) tp
                JOIN src_senses ss ON ss.ili = tp.pts[3] AND tp.pts[2] <> ss.src_lang
                WHERE cardinality(tp.pts) = 3
            ),
            resolved AS (
                SELECT public.laplace_label_text(public.blake3_send(tgt_lemma), true) AS translation,
                       public.laplace_label_text(public.blake3_send(tgt_lang), true) AS language,
                       eff_mu,
                       witnesses
                FROM tgt_senses
            )
            SELECT DISTINCT ON (translation, language) translation, language, eff_mu, witnesses
            FROM resolved
            WHERE translation IS NOT NULL AND translation <> ''
              AND language IS NOT NULL AND language <> ''
            ORDER BY translation, language, eff_mu DESC NULLS LAST, witnesses DESC
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new TranslationRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0.5m : r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.AddWithValue(term);
                p.AddWithValue(limit);
            }, ct: ct, label: "translations", onError: onError);
    }
}
