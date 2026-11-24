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
        const string sql = """
            WITH hypernym AS (
                SELECT ARRAY[
                    public.laplace_text_id('IS_A'),
                    public.laplace_text_id('hypernym'),
                    public.laplace_text_id('instance hypernym'),
                    public.laplace_text_id('is a'),
                    public.laplace_text_id('subclass of'),
                    public.laplace_text_id('instance of')
                ] AS rels
            ),
            self_row AS (
                SELECT 'self'::text AS dir, 0 AS ord, encode($1, 'hex') AS id_hex,
                       coalesce(public.laplace_label_text($1, false), encode($1, 'hex')) AS label,
                       NULL::numeric AS eff_mu
            ),
            up_rows AS (
                SELECT 'up'::text AS dir, p.hops AS ord,
                       encode(public.blake3_send(p.id), 'hex') AS id_hex,
                       coalesce(public.laplace_label_text(public.blake3_send(p.id), false), encode(public.blake3_send(p.id), 'hex')) AS label,
                       public.laplace_confidence(p.rating, p.deviation, 2::double precision)::numeric AS eff_mu
                FROM hypernym h,
                     public.laplace_pull(ARRAY[public.blake3_from_bytea($1)], NULL, greatest(1, $2), greatest(1, $3)::bigint, 256::bigint, h.rels) p
            ),
            down_claims AS (
                SELECT row_number() OVER (
                    ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC NULLS LAST, c.matches DESC
                )::int AS ord,
                       public.laplace_parts(c.path) AS pts,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu
                FROM hypernym h,
                     public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $3)::bigint, NULL, '{}'::public.blake3[], 2::smallint, h.rels) c
                LIMIT greatest(0, $3)
            ),
            down_rows AS (
                SELECT 'down'::text AS dir, d.ord,
                       encode(public.blake3_send(d.pts[1]), 'hex') AS id_hex,
                       coalesce(public.laplace_label_text(public.blake3_send(d.pts[1]), false), encode(public.blake3_send(d.pts[1]), 'hex')) AS label,
                       d.eff_mu
                FROM down_claims d
                WHERE cardinality(d.pts) >= 2 AND d.pts[1] <> public.blake3_from_bytea($1)
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
                p.AddWithValue(RequestedLimit(depth));
                p.AddWithValue(RequestedLimit(childLimit));
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
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, NULL, '{}'::public.blake3[], 2::smallint) c
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
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], 24::bigint, NULL, '{}'::public.blake3[], 1::smallint) c
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
            WITH anchor AS (
                SELECT public.laplace_resolve_ref($1) AS tid
            ),
            rel AS (
                SELECT ARRAY[
                    public.laplace_text_id('translation_of'),
                    public.laplace_text_id('has_translation'),
                    public.laplace_text_id('cili'),
                    public.laplace_text_id('synonym'),
                    public.laplace_text_id('equivalent')
                ] AS rels
            ),
            pulled AS MATERIALIZED (
                SELECT p.id, p.rating, p.deviation, p.claim
                FROM anchor a, rel r,
                     public.laplace_pull(ARRAY[public.blake3_from_bytea(a.tid)], NULL, 2, greatest(1, $2)::bigint, 256::bigint, r.rels) p
                WHERE a.tid IS NOT NULL AND p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(p.id), false), encode(public.blake3_send(p.id), 'hex')),
                   ''::text,
                   public.laplace_confidence(p.rating, p.deviation, 2::double precision)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings s ON s.claim = p.claim
            ORDER BY public.laplace_confidence(p.rating, p.deviation, 2::double precision) DESC NULLS LAST
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new TranslationRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.AddWithValue(term);
                p.AddWithValue(limit);
            }, ct: ct, label: "translations", onError: onError);
    }
}
