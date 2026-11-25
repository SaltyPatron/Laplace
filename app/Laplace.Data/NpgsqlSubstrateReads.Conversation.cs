using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct ConverseReplyRow(string Reply, decimal? EffMu, long? Witnesses);

    private static ConverseReplyRow MapConverseReply(NpgsqlDataReader r) => new(
        r.IsDBNull(0) ? "" : r.GetString(0),
        r.IsDBNull(1) ? null : r.GetDecimal(1),
        r.IsDBNull(2) ? null : r.GetInt64(2));

    public static Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlConnection conn, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH anchor AS (
                SELECT public.laplace_resolve_ref($1) AS tid
            ),
            coupled AS MATERIALIZED (
                SELECT c.entity, c.via, c.rating, c.deviation
                FROM anchor a,
                     public.laplace_couple(ARRAY[public.blake3_from_bytea(a.tid)], 32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 24) c
                WHERE a.tid IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT via FROM coupled WHERE via IS NOT NULL)) s
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   public.laplace_confidence(c.rating, c.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM coupled c
            LEFT JOIN standings s ON s.claim = c.via
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql, MapConverseReply,
            p => p.AddWithValue(prompt),
            ct: ct, label: "recall_session", onError: onError);
    }

    public static async Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlDataSource dataSource, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await RecallSessionAsync(conn, prompt, session, ct, onError).ConfigureAwait(false);
    }

    private sealed record RelationBandDefinition(int Band, string Name, double Rank, string[] Predicates);

    private static readonly RelationBandDefinition[] GovernedBands =
    [
        new(1, "definition", 1.0, [
            "definition of the synset",
            "standard definition of the synset in the ILI",
            "English definition",
            "example usage",
        ]),
        new(2, "taxonomy", 0.95, [
            "hypernym",
            "instance_hypernym",
            "hyponym",
            "instance_hyponym",
            "subclass of",
            "instance of",
            "IS_A",
        ]),
        new(3, "lexical", 0.90, [
            "lexical sense",
            "reference",
            "other form",
            "synonym",
            "similar",
            "antonym",
            "also",
            "derivation",
            "pertainym",
        ]),
        new(4, "part_whole", 0.85, [
            "mero_part",
            "holo_part",
            "mero_member",
            "holo_member",
            "mero_substance",
            "holo_substance",
            "PART_OF",
            "HAS_A",
        ]),
        new(5, "language_code", 0.80, [
            "Part1",
            "Part2b",
            "Part2t",
            "variety of language",
            "pronunciation of a form",
            "emoji name",
            "CLDR short name of sequence",
        ]),
    ];

    private static string[]? ResolveBandPredicates(int[]? bands)
    {
        if (bands is null || bands.Length == 0)
            return null;

        var preds = new List<string>();
        foreach (var b in bands)
        {
            var match = Array.Find(GovernedBands, def => def.Band == b);
            if (match is null)
                throw new ArgumentException($"Unknown relation band {b}. Valid bands are 1..5.", nameof(bands));
            preds.AddRange(match.Predicates);
        }
        return [.. preds.Distinct(StringComparer.Ordinal)];
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> RecallIntentAsync(
        NpgsqlDataSource dataSource, string shape, byte[] topic, byte[]? topic2,
        string? relationType, string? lang, byte[][]? contextIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        string normalized = (shape ?? string.Empty).Trim().ToLowerInvariant();
        string? rel = string.IsNullOrWhiteSpace(relationType) ? null : relationType.Trim();
        string? langTrimmed = string.IsNullOrWhiteSpace(lang) ? null : lang.Trim();
        byte[][]? ctx = contextIds is { Length: > 0 } ? contextIds : null;

        return normalized switch
        {
            "define" => RecallDefineAsync(dataSource, topic, langTrimmed, ctx, ct, onError),
            "describe" => RecallDescribeAsync(dataSource, topic, langTrimmed, ctx, ct, onError),
            "what_is" => RecallWhatIsAsync(dataSource, topic, langTrimmed, ctx, ct, onError),
            "is_a" => topic2 is not null
                ? RecallIsAAsync(dataSource, topic, topic2, ct, onError)
                : throw new ArgumentException("Shape 'is_a' requires topic2.", nameof(topic2)),
            "related" => rel is not null
                ? RecallRelatedAsync(dataSource, topic, topic2, rel, langTrimmed, ctx, ct, onError)
                : throw new ArgumentException("Shape 'related' requires relation_type.", nameof(relationType)),
            "reason" => topic2 is not null
                ? RecallReasonAsync(dataSource, topic, topic2, rel, ct, onError)
                : throw new ArgumentException("Shape 'reason' requires topic2.", nameof(topic2)),
            "walk" => RecallWalkAsync(dataSource, topic, rel, ctx, ct, onError),
            "complete" => RecallCompleteAsync(dataSource, topic, ctx, ct, onError),
            _ => throw new ArgumentException($"unknown shape '{shape}'", nameof(shape)),
        };
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallDefineAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? lang, byte[][]? contextIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH lang_codes AS MATERIALIZED (
                SELECT public.laplace_text_id($2) AS id
                WHERE $2 IS NOT NULL
                UNION
                SELECT CASE WHEN pts[1] = public.laplace_text_id($2) THEN pts[cardinality(pts)] ELSE pts[1] END
                FROM public.laplace_claims_each(
                    ARRAY[public.laplace_text_id($2)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 3::smallint,
                    ARRAY[public.laplace_text_id('Part1'), public.laplace_text_id('Part2b'), public.laplace_text_id('Part2t')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE $2 IS NOT NULL AND cardinality(pts) = 3
            ),
            def_rels AS MATERIALIZED (
                SELECT ARRAY[
                    public.laplace_text_id('definition of the synset'),
                    public.laplace_text_id('standard definition of the synset in the ILI'),
                    public.laplace_text_id('English definition')
                ] AS rels
            ),
            ctx_coupled AS MATERIALIZED (
                SELECT c.entity, count(*)::double precision AS hits
                FROM public.laplace_couple(
                    ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest(coalesce($3::bytea[], ARRAY[]::bytea[])) AS u(x)),
                    32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 64) c
                WHERE $3 IS NOT NULL AND cardinality($3) > 0
                GROUP BY c.entity
            ),
            topic_senses AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS sense_id,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS conf,
                       c.matches
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 48::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
            ),
            filtered_senses AS MATERIALIZED (
                SELECT ts.sense_id, ts.conf, ts.matches
                FROM topic_senses ts
                LEFT JOIN public.laplace_physicalities(ARRAY(SELECT sense_id FROM topic_senses)) ph
                  ON ph.entity = ts.sense_id
                LEFT JOIN LATERAL (SELECT public.laplace_parts(ph.path) AS spts) sp ON true
                WHERE $2 IS NULL OR (sp.spts IS NOT NULL AND cardinality(sp.spts) >= 2 AND sp.spts[2] = ANY(ARRAY(SELECT id FROM lang_codes)))
            ),
            synset_refs AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS synset_id,
                       fs.sense_id AS via_sense,
                       fs.conf, fs.matches
                FROM filtered_senses fs,
                     public.laplace_claims_each(
                         ARRAY[fs.sense_id], 8::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
                UNION ALL
                SELECT p.pts[1] AS synset_id,
                       NULL::public.blake3 AS via_sense,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS conf,
                       c.matches
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 2::smallint,
                    NULL, 2::bigint) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE $2 IS NULL AND cardinality(p.pts) = 2
            ),
            direct_defs AS (
                SELECT coalesce(public.laplace_label_text(public.blake3_send(p.pts[cardinality(p.pts)]), false),
                                encode(public.blake3_send(p.pts[cardinality(p.pts)]), 'hex')) AS reply,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       0.0::double precision AS boost
                FROM def_rels dr,
                     public.laplace_claims_each(
                         ARRAY[public.blake3_from_bytea($1)], 24::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         dr.rels) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
            ),
            synset_defs AS (
                SELECT coalesce(public.laplace_label_text(public.blake3_send(p.pts[cardinality(p.pts)]), false),
                                encode(public.blake3_send(p.pts[cardinality(p.pts)]), 'hex')) AS reply,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       coalesce(cx1.hits, 0.0) + coalesce(cx2.hits, 0.0) AS boost
                FROM def_rels dr
                CROSS JOIN synset_refs sr
                CROSS JOIN LATERAL public.laplace_claims_each(
                         ARRAY[sr.synset_id], 8::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         dr.rels) c
                CROSS JOIN LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                LEFT JOIN ctx_coupled cx1 ON cx1.entity = sr.synset_id
                LEFT JOIN ctx_coupled cx2 ON cx2.entity = sr.via_sense
                WHERE cardinality(p.pts) = 3
            ),
            combined AS (
                SELECT DISTINCT ON (reply) reply, eff_mu, witnesses, boost
                FROM (
                    SELECT * FROM direct_defs
                    UNION ALL
                    SELECT * FROM synset_defs
                ) all_defs
                WHERE reply IS NOT NULL AND reply <> ''
                ORDER BY reply, boost DESC, eff_mu DESC NULLS LAST, witnesses DESC NULLS LAST
            )
            SELECT reply, eff_mu, witnesses
            FROM combined
            ORDER BY boost DESC, eff_mu DESC NULLS LAST, witnesses DESC NULLS LAST
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)lang ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = (object?)contextIds ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_define", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallDescribeAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? lang, byte[][]? contextIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH lang_codes AS MATERIALIZED (
                SELECT public.laplace_text_id($2) AS id
                WHERE $2 IS NOT NULL
                UNION
                SELECT CASE WHEN pts[1] = public.laplace_text_id($2) THEN pts[cardinality(pts)] ELSE pts[1] END
                FROM public.laplace_claims_each(
                    ARRAY[public.laplace_text_id($2)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 3::smallint,
                    ARRAY[public.laplace_text_id('Part1'), public.laplace_text_id('Part2b'), public.laplace_text_id('Part2t')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE $2 IS NOT NULL AND cardinality(pts) = 3
            ),
            ctx_coupled AS MATERIALIZED (
                SELECT c.entity, count(*)::double precision AS hits
                FROM public.laplace_couple(
                    ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest(coalesce($3::bytea[], ARRAY[]::bytea[])) AS u(x)),
                    32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 64) c
                WHERE $3 IS NOT NULL AND cardinality($3) > 0
                GROUP BY c.entity
            ),
            senses AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS sense_id
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 24::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) c
                CROSS JOIN LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                LEFT JOIN public.laplace_physicalities(ARRAY[p.pts[cardinality(p.pts)]]) ph ON ph.entity = p.pts[cardinality(p.pts)]
                LEFT JOIN LATERAL (SELECT public.laplace_parts(ph.path) AS spts) sp ON true
                WHERE cardinality(p.pts) = 3
                  AND ($2 IS NULL OR (sp.spts IS NOT NULL AND cardinality(sp.spts) >= 2 AND sp.spts[2] = ANY(ARRAY(SELECT id FROM lang_codes))))
            ),
            synsets AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS synset_id
                FROM senses s,
                     public.laplace_claims_each(
                         ARRAY[s.sense_id], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
            ),
            defs AS (
                SELECT coalesce(public.laplace_label_text(public.blake3_send(p.pts[cardinality(p.pts)]), false),
                                encode(public.blake3_send(p.pts[cardinality(p.pts)]), 'hex')) AS reply,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       1.0::double precision + coalesce(cx.hits, 0.0) AS boost
                FROM synsets sy
                JOIN LATERAL public.laplace_claims_each(
                    ARRAY[sy.synset_id], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('definition of the synset'), public.laplace_text_id('standard definition of the synset in the ILI')]) c ON true
                CROSS JOIN LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                LEFT JOIN ctx_coupled cx ON cx.entity = sy.synset_id
                WHERE cardinality(p.pts) = 3
            ),
            direct AS (
                SELECT CASE
                           WHEN cardinality(p.pts) >= 3 THEN
                               coalesce(public.laplace_label_text(public.blake3_send(p.pts[2]), false), encode(public.blake3_send(p.pts[2]), 'hex'))
                               || ': ' ||
                               coalesce(public.laplace_label_text(public.blake3_send(p.pts[cardinality(p.pts)]), false), encode(public.blake3_send(p.pts[cardinality(p.pts)]), 'hex'))
                           ELSE coalesce(public.laplace_label_text(public.blake3_send(p.pts[cardinality(p.pts)]), false), encode(public.blake3_send(p.pts[cardinality(p.pts)]), 'hex'))
                       END AS reply,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       coalesce(cx.hits, 0.0) AS boost
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 40::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint) c
                CROSS JOIN LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                LEFT JOIN ctx_coupled cx ON cx.entity = p.pts[cardinality(p.pts)]
                WHERE cardinality(p.pts) >= 2
                  AND (
                    cardinality(p.pts) < 3
                    OR coalesce(public.laplace_label_text(public.blake3_send(p.pts[2]), false), '') !~*
                       '^\s*$|^upos$|^xpos$|^lexfile$|^deprel$|^part[ _]of[ _]speech( annotation)?$|^grammatical property category$|^variety of language$|^type of pointer that lemma|^HAS[ _]SENSE[ _]FREQUENCY$|^sense[ _]key$|^I hold$'
                  )
                  AND coalesce(public.laplace_label_text(public.blake3_send(p.pts[cardinality(p.pts)]), false), '') !~ '^I hold "'
            ),
            combined AS (
                SELECT DISTINCT ON (reply) reply, eff_mu, witnesses, boost
                FROM (SELECT * FROM defs UNION ALL SELECT * FROM direct) u
                ORDER BY reply, boost DESC, eff_mu DESC NULLS LAST, witnesses DESC NULLS LAST
            )
            SELECT reply, eff_mu, witnesses
            FROM combined
            ORDER BY boost DESC, eff_mu DESC NULLS LAST, witnesses DESC NULLS LAST
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)lang ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = (object?)contextIds ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_describe", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallWhatIsAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? lang, byte[][]? contextIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH lang_codes AS MATERIALIZED (
                SELECT public.laplace_text_id($2) AS id
                WHERE $2 IS NOT NULL
                UNION
                SELECT CASE WHEN pts[1] = public.laplace_text_id($2) THEN pts[cardinality(pts)] ELSE pts[1] END
                FROM public.laplace_claims_each(
                    ARRAY[public.laplace_text_id($2)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 3::smallint,
                    ARRAY[public.laplace_text_id('Part1'), public.laplace_text_id('Part2b'), public.laplace_text_id('Part2t')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE $2 IS NOT NULL AND cardinality(pts) = 3
            ),
            ctx_coupled AS MATERIALIZED (
                SELECT c.entity, count(*)::double precision AS hits
                FROM public.laplace_couple(
                    ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest(coalesce($3::bytea[], ARRAY[]::bytea[])) AS u(x)),
                    32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 64) c
                WHERE $3 IS NOT NULL AND cardinality($3) > 0
                GROUP BY c.entity
            ),
            hypernym_rels AS MATERIALIZED (
                SELECT ARRAY[
                    public.laplace_text_id('hypernym'),
                    public.laplace_text_id('instance_hypernym'),
                    public.laplace_text_id('subclass of'),
                    public.laplace_text_id('instance of'),
                    public.laplace_text_id('IS_A')
                ] AS rels
            ),
            senses AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS sense_id
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 24::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) c
                CROSS JOIN LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                LEFT JOIN public.laplace_physicalities(ARRAY[p.pts[cardinality(p.pts)]]) ph ON ph.entity = p.pts[cardinality(p.pts)]
                LEFT JOIN LATERAL (SELECT public.laplace_parts(ph.path) AS spts) sp ON true
                WHERE cardinality(p.pts) = 3
                  AND ($2 IS NULL OR (sp.spts IS NOT NULL AND cardinality(sp.spts) >= 2 AND sp.spts[2] = ANY(ARRAY(SELECT id FROM lang_codes))))
            ),
            synsets AS MATERIALIZED (
                SELECT public.blake3_from_bytea($1) AS root_id
                UNION
                SELECT p.pts[cardinality(p.pts)]
                FROM senses s,
                     public.laplace_claims_each(
                         ARRAY[s.sense_id], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
                LIMIT 12
            ),
            parents AS MATERIALIZED (
                SELECT ch.entity,
                       min(ch.depth) AS min_depth,
                       max(public.laplace_confidence(ch.rating, ch.deviation, 2::double precision)) AS eff_mu,
                       max(ch.matches) AS witnesses
                FROM hypernym_rels h,
                     public.laplace_chains(
                         ARRAY(SELECT root_id FROM synsets),
                         ARRAY[h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5]],
                         ARRAY[1,1,1,1,1, 2,2,2,2,2, 2,2,2,2,2, 3,3,3,3,3, 3,3,3,3,3, 3,3,3,3,3],
                         ARRAY[0,0,0,0,0, 0,0,0,0,0, 1,1,1,1,1, 0,0,0,0,0, 1,1,1,1,1, 2,2,2,2,2],
                         ARRAY[false,false,false,false,false,
                               false,false,false,false,false, false,false,false,false,false,
                               false,false,false,false,false, false,false,false,false,false, false,false,false,false,false],
                         8192::bigint) ch
                WHERE ch.entity <> ALL(ARRAY(SELECT root_id FROM synsets))
                GROUP BY ch.entity
            ),
            readable AS (
                SELECT pr.entity,
                       pr.min_depth,
                       pr.eff_mu,
                       pr.witnesses,
                       coalesce(cx.hits, 0.0) AS boost,
                       (
                           SELECT string_agg(m.lbl, ', ')
                           FROM (
                               SELECT DISTINCT coalesce(public.laplace_label_text(public.blake3_send(mp.pts[2]), false), '') AS lbl
                               FROM public.laplace_claims_each(
                                   ARRAY[pr.entity], 12::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                                   NULL, 2::bigint) mc,
                                    LATERAL (SELECT public.laplace_parts(mc.path) AS pts) mp
                               WHERE cardinality(mp.pts) = 2
                           ) m
                           WHERE m.lbl <> ''
                             AND m.lbl !~ '^i[0-9]+$'
                             AND m.lbl !~ '^(noun|verb|adj|adv)\.[a-z_]+$'
                             AND m.lbl !~ '^(NOUN|VERB|ADJ|ADV|PROPN|PRON|DET|ADP|CONJ|SCONJ|CCONJ|NUM|PART|INTJ|AUX|SYM|PUNCT|X)$'
                             AND m.lbl !~ '^I hold "'
                             AND length(m.lbl) <= 64
                       ) AS member_label,
                       coalesce(public.laplace_label_text(public.blake3_send(pr.entity), false),
                                encode(public.blake3_send(pr.entity), 'hex')) AS raw_label
                FROM parents pr
                LEFT JOIN ctx_coupled cx ON cx.entity = pr.entity
            )
            SELECT coalesce(nullif(member_label, ''), raw_label) AS reply,
                   eff_mu::numeric,
                   witnesses
            FROM readable
            ORDER BY min_depth ASC, boost DESC, eff_mu DESC NULLS LAST, witnesses DESC
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)lang ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = (object?)contextIds ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_what_is", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallIsAAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[] topic2,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH hypernym_rels AS MATERIALIZED (
                SELECT ARRAY[
                    public.laplace_text_id('hypernym'),
                    public.laplace_text_id('instance_hypernym'),
                    public.laplace_text_id('subclass of'),
                    public.laplace_text_id('instance of'),
                    public.laplace_text_id('IS_A')
                ] AS rels
            ),
            src_synsets AS MATERIALIZED (
                SELECT public.blake3_from_bytea($1) AS id
                UNION
                SELECT pr.pts[cardinality(pr.pts)]
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) cs,
                     LATERAL (SELECT public.laplace_parts(cs.path) AS pts) ps,
                     public.laplace_claims_each(
                         ARRAY[ps.pts[cardinality(ps.pts)]], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) cr,
                     LATERAL (SELECT public.laplace_parts(cr.path) AS pts) pr
                WHERE cardinality(ps.pts) = 3 AND cardinality(pr.pts) = 3
                LIMIT 10
            ),
            dst_synsets AS MATERIALIZED (
                SELECT public.blake3_from_bytea($2) AS id
                UNION
                SELECT pr.pts[cardinality(pr.pts)]
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($2)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) cs,
                     LATERAL (SELECT public.laplace_parts(cs.path) AS pts) ps,
                     public.laplace_claims_each(
                         ARRAY[ps.pts[cardinality(ps.pts)]], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) cr,
                     LATERAL (SELECT public.laplace_parts(cr.path) AS pts) pr
                WHERE cardinality(ps.pts) = 3 AND cardinality(pr.pts) = 3
                LIMIT 16
            ),
            chains AS MATERIALIZED (
                SELECT ch.root, ch.program, ch.path, ch.depth, ch.entity,
                       public.laplace_confidence(ch.rating, ch.deviation, 2::double precision) AS conf,
                       ch.matches
                FROM hypernym_rels h,
                     public.laplace_chains(
                         ARRAY(SELECT id FROM src_synsets),
                         ARRAY[h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5],
                               h.rels[1], h.rels[2], h.rels[3], h.rels[4], h.rels[5]],
                         ARRAY[1,1,1,1,1, 2,2,2,2,2, 2,2,2,2,2,
                               3,3,3,3,3, 3,3,3,3,3, 3,3,3,3,3,
                               4,4,4,4,4, 4,4,4,4,4, 4,4,4,4,4, 4,4,4,4,4,
                               5,5,5,5,5, 5,5,5,5,5, 5,5,5,5,5, 5,5,5,5,5, 5,5,5,5,5],
                         ARRAY[0,0,0,0,0, 0,0,0,0,0, 1,1,1,1,1,
                               0,0,0,0,0, 1,1,1,1,1, 2,2,2,2,2,
                               0,0,0,0,0, 1,1,1,1,1, 2,2,2,2,2, 3,3,3,3,3,
                               0,0,0,0,0, 1,1,1,1,1, 2,2,2,2,2, 3,3,3,3,3, 4,4,4,4,4],
                         ARRAY[false,false,false,false,false,
                               false,false,false,false,false, false,false,false,false,false,
                               false,false,false,false,false, false,false,false,false,false, false,false,false,false,false,
                               false,false,false,false,false, false,false,false,false,false, false,false,false,false,false, false,false,false,false,false,
                               false,false,false,false,false, false,false,false,false,false, false,false,false,false,false, false,false,false,false,false, false,false,false,false,false],
                         8192::bigint) ch
            ),
            hit_paths AS (
                SELECT DISTINCT path
                FROM chains
                WHERE entity = ANY(ARRAY(SELECT id FROM dst_synsets))
            ),
            numbered_roots AS (
                SELECT row_number() OVER () AS idx, id FROM src_synsets
            ),
            formatted AS (
                SELECT coalesce(public.laplace_label_text(public.blake3_send(nr.id), false), encode(public.blake3_send(nr.id), 'hex'))
                       || ' -> ' ||
                       string_agg(
                           coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                           ' -> ' ORDER BY c.depth) AS chain_text,
                       min(c.conf)::numeric AS eff_mu,
                       min(c.matches)::bigint AS witnesses
                FROM hit_paths hp
                JOIN chains c ON c.path = hp.path
                LEFT JOIN numbered_roots nr ON nr.idx = c.root
                GROUP BY hp.path, nr.id
            )
            SELECT DISTINCT ON (chain_text) chain_text, eff_mu, witnesses
            FROM formatted
            ORDER BY chain_text, eff_mu DESC NULLS LAST, witnesses DESC
            LIMIT 20
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic2 });
            }, ct: ct, label: "recall_intent_is_a", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallRelatedAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[]? topic2, string relationType,
        string? lang, byte[][]? contextIds, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH rel AS MATERIALIZED (
                SELECT coalesce(public.laplace_text_id($3), public.blake3_from_bytea(public.laplace_resolve_ref($3))) AS rel_id
            ),
            lang_codes AS MATERIALIZED (
                SELECT public.laplace_text_id($4) AS id
                WHERE $4 IS NOT NULL
                UNION
                SELECT CASE WHEN pts[1] = public.laplace_text_id($4) THEN pts[cardinality(pts)] ELSE pts[1] END
                FROM public.laplace_claims_each(
                    ARRAY[public.laplace_text_id($4)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 3::smallint,
                    ARRAY[public.laplace_text_id('Part1'), public.laplace_text_id('Part2b'), public.laplace_text_id('Part2t')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE $4 IS NOT NULL AND cardinality(pts) = 3
            ),
            ctx_coupled AS MATERIALIZED (
                SELECT c.entity, count(*)::double precision AS hits
                FROM public.laplace_couple(
                    ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest(coalesce($5::bytea[], ARRAY[]::bytea[])) AS u(x)),
                    32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 64) c
                WHERE $5 IS NOT NULL AND cardinality($5) > 0
                GROUP BY c.entity
            ),
            senses AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS sense_id
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 24::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) c
                CROSS JOIN LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                LEFT JOIN public.laplace_physicalities(ARRAY[p.pts[cardinality(p.pts)]]) ph ON ph.entity = p.pts[cardinality(p.pts)]
                LEFT JOIN LATERAL (SELECT public.laplace_parts(ph.path) AS spts) sp ON true
                WHERE cardinality(p.pts) = 3
                  AND ($4 IS NULL OR (sp.spts IS NOT NULL AND cardinality(sp.spts) >= 2 AND sp.spts[2] = ANY(ARRAY(SELECT id FROM lang_codes))))
            ),
            seeds AS MATERIALIZED (
                SELECT public.blake3_from_bytea($1) AS id
                UNION
                SELECT sense_id FROM senses
                UNION
                SELECT p.pts[cardinality(p.pts)]
                FROM senses s,
                     public.laplace_claims_each(
                         ARRAY[s.sense_id], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
            ),
            matched AS (
                SELECT p.pts,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses
                FROM rel r,
                     public.laplace_claims_each(
                         ARRAY(SELECT id FROM seeds),
                         48::bigint,
                         ARRAY[public.laplace_bank_bit('kind','claim')],
                         '{}'::public.blake3[],
                         1::smallint,
                         ARRAY[r.rel_id],
                         0::bigint,
                         CASE WHEN $2 IS NULL THEN NULL ELSE ARRAY[public.blake3_from_bytea($2)] END) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) >= 2
            ),
            labeled AS (
                SELECT m.pts[cardinality(m.pts)] AS obj_id,
                       coalesce(public.laplace_label_text(public.blake3_send(m.pts[cardinality(m.pts)]), false),
                                encode(public.blake3_send(m.pts[cardinality(m.pts)]), 'hex')) AS reply,
                       m.eff_mu,
                       m.witnesses,
                       coalesce(cx.hits, 0.0) AS boost
                FROM matched m
                LEFT JOIN ctx_coupled cx ON cx.entity = m.pts[cardinality(m.pts)]
            )
            SELECT DISTINCT ON (reply) reply, eff_mu, witnesses
            FROM labeled
            ORDER BY reply, boost DESC, eff_mu DESC NULLS LAST, witnesses DESC
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = (object?)topic2 ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = relationType });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)lang ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = (object?)contextIds ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_related", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallReasonAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[] topic2, string? relationType,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH mid AS MATERIALIZED (
                SELECT CASE WHEN $3 IS NULL OR $3 = '' THEN NULL ELSE ARRAY[public.laplace_text_id($3)] END AS rels
            ),
            src_synsets AS MATERIALIZED (
                SELECT public.blake3_from_bytea($1) AS id
                UNION
                (
                    SELECT pr.pts[cardinality(pr.pts)]
                    FROM public.laplace_claims_each(
                        ARRAY[public.blake3_from_bytea($1)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                        ARRAY[public.laplace_text_id('lexical sense')]) cs
                    CROSS JOIN LATERAL (SELECT public.laplace_parts(cs.path) AS pts) ps
                    CROSS JOIN LATERAL public.laplace_claims_each(
                        ARRAY[ps.pts[cardinality(ps.pts)]], 2::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                        ARRAY[public.laplace_text_id('reference')]) cr
                    CROSS JOIN LATERAL (SELECT public.laplace_parts(cr.path) AS pts) pr
                    WHERE cardinality(ps.pts) = 3 AND cardinality(pr.pts) = 3
                    LIMIT 15
                )
            ),
            dst_synsets AS MATERIALIZED (
                SELECT public.blake3_from_bytea($2) AS id
                UNION
                (
                    SELECT pr.pts[cardinality(pr.pts)]
                    FROM public.laplace_claims_each(
                        ARRAY[public.blake3_from_bytea($2)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                        ARRAY[public.laplace_text_id('lexical sense')]) cs
                    CROSS JOIN LATERAL (SELECT public.laplace_parts(cs.path) AS pts) ps
                    CROSS JOIN LATERAL public.laplace_claims_each(
                        ARRAY[ps.pts[cardinality(ps.pts)]], 2::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                        ARRAY[public.laplace_text_id('reference')]) cr
                    CROSS JOIN LATERAL (SELECT public.laplace_parts(cr.path) AS pts) pr
                    WHERE cardinality(ps.pts) = 3 AND cardinality(pr.pts) = 3
                    LIMIT 15
                )
            ),
            pulled_direct AS MATERIALIZED (
                SELECT p.chain, p.rating, p.deviation, p.claim, p.cost
                FROM mid m
                CROSS JOIN LATERAL public.laplace_pull(
                    ARRAY(SELECT id FROM src_synsets),
                    ARRAY(SELECT id FROM dst_synsets),
                    4, 64::bigint, 512::bigint, m.rels) p
                WHERE p.goal AND p.claim IS NOT NULL
            ),
            bridge_rels AS MATERIALIZED (
                SELECT CASE
                           WHEN $3 IS NOT NULL AND $3 <> '' THEN ARRAY[public.laplace_text_id($3)]
                           ELSE ARRAY[
                               public.laplace_text_id('hypernym'),
                               public.laplace_text_id('hyponym'),
                               public.laplace_text_id('instance_hypernym'),
                               public.laplace_text_id('similar'),
                               public.laplace_text_id('also')
                           ]
                       END AS rels
            ),
            pull_fwd AS MATERIALIZED (
                SELECT p.id, p.chain, p.rating, p.deviation, p.claim, p.cost
                FROM bridge_rels br
                CROSS JOIN LATERAL public.laplace_pull(
                    ARRAY(SELECT id FROM src_synsets),
                    ARRAY(SELECT id FROM dst_synsets),
                    5, 32::bigint, 512::bigint, br.rels) p
                WHERE p.claim IS NOT NULL
            ),
            pull_bwd AS MATERIALIZED (
                SELECT p.id, p.chain, p.rating, p.deviation, p.claim, p.cost
                FROM bridge_rels br
                CROSS JOIN LATERAL public.laplace_pull(
                    ARRAY(SELECT id FROM dst_synsets),
                    ARRAY(SELECT id FROM src_synsets),
                    5, 32::bigint, 512::bigint, br.rels) p
                WHERE p.claim IS NOT NULL
            ),
            pulled_meet AS MATERIALIZED (
                SELECT (
                           p1.chain || ARRAY(
                               SELECT p2.chain[i]
                               FROM generate_subscripts(p2.chain, 1) AS g(i)
                               WHERE i < cardinality(p2.chain)
                               ORDER BY i DESC
                           )
                       ) AS chain,
                       CASE
                           WHEN coalesce(p1.rating, 1500.0) <= coalesce(p2.rating, 1500.0) THEN p1.rating
                           ELSE p2.rating
                       END AS rating,
                       CASE
                           WHEN coalesce(p1.rating, 1500.0) <= coalesce(p2.rating, 1500.0) THEN p1.deviation
                           ELSE p2.deviation
                       END AS deviation,
                       p1.claim AS claim,
                       p1.cost + p2.cost AS cost
                FROM pull_fwd p1
                JOIN pull_bwd p2 ON p1.id = p2.id
            ),
            pulled AS MATERIALIZED (
                SELECT chain, rating, deviation, claim, cost FROM pulled_direct
                UNION ALL
                SELECT chain, rating, deviation, claim, cost FROM pulled_meet
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            ),
            rendered AS (
                SELECT array_to_string(ARRAY(
                           SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                           FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                       ), ' -> ') AS chain_text,
                       public.laplace_confidence(p.rating, p.deviation, 2)::numeric AS eff_mu,
                       coalesce(st.matches, 1)::bigint AS witnesses,
                       p.cost
                FROM pulled p
                LEFT JOIN standings st ON st.claim = p.claim
            ),
            dedup AS (
                SELECT DISTINCT ON (chain_text) chain_text, eff_mu, witnesses, cost
                FROM rendered
                WHERE chain_text IS NOT NULL AND chain_text <> ''
                ORDER BY chain_text, cost ASC
            )
            SELECT chain_text, eff_mu, witnesses
            FROM dedup
            ORDER BY cost ASC
            LIMIT 20
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic2 });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)relationType ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_reason", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallWalkAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? relationType, byte[][]? contextIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH mid AS MATERIALIZED (
                SELECT CASE WHEN $2 IS NULL OR $2 = '' THEN NULL ELSE ARRAY[public.laplace_text_id($2)] END AS rels
            ),
            ctx_coupled AS MATERIALIZED (
                SELECT c.entity, count(*)::double precision AS hits
                FROM public.laplace_couple(
                    ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest(coalesce($3::bytea[], ARRAY[]::bytea[])) AS u(x)),
                    32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 64) c
                WHERE $3 IS NOT NULL AND cardinality($3) > 0
                GROUP BY c.entity
            ),
            pulled AS MATERIALIZED (
                SELECT p.id, p.chain, p.rating, p.deviation, p.claim, p.hops, p.cost
                FROM mid m,
                     public.laplace_pull(
                         ARRAY[public.blake3_from_bytea($1)],
                         NULL,
                         3, 32::bigint, 256::bigint, m.rels) p
                WHERE p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   coalesce(st.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings st ON st.claim = p.claim
            LEFT JOIN ctx_coupled cx ON cx.entity = p.id
            ORDER BY coalesce(cx.hits, 0.0) DESC, p.hops ASC, p.cost ASC
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)relationType ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = (object?)contextIds ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_walk", onError: onError);
    }

    private static Task<IReadOnlyList<ConverseReplyRow>> RecallCompleteAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[][]? contextIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError)
    {
        const string sql = """
            WITH phys AS MATERIALIZED (
                SELECT public.laplace_parts(ph.path) AS pts
                FROM public.laplace_physicalities(ARRAY[public.blake3_from_bytea($1)]) ph
                LIMIT 1
            ),
            seq AS MATERIALIZED (
                SELECT ARRAY(
                    SELECT public.blake3_from_bytea(x)
                    FROM unnest(coalesce($2::bytea[], ARRAY[]::bytea[])) AS u(x)
                    WHERE x IS NOT NULL
                ) || coalesce(
                    (SELECT CASE WHEN cardinality(pts) > 0 THEN pts ELSE ARRAY[public.blake3_from_bytea($1)] END FROM phys),
                    ARRAY[public.blake3_from_bytea($1)]
                ) AS ids
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(f.next), false), encode(public.blake3_send(f.next), 'hex')),
                   f.times::numeric,
                   f.paths
            FROM seq,
                 public.laplace_forward_suffix(seq.ids, 32::bigint) f
            ORDER BY f.paths DESC NULLS LAST, f.times DESC NULLS LAST
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = (object?)contextIds ?? DBNull.Value });
            }, ct: ct, label: "recall_intent_complete", onError: onError);
    }

    public readonly record struct CompletionRow(
        string ObjectIdHex, string TypeIdHex, decimal EffectiveMu, long Witnesses, string ObjectLabel);

    public static Task<IReadOnlyList<CompletionRow>> CompletionsAsync(
        NpgsqlConnection conn, string prompt, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH tid AS (
                SELECT public.laplace_text_id($1) AS id
            ),
            phys AS (
                SELECT ph.path
                FROM tid t
                LEFT JOIN LATERAL public.laplace_physicalities(ARRAY[t.id]) ph ON t.id IS NOT NULL
            ),
            parts AS (
                SELECT coalesce(
                    public.laplace_parts(p.path),
                    CASE WHEN t.id IS NOT NULL THEN ARRAY[t.id] ELSE ARRAY[]::public.blake3[] END
                ) AS ids
                FROM tid t
                LEFT JOIN phys p ON true
            )
            SELECT encode(public.blake3_send(f.next), 'hex'),
                   repeat('0', 32),
                   coalesce(f.times, 0)::numeric,
                   coalesce(f.paths, 0)::bigint,
                   coalesce(public.laplace_label_text(public.blake3_send(f.next), false), encode(public.blake3_send(f.next), 'hex'))
            FROM parts, public.laplace_forward(parts.ids, 32::bigint) f
            ORDER BY f.paths DESC NULLS LAST, f.times DESC NULLS LAST
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new CompletionRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3), r.GetString(4)),
            p =>
            {
                p.AddWithValue(prompt);
                p.AddWithValue(limit);
            }, ct: ct, label: "completions", onError: onError);
    }

    public readonly record struct WalkBranchStepRow(
        int Depth, string[] PathHex, string[] TypePathHex, string EntityIdHex, string EntityLabel,
        decimal EffMu, decimal PathMu, long Witnesses);

    public static Task<IReadOnlyList<WalkBranchStepRow>> ExplainTraceStepsAsync(
        NpgsqlConnection conn, string prompt, int depth, int beam, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH seed AS (
                SELECT public.blake3_from_bytea(public.laplace_resolve_ref($1)) AS id
            ),
            pulled AS MATERIALIZED (
                SELECT p.hops, p.chain, p.claims, p.id, p.rating, p.deviation, p.cost
                FROM seed s,
                     public.laplace_pull(ARRAY[s.id], NULL, greatest(1, $2), greatest(1, $3)::bigint, 512::bigint) p
                WHERE s.id IS NOT NULL
                  AND p.claims IS NOT NULL
                  AND cardinality(p.claims) > 0
                  AND p.claims[cardinality(p.claims)] IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claims[cardinality(claims)] FROM pulled)) s
            )
            SELECT p.hops,
                   ARRAY(SELECT encode(public.blake3_send(x), 'hex') FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x),
                   ARRAY(SELECT encode(public.blake3_send(x), 'hex') FROM unnest(coalesce(p.claims, ARRAY[]::public.blake3[])) AS x),
                   encode(public.blake3_send(p.id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(p.id), false), encode(public.blake3_send(p.id), 'hex')),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   p.cost::numeric,
                   coalesce(st.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings st ON st.claim = p.claims[cardinality(p.claims)]
            ORDER BY p.hops, p.cost
            LIMIT greatest(1, $3) * 8
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new WalkBranchStepRow(
                r.GetInt32(0), r.GetFieldValue<string[]>(1), r.GetFieldValue<string[]>(2),
                r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetInt64(7)),
            p =>
            {
                p.AddWithValue(prompt);
                p.AddWithValue(depth);
                p.AddWithValue(beam);
            }, ct: ct, label: "explain_trace_steps", onError: onError);
    }

    public readonly record struct WalkBranchRow(
        int Depth, string Path, decimal EffMu, decimal PathMu, long Witnesses);

    public static Task<IReadOnlyList<WalkBranchRow>> WalkBranchesAsync(
        NpgsqlDataSource dataSource, string? prompt, string? entityHex, string? relationType,
        int depth, int breadth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH seed AS (
                SELECT CASE
                    WHEN $2 IS NOT NULL AND length($2) = 32 THEN public.blake3_from_bytea(decode($2, 'hex'))
                    WHEN $1 IS NOT NULL THEN public.blake3_from_bytea(public.laplace_resolve_ref($1))
                    ELSE NULL
                END AS id
            ),
            mid AS (
                SELECT CASE WHEN $3 IS NULL OR $3 = '' THEN NULL ELSE ARRAY[public.laplace_text_id($3)] END AS rels
            ),
            pulled AS MATERIALIZED (
                SELECT p.hops, p.chain, p.rating, p.deviation, p.cost, p.claim
                FROM seed s, mid m,
                     public.laplace_pull(ARRAY[s.id], NULL, greatest(1, $4), greatest(1, $5)::bigint, 512::bigint, m.rels) p
                WHERE s.id IS NOT NULL AND p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT p.hops,
                   array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   p.cost::numeric,
                   coalesce(st.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings st ON st.claim = p.claim
            ORDER BY p.hops, p.cost
            LIMIT 100
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new WalkBranchRow(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)prompt ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entityHex ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)relationType ?? DBNull.Value });
                p.AddWithValue(depth);
                p.AddWithValue(breadth);
            }, ct: ct, label: "walk_branches", onError: onError);
    }

    public readonly record struct QueryShapeRow(
        string Shape, string Summary, bool NeedsTopic2, bool NeedsType, bool AcceptsLang);

    public static Task<IReadOnlyList<QueryShapeRow>> QueryShapesAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        IReadOnlyList<QueryShapeRow> rows =
        [
            new("define", "Definitional assertions for a topic", false, false, true),
            new("describe", "Definitions and highest-standing assertions for a topic", false, false, true),
            new("what_is", "Taxonomic supertype (hypernym) assertions", false, false, true),
            new("related", "Rated assertions filtered by relation type", false, true, true),
            new("is_a", "Test taxonomic hypernym chain between two topics", true, false, false),
            new("reason", "Shortest rated chain between two topics", true, false, false),
            new("walk", "Best-first rated beam walk from a topic", false, false, false),
            new("complete", "Forward trajectory suffix continuation", false, false, false),
        ];
        return Task.FromResult(rows);
    }

    public readonly record struct RelationBandRow(
        int Band, string Name, double Rank, long RelationTypes, long ConsensusRows);

    public static Task<IReadOnlyList<RelationBandRow>> RelationBandsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var bandNums = new List<int>();
        var bandNames = new List<string>();
        var bandRanks = new List<double>();
        var predicates = new List<string>();
        var predCounts = new List<long>();

        foreach (var def in GovernedBands)
        {
            foreach (var pred in def.Predicates)
            {
                bandNums.Add(def.Band);
                bandNames.Add(def.Name);
                bandRanks.Add(def.Rank);
                predicates.Add(pred);
                predCounts.Add(def.Predicates.Length);
            }
        }

        const string sql = """
            WITH catalog AS (
                SELECT band, name, rank, pred_cnt, public.laplace_text_id(pred) AS pid
                FROM unnest($1::int4[], $2::text[], $3::float8[], $4::text[], $5::int8[])
                     AS t(band, name, rank, pred, pred_cnt)
            ),
            counts AS (
                SELECT c.band, c.name, c.rank, max(c.pred_cnt) AS relation_types,
                       coalesce(sum(hc.claims + hc.observations), 0)::bigint AS consensus_rows
                FROM catalog c
                LEFT JOIN LATERAL public.laplace_holder_counts(ARRAY[c.pid], -1::smallint, 3::smallint) hc ON true
                GROUP BY c.band, c.name, c.rank
            )
            SELECT band, name, rank, relation_types, consensus_rows
            FROM counts
            ORDER BY band ASC
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new RelationBandRow(r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer, Value = bandNums.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = bandNames.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double, Value = bandRanks.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = predicates.ToArray() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = predCounts.ToArray() });
            },
            ct: ct, label: "relation_bands", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> BandFactsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int[]? bands, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var predicates = ResolveBandPredicates(bands);
        const string sql = """
            WITH mid AS MATERIALIZED (
                SELECT CASE
                    WHEN $2 IS NULL OR cardinality($2) = 0 THEN NULL
                    ELSE ARRAY(SELECT public.laplace_text_id(p) FROM unnest($2::text[]) AS p)
                END AS rels
            ),
            senses AS MATERIALIZED (
                SELECT p.pts[cardinality(p.pts)] AS sense_id
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 16::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                    ARRAY[public.laplace_text_id('lexical sense')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) p
                WHERE cardinality(p.pts) = 3
            ),
            seeds AS MATERIALIZED (
                SELECT public.blake3_from_bytea($1) AS id
                UNION
                SELECT pr.pts[cardinality(pr.pts)]
                FROM senses s,
                     public.laplace_claims_each(
                         ARRAY[s.sense_id], 4::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint,
                         ARRAY[public.laplace_text_id('reference')]) c,
                     LATERAL (SELECT public.laplace_parts(c.path) AS pts) pr
                WHERE cardinality(pr.pts) = 3
                LIMIT 10
            ),
            claims AS (
                SELECT public.laplace_parts(c.path) AS pts,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses
                FROM mid m,
                     public.laplace_claims_each(
                         ARRAY(SELECT id FROM seeds),
                         greatest(8, $3 * 2)::bigint,
                         ARRAY[public.laplace_bank_bit('kind','claim')],
                         '{}'::public.blake3[],
                         3::smallint,
                         m.rels) c
            ),
            formatted AS (
                SELECT CASE
                           WHEN pts[1] = ANY(ARRAY(SELECT id FROM seeds)) AND cardinality(pts) >= 3 THEN
                               coalesce(public.laplace_label_text(public.blake3_send(pts[2]), false), encode(public.blake3_send(pts[2]), 'hex'))
                               || ': ' ||
                               coalesce(public.laplace_label_text(public.blake3_send(pts[cardinality(pts)]), false), encode(public.blake3_send(pts[cardinality(pts)]), 'hex'))
                           WHEN cardinality(pts) >= 3 THEN
                               coalesce(public.laplace_label_text(public.blake3_send(pts[1]), false), encode(public.blake3_send(pts[1]), 'hex'))
                               || ' [' || coalesce(public.laplace_label_text(public.blake3_send(pts[2]), false), encode(public.blake3_send(pts[2]), 'hex')) || ']'
                           ELSE
                               coalesce(public.laplace_label_text(public.blake3_send(pts[cardinality(pts)]), false), encode(public.blake3_send(pts[cardinality(pts)]), 'hex'))
                       END AS reply,
                       eff_mu,
                       witnesses
                FROM claims
                WHERE cardinality(pts) >= 2
            )
            SELECT DISTINCT ON (reply) reply, eff_mu, witnesses
            FROM formatted
            ORDER BY reply, eff_mu DESC NULLS LAST, witnesses DESC
            LIMIT greatest(0, $3)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = (object?)predicates ?? DBNull.Value });
                p.AddWithValue(limit);
            }, ct: ct, label: "band_facts", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> WalkStrongestAsync(
        NpgsqlDataSource dataSource, byte[] topic, int depth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        WalkBranchesBeamAsync(dataSource, topic, null, null, depth, 8, 20, ct, onError);

    public static Task<IReadOnlyList<ConverseReplyRow>> WalkBranchesBeamAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? relationType, int[]? bands,
        int depth, int breadth, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var bandPredicates = ResolveBandPredicates(bands);
        List<string> relFilter = [];
        if (!string.IsNullOrWhiteSpace(relationType))
            relFilter.Add(relationType.Trim());
        if (bandPredicates is not null)
            relFilter.AddRange(bandPredicates);
        string[]? middleNames = relFilter.Count == 0 ? null : [.. relFilter.Distinct(StringComparer.Ordinal)];

        const string sql = """
            WITH mid AS (
                SELECT CASE
                    WHEN $2 IS NULL OR cardinality($2) = 0 THEN NULL
                    ELSE ARRAY(SELECT public.laplace_text_id(p) FROM unnest($2::text[]) AS p)
                END AS rels
            ),
            pulled AS MATERIALIZED (
                SELECT p.chain, p.rating, p.deviation, p.claim
                FROM mid m,
                     public.laplace_pull(ARRAY[public.blake3_from_bytea($1)], NULL, greatest(1, $3), greatest(1, $4)::bigint, 512::bigint, m.rels) p
                WHERE p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings s ON s.claim = p.claim
            ORDER BY public.laplace_confidence(p.rating, p.deviation, 2::double precision) DESC NULLS LAST
            LIMIT greatest(0, $5)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = (object?)middleNames ?? DBNull.Value });
                p.AddWithValue(depth);
                p.AddWithValue(breadth);
                p.AddWithValue(limit);
            }, ct: ct, label: "walk_branches_beam", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> AstarPathAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[] topic2, int depth,
        bool directed, bool useGeometry, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH pulled AS MATERIALIZED (
                SELECT p.id, p.chain, p.claims, p.rating, p.deviation, p.claim, p.cost
                FROM public.laplace_pull(
                    ARRAY[public.blake3_from_bytea($1)],
                    ARRAY[public.blake3_from_bytea($2)],
                    greatest(1, $3), 64::bigint, 1024::bigint) p
                WHERE p.goal AND p.claim IS NOT NULL
            ),
            checked AS MATERIALIZED (
                SELECT p.id, p.chain, p.rating, p.deviation, p.claim, p.cost
                FROM pulled p
                WHERE NOT $4
                   OR (
                       cardinality(p.claims) > 0
                       AND NOT EXISTS (
                           SELECT 1
                           FROM unnest(p.claims) WITH ORDINALITY AS cl(cid, ord)
                           JOIN public.laplace_physicalities(ARRAY[cl.cid]) ph ON ph.entity = cl.cid
                           WHERE (public.laplace_parts(ph.path))[1] <> p.chain[cl.ord]
                       )
                   )
            ),
            goal_coord AS MATERIALIZED (
                SELECT c.x, c.y, c.z, c.m
                FROM public.laplace_entity_coords(ARRAY[public.blake3_from_bytea($2)]) c
                WHERE $5
                LIMIT 1
            ),
            end_coords AS MATERIALIZED (
                SELECT c.id, c.x, c.y, c.z, c.m
                FROM public.laplace_entity_coords(ARRAY(SELECT DISTINCT id FROM checked)) c
                WHERE $5
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM checked WHERE claim IS NOT NULL)) s
            )
            SELECT array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(ch.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(ch.rating, ch.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM checked ch
            LEFT JOIN standings s ON s.claim = ch.claim
            LEFT JOIN end_coords ec ON ec.id = ch.id
            LEFT JOIN goal_coord gc ON true
            ORDER BY CASE
                WHEN $5 AND ec.id IS NOT NULL AND gc.x IS NOT NULL THEN
                    ch.cost + 0.1 * sqrt(
                        power(ec.x - gc.x, 2) + power(ec.y - gc.y, 2) +
                        power(ec.z - gc.z, 2) + power(ec.m - gc.m, 2))
                ELSE ch.cost
            END ASC
            LIMIT 20
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic2 });
                p.AddWithValue(depth);
                p.AddWithValue(directed);
                p.AddWithValue(useGeometry);
            }, ct: ct, label: "astar_path", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> WalkContinuationsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int steps, int maxStride,
        double spread, int breadth, long? seed, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH phys AS MATERIALIZED (
                SELECT public.laplace_parts(ph.path) AS pts
                FROM public.laplace_physicalities(ARRAY[public.blake3_from_bytea($1)]) ph
                LIMIT 1
            ),
            parts AS MATERIALIZED (
                SELECT coalesce(
                    (SELECT CASE
                        WHEN cardinality(pts) = 0 THEN ARRAY[public.blake3_from_bytea($1)]
                        WHEN $3 > 0 AND cardinality(pts) > $3 THEN pts[(cardinality(pts) - $3 + 1):cardinality(pts)]
                        ELSE pts
                    END FROM phys),
                    ARRAY[public.blake3_from_bytea($1)]
                ) AS ids
            ),
            fwd AS MATERIALIZED (
                SELECT f.next, f.times, f.paths
                FROM parts p,
                     public.laplace_forward_suffix(p.ids, greatest(1, $5)::bigint) f
            ),
            mx AS MATERIALIZED (
                SELECT max(times) AS max_t FROM fwd
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(f.next), false), encode(public.blake3_send(f.next), 'hex')),
                   f.times::numeric,
                   f.paths
            FROM fwd f, mx
            WHERE mx.max_t IS NULL OR f.times >= mx.max_t * (1.0 - least(1.0, greatest(0.0, $4)))
            ORDER BY f.paths DESC NULLS LAST,
                     f.times DESC NULLS LAST,
                     CASE WHEN $6 IS NULL THEN 0 ELSE hashtextextended(encode(public.blake3_send(f.next), 'hex'), $6) END
            LIMIT greatest(1, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.AddWithValue(Math.Max(1, steps));
                p.AddWithValue(maxStride);
                p.AddWithValue(spread);
                p.AddWithValue(Math.Max(1, breadth));
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)seed ?? DBNull.Value });
            }, ct: ct, label: "walk_continuations", onError: onError);
    }
}
