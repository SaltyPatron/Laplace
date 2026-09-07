\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '60s';
-- converse.word_segment_resolved — word_segment, with the substrate deciding
-- where a word ends inside a run the text gave no boundary for.
--
-- UAX#29 word break joins ALetter runs, so Latin, Cyrillic, Arabic and Hangul
-- words survive whole while 4.1 puts Han, Hiragana, Katakana, Thai, Lao and
-- Khmer segmentation explicitly out of scope. Measured live 2026-08-23:
-- 自転車 (167 edges), 北京 (93), ある (81), สวัสดี (27) and 氷河 (21) all exist
-- with rated evidence and converse.word_segment reaches none of them, emitting
-- 3, 2, 2, 4 and 2 fragments. Downstream then runs one rung lower for those
-- scripts: generation.trajectory_continuations for 氷 returns 20 of 20 non-word
-- entities, against 20 of 20 words for New.
--
-- Whitespace is a real boundary and is never crossed, so "hot dog" and
-- "New York" stay tier-3 compositions of two tier-2 words rather than becoming
-- word tokens. Only byte-contiguous runs are joined; the rule names no script.
--
-- Stores nothing. Adjacency remains a view over the trajectory per the
-- 2026-07-25 ruling in relation_types.toml. See src/content_resolve.c.
-- No DROP FUNCTION here. converse.prompt_words is BEGIN ATOMIC and references this
-- body, which records a hard catalog dependency, so a DROP on the upgrade path fails
-- against the caller installed by the previous version. The signature is new and never
-- changes under replace, so CREATE OR REPLACE alone is sufficient and idempotent.
CREATE OR REPLACE FUNCTION converse.word_segment_resolved(p_text text)
    RETURNS TABLE(ord int, word text, id bytea)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_word_segment_resolved'
    LANGUAGE C STABLE;
CREATE OR REPLACE FUNCTION converse.word_segment(p_text text)
    RETURNS TABLE(ord int, word text, id bytea)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_word_segment'
    LANGUAGE C STABLE;
-- Canonical prompt identity is independent of whether or how often a case
-- variant has been witnessed. Evidence expansion belongs to typed routing.
CREATE OR REPLACE FUNCTION converse.prompt_tree(p_text text)
    RETURNS TABLE(root_id bytea, node_index int, parent_index int, tier smallint,
                  byte_offset int, byte_length int, id bytea, surface text)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_prompt_tree'
    LANGUAGE C STABLE PARALLEL SAFE;

CREATE OR REPLACE FUNCTION converse.prompt_words(p_text text)
    RETURNS TABLE(ord int, word text, id bytea)
    LANGUAGE sql LAPLACE_STABLE_STRICT
BEGIN ATOMIC
    SELECT s.ord, s.word, s.id
    FROM converse.word_segment_resolved(p_text) s
    ORDER BY s.ord;
END;
CREATE OR REPLACE FUNCTION converse.resolve_phrase(p_phrase text)
    RETURNS bytea
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_resolve_phrase'
    LANGUAGE C STABLE STRICT;
-- Shared native set access and standing reduction; input order and duplicates
-- are retained at this SQL surface. Refutations remain inspectable.
CREATE OR REPLACE FUNCTION consensus.explore_web_neighbors(
    p_subjects bytea[],
    p_types bytea[],
    p_limit int)
    RETURNS TABLE(frontier_id bytea, nbr bytea, type_id bytea, rating bigint,
                  rd bigint, witness_count bigint, outbound boolean)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_explore_web_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE;

-- Shared native set access and standing reduction; input order and duplicates
-- are retained at this SQL surface. Refutations remain inspectable.
CREATE OR REPLACE FUNCTION consensus.explore_web_neighbors(
    p_subjects bytea[],
    p_limit int)
    RETURNS TABLE(frontier_id bytea, nbr bytea, type_id bytea, rating bigint,
                  rd bigint, witness_count bigint, outbound boolean)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_explore_web_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE;
CREATE OR REPLACE FUNCTION consensus.explore_web(
    p_seeds bytea[],
    p_hops int DEFAULT 2,
    p_fanout int DEFAULT 10,
    p_max_nodes int DEFAULT NULL)
    RETURNS TABLE(
        source_id bytea,
        type_id bytea,
        object_id bytea,
        hop smallint,
        rating bigint,
        rd bigint,
        witness_count bigint)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_explore_web'
    LANGUAGE C STABLE;

-- Scalar convenience delegates to the canonical multi-seed crawl. It is not a
-- second traversal implementation.
CREATE OR REPLACE FUNCTION consensus.explore_web(
    p_seed bytea,
    p_hops int DEFAULT 2,
    p_fanout int DEFAULT 10,
    p_max_nodes int DEFAULT NULL)
    RETURNS TABLE(
        source_id bytea,
        type_id bytea,
        object_id bytea,
        hop smallint,
        rating bigint,
        rd bigint,
        witness_count bigint)
    LANGUAGE sql STABLE
BEGIN ATOMIC
    SELECT * FROM consensus.explore_web(
        ARRAY[p_seed], p_hops, p_fanout, p_max_nodes);
END;
-- generation.trajectory_continuations(ctx, topk) -> what followed this exact context, counted.
-- NULL topk returns the complete successor set; zero returns no rows.  Callers that
-- need a bounded presentation choose that bound explicitly after any downstream
-- steering, rather than losing candidates to a guessed pre-steering cutoff.
--
-- The substrate-native replacement for GenCorpus and its RAM suffix array. Same
-- operation continuations_collect performed -- find every position where the last
-- k content realize.constituents match, take what came next, count it -- scoped by an
-- index probe on the context's LAST token instead of a binary search over a
-- 97,111,658-row flattened copy of the corpus.
--
-- WHY THE CACHE WAS NEVER NEEDED: the trajectory already IS the ordered sequence
-- (§9 -- stored trajectories carry constituent identity). GenCorpus rebuilt that
-- ordering into a flat int32 stream, per backend, from a full-corpus scan --
-- measured 46 minutes for one generation.generate() call, and the same timeout at steps=1,
-- so the cost was never the walk. §7 requires cost bounded by the path, not by
-- corpus size; a per-backend whole-corpus build cannot satisfy that at any size.
--
-- SEPARATORS ARE NOT CONTEXT. The corpus stream was content-only with the
-- following separator carried beside it (GenCorpus.sep_after), so matching must
-- happen over content positions or " the king" and "the king" become different
-- contexts.
--
-- ONLY TIER 0 CAN BE A SEPARATOR, and that is the narrowing that makes this cheap.
-- Measured on this substrate: a tier-3 sentence trajectory holds tier-2 words
-- beside tier-0 atoms, because a single-character token collapses to the
-- character's own id (§3, single-child collapse). Words are never separators, so
-- classification touches only the tier-0 realize.constituents. It cannot be decided by
-- tier ALONE -- `a` and `I` are single-grapheme words that collapse to tier 0 too
-- -- so the UCD attestation still decides, via generation.separator_ids(), which owns
-- that fact for every caller.
--
-- The frame offset is a parameter, so one body serves every backoff order rather
-- than five hand-written arms.
CREATE OR REPLACE FUNCTION generation.trajectory_continuations(
        p_ctx bytea[], p_topk int DEFAULT NULL)
    RETURNS TABLE(object_id bytea, sep_id bytea, weight bigint)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_trajectory_continuations'
    LANGUAGE C STABLE;
-- generation.steer_candidates(candidates, frontier) -> S7 STEER (docs/specs/36 §3).
--
-- Re-rank S6's proposed continuations by rated consensus mass reaching the LIVE
-- frontier from S4. The frontier argument is what makes it S7 rather than a
-- prior: it is passed per emitted token, so a candidate's score depends on where
-- the walk currently stands, not on which source sentence it was gathered from.
--
-- Returns a row for EVERY candidate, including unreachable ones (steer 0.0,
-- edges 0). edges = 0 is "no attested path to the frontier"; edges > 0 with
-- steer = 0.0 is "attested and adjudicated to neutral". The caller must not
-- collapse them -- that distinction is what tells a walk to back off rather than
-- dead-end.
--
-- steer is COVERAGE-WEIGHTED, not a bare edge sum. Mass folds per DISTINCT
-- frontier member, then coverage rides as a multiplier: sum * (1 + ln(covered)).
-- A bare sum ranks by "reaches anything in the frontier"; a composed prefix
-- means "sits where the frontier overlaps". For "the capital of france is",
-- `Lyon` (one strong edge to `france`, sum 10, covered 1) scores 10 while
-- `Paris` (moderate edges to `france` AND `capital`, sum 8, covered 2) scores
-- 8 * 1.69 = 13.5 -- breadth wins without rescaling either candidate's mass.
--
-- The SUM carries through unscaled on purpose: the caller combines as
-- eff = weight * steer (trajectory_generate.c), so squashing steer through a
-- concave curve would hand the ranking to sequence weight -- a global weakening
-- of steering disguised as a coverage fix. Refuted totals are never amplified.
--
-- covered is the DISTINCT frontier members reached -- not edges, which counts
-- five edges into `france` alone the same as edges spread across the frontier.
-- Refuted (negative) member mass bypasses log1p and stays linear so it can sink
-- a candidate instead of being squashed toward zero.
--
-- NOT PARALLEL SAFE: reads consensus through SPI.
CREATE OR REPLACE FUNCTION generation.steer_candidates(
        p_candidates bytea[], p_frontier bytea[], p_relation_types bytea[])
    RETURNS TABLE(candidate bytea, steer float8, edges bigint, covered bigint)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_steer_candidates'
    LANGUAGE C VOLATILE;

CREATE OR REPLACE FUNCTION generation.steer_candidates(
        p_candidates bytea[], p_frontier bytea[])
    RETURNS TABLE(candidate bytea, steer float8, edges bigint, covered bigint)
    LANGUAGE sql VOLATILE
BEGIN ATOMIC
    SELECT * FROM generation.steer_candidates(p_candidates, p_frontier, NULL);
END;
CREATE OR REPLACE FUNCTION generation.forward_walk_continuations(
        p_ctx bytea[], p_steps int, p_max_stride int,
        p_spread float8, p_top_k int, p_seed bigint, p_frontier bytea[],
        p_relation_types bytea[])
    RETURNS TABLE(step int, entity bytea, stride_used int, sep_entity bytea)
    AS '/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_substrate/laplace_execution_4c09004a24749369', 'pg_laplace_walk_continuations'
    LANGUAGE C VOLATILE;

CREATE OR REPLACE FUNCTION generation.forward_walk_continuations(
        p_ctx bytea[], p_steps int DEFAULT 24, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_top_k int DEFAULT 10,
        p_seed bigint DEFAULT NULL, p_frontier bytea[] DEFAULT NULL)
    RETURNS TABLE(step int, entity bytea, stride_used int, sep_entity bytea)
    LANGUAGE sql VOLATILE
BEGIN ATOMIC
    SELECT * FROM generation.forward_walk_continuations(
        p_ctx, p_steps, p_max_stride, p_spread, p_top_k, p_seed, p_frontier, NULL);
END;

-- Compatibility surface: preserve the installed parameter name `p_breadth` so
-- CREATE OR REPLACE remains upgrade-safe for callers that bind by name. The
-- canonical routed operator names the same operand `p_top_k`; this wrapper only
-- translates the legacy API spelling and does not own a second implementation.
CREATE OR REPLACE FUNCTION generation.walk_continuations(
        p_ctx bytea[], p_steps int DEFAULT 24, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_breadth int DEFAULT 10,
        p_seed bigint DEFAULT NULL)
    RETURNS TABLE(step int, entity bytea, stride_used int, sep_entity bytea)
    LANGUAGE sql VOLATILE
BEGIN ATOMIC
    SELECT * FROM generation.forward_walk_continuations(
        p_ctx, p_steps, p_max_stride, p_spread, p_breadth, p_seed, NULL);
END;
CREATE OR REPLACE FUNCTION generation.forward_text(
        p_prompt text, p_steps int DEFAULT 24, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_top_k int DEFAULT 10,
        p_seed bigint DEFAULT NULL, p_hops int DEFAULT 2,
        p_fanout int DEFAULT 8, p_prior_frontier bytea[] DEFAULT NULL)
    RETURNS TABLE(step int, entity text, stride_used int)
    LANGUAGE sql VOLATILE
BEGIN ATOMIC
    -- The observation root and its complete canonical tree precede routing.
    -- No case-popularity substitution, named-English relation extraction or
    -- dictionary-membership filter is allowed to change this operand.
    WITH observation AS MATERIALIZED (
        SELECT * FROM converse.prompt_tree(p_prompt)
    ), ctx AS MATERIALIZED (
        SELECT array_agg(p.id ORDER BY p.byte_offset, p.node_index) AS ids
        FROM observation p
        WHERE p.tier = 2
    ), seed_array AS MATERIALIZED (
        SELECT ARRAY(
            SELECT n.id FROM (
                SELECT p.root_id AS id FROM observation p
                UNION
                SELECT p.id FROM observation p
            ) n ORDER BY n.id) AS ids
    ), frontier AS MATERIALIZED (
        SELECT array_agg(x.entity ORDER BY x.hop, x.entity) AS ids
        FROM (
            SELECT f.entity, f.hop
            FROM seed_array s
            CROSS JOIN LATERAL generation.forward_frontier_ids(
                s.ids, p_hops, p_fanout, NULL) f
            UNION
            SELECT p.entity, 0::smallint
            FROM unnest(COALESCE(p_prior_frontier, '{}'::bytea[])) p(entity)
            WHERE p.entity IS NOT NULL
        ) x
    ), walked AS MATERIALIZED (
        SELECT g.*
        FROM ctx CROSS JOIN frontier,
             generation.forward_walk_continuations(
                 ctx.ids, p_steps, p_max_stride, p_spread, p_top_k,
                 COALESCE(
                     p_seed,
                     laplace.hash128_lo(public.laplace_hash128_blake3(
                         convert_to(p_prompt, 'UTF8')))),
                 frontier.ids,
                 NULL::bytea[]) g
        WHERE ctx.ids IS NOT NULL
    ), ids AS MATERIALIZED (
        SELECT array_agg(entity ORDER BY step) AS entities,
               array_agg(sep_entity ORDER BY step) AS separators
        FROM walked
    ), txt AS MATERIALIZED (
        SELECT realize.render_text_batch(entities) AS entities,
               realize.render_text_batch(separators) AS separators
        FROM ids
    )
    SELECT w.step,
           t.entities[rn] || COALESCE(t.separators[rn], ''),
           w.stride_used
    FROM (SELECT w.*, row_number() OVER (ORDER BY step) AS rn FROM walked w) w
    CROSS JOIN txt t;
END;

-- Compatibility surface: preserve the installed `p_breadth` name for named
-- callers and extension upgrades. The canonical operator owns the semantic name
-- `p_top_k`; this wrapper translates only the legacy API spelling.
CREATE OR REPLACE FUNCTION generation.walk_text(
        p_prompt text, p_steps int DEFAULT 24, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_breadth int DEFAULT 10,
        p_seed bigint DEFAULT NULL)
    RETURNS TABLE(step int, entity text, stride_used int)
    LANGUAGE sql VOLATILE
BEGIN ATOMIC
    SELECT * FROM generation.forward_text(
        p_prompt, p_steps, p_max_stride, p_spread, p_breadth, p_seed, 2, 8, NULL);
END;

-- Upgrade retirement. These text routing surfaces had no callers outside the
-- recursive chain removed above. They must be dropped only AFTER forward_text is
-- rebound to forward_frontier_ids: BEGIN ATOMIC records pg_depend, so dropping
-- them earlier would make an in-place extension upgrade fail under RESTRICT.

-- The generation SEQUENCE surface, corpus-free (#728): trajectory_continuations
-- reads exact ordinal successors straight off physicalities.trajectory;
-- separators occupy their original positions; walk_continuations runs
-- S6 propose → S7 steer → S8 sample with the consensus COMPLETES_TO floor.
-- Exact identity and ordinal continuity include separators. Run boundaries
-- do not pair across sequences; the same (data, prompt, seed)
-- is deterministic, and a dead-end context continues through the floor with
-- stride_used = 0. New-era invariant replacing "probe invalidation": there is no
-- cache to invalidate — a trajectory written now is visible to the next read.


DO $$
DECLARE
    type_t    bytea := public.laplace_hash128_blake3('Type');
    type_word bytea := public.laplace_hash128_blake3('Word');
    type_sent bytea := public.laplace_hash128_blake3('Sentence');
    type_doc  bytea := public.laplace_hash128_blake3('Document');
    src       bytea := public.laplace_hash128_blake3('test/corpus/source');
    w_the     bytea := public.laplace_hash128_blake3('test/corpus/word-the');
    w_capital bytea := public.laplace_hash128_blake3('test/corpus/word-capital');
    w_of      bytea := public.laplace_hash128_blake3('test/corpus/word-of');
    w_france  bytea := public.laplace_hash128_blake3('test/corpus/word-france');
    w_end     bytea := public.laplace_hash128_blake3('test/corpus/word-end');
    w_target  bytea := public.laplace_hash128_blake3('test/corpus/word-target');
    sp        bytea := public.laplace_hash128_blake3('test/corpus/space');
    zs_cat    bytea := public.laplace_hash128_blake3('test/corpus/zs-category');
    sent      bytea := public.laplace_hash128_blake3('test/corpus/sentence');
    sent2     bytea := public.laplace_hash128_blake3('test/corpus/sentence2');
    sent3     bytea := public.laplace_hash128_blake3('test/corpus/sentence3');
    doc       bytea := public.laplace_hash128_blake3('test/corpus/document');
    t2flag    bigint := (2::bigint << 1);
    n bigint;
BEGIN
    -- Compositional entities carry the type the compose path stamps
    -- (TextEntityBuilder.TierTypeId / content_witness_batch.c), NOT a single generic
    -- 'Type'. The generation lane selects roles by type_id, because tier cannot: tier 2
    -- also holds relation types, trust classes, POS tags, sources and languages, and a
    -- single-grapheme word collapses to tier 0. A fixture that typed everything the same
    -- could not tell a correct role predicate from a broken one.
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (w_the, 2, type_word, src), (w_capital, 2, type_word, src),
        (w_of, 2, type_word, src), (w_france, 2, type_word, src),
        (w_end, 2, type_word, src), (w_target, 2, type_word, src),
        (sp, 2, type_word, src), (zs_cat, 0, type_t, src),
        (sent, 3, type_sent, src), (sent2, 3, type_sent, src), (doc, 4, type_doc, src);

    -- Separator-ness is an ATTESTED UCD fact, never a render: the fixture
    -- declares its space exactly the way the Unicode seed does —
    -- HAS_GENERAL_CATEGORY → Zs — and generation.separator_ids() resolves it.
    INSERT INTO laplace.canonical_names (id, name)
    VALUES (zs_cat, 'unicode/category/Zs/v1');
    INSERT INTO laplace.attestations (id, subject_id, type_id, object_id, source_id,
                              context_id, outcome, last_observed_at, observation_count,
                              sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES (public.laplace_hash128_blake3('test/corpus/att-sp-zs'), sp,
            laplace.relation_type_id('HAS_GENERAL_CATEGORY'), zs_cat, src,
            NULL, 2, now(), 1, 1000000000, 30000000000);

    IF NOT (sp = ANY (generation.separator_ids())) THEN
        RAISE EXCEPTION 'FAIL: attested Zs token not in generation.separator_ids()';
    END IF;

    -- sent = the ␣ capital ␣ of ␣ france
    INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (public.laplace_hash128_blake3('test/corpus/phys-sentence'), sent, 1,
            public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
            decode('00000000000000000000000000000000','hex'),
            public.ST_MakeLine(ARRAY[
                public.laplace_mantissa_pack(w_the, 1, 1, t2flag),
                public.laplace_mantissa_pack(sp, 2, 1, t2flag),
                public.laplace_mantissa_pack(w_capital, 3, 1, t2flag),
                public.laplace_mantissa_pack(sp, 4, 1, t2flag),
                public.laplace_mantissa_pack(w_of, 5, 1, t2flag),
                public.laplace_mantissa_pack(sp, 6, 1, t2flag),
                public.laplace_mantissa_pack(w_france, 7, 1, t2flag)]),
            7, now());

    -- Multi-identity containment is one shared GIN probe.  It returns the
    -- composition carrying both points, not a manufactured capital->of edge.
    IF NOT EXISTS (
        SELECT 1
        FROM structural.containers_containing_all(ARRAY[w_capital, w_of]) c
        WHERE c.entity_id = sent) THEN
        RAISE EXCEPTION 'FAIL: shared container for capital/of missing';
    END IF;
    IF EXISTS (
        SELECT 1 FROM structural.containers_containing_all(ARRAY[]::bytea[])) THEN
        RAISE EXCEPTION 'FAIL: empty shared-container set scanned physicalities';
    END IF;

    -- doc wraps sent: contributes no pairs of its own and never double-counts.
    INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (public.laplace_hash128_blake3('test/corpus/phys-doc'), doc, 1,
            public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
            decode('00000000000000000000000000000000','hex'),
            public.ST_MakeLine(ARRAY[
                public.laplace_mantissa_pack(sent, 1, 2, (3::bigint << 1))]),
            1, now());

    -- sent2 = france ␣ end
    INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (public.laplace_hash128_blake3('test/corpus/phys-sentence2'), sent2, 1,
            public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
            decode('00000000000000000000000000000000','hex'),
            public.ST_MakeLine(ARRAY[
                public.laplace_mantissa_pack(w_france, 1, 1, t2flag),
                public.laplace_mantissa_pack(sp, 2, 1, t2flag),
                public.laplace_mantissa_pack(w_end, 3, 1, t2flag)]),
            3, now());

    -- S6 preserves each exact constituent, including the intervening space.
    IF NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_the], 8) t
        WHERE t.object_id = sp AND t.weight = 1 AND t.sep_id IS NULL) THEN
        RAISE EXCEPTION 'FAIL: exact (the→space) continuation missing';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_the, sp], 8) t
        WHERE t.object_id = w_capital AND t.weight = 1 AND t.sep_id IS NULL) THEN
        RAISE EXCEPTION 'FAIL: exact (the,space→capital) continuation missing';
    END IF;

    -- A trajectory ending at france cannot pair with the next physical root.
    IF NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_france, sp], 8) t
        WHERE t.object_id = w_end AND t.weight = 1) THEN
        RAISE EXCEPTION 'FAIL: within-sequence continuation missing';
    END IF;
    IF (SELECT count(*) FROM generation.trajectory_continuations(ARRAY[w_france], 8)) <> 1 THEN
        RAISE EXCEPTION 'FAIL: cross-sequence pair leaked through a run boundary';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_capital, sp, w_of, sp], 8) t
        WHERE t.object_id = w_france AND t.weight = 1) THEN
        RAISE EXCEPTION 'FAIL: exact four-constituent context missing';
    END IF;
    IF EXISTS (SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_capital, w_of], 8)) THEN
        RAISE EXCEPTION 'FAIL: context crossed an omitted separator';
    END IF;

    -- Export plane, computed inline from the trajectories: of→france is P=1.0.
    IF NOT EXISTS (
        SELECT 1 FROM generation.relation_plane('traj', 'next') p
        WHERE p.subject_id = w_of AND p.object_id = w_france AND p.w = 1.0) THEN
        RAISE EXCEPTION 'FAIL: relation_plane traj next (of→france) missing or not P=1.0';
    END IF;

    -- S8 determinism: same (data, prompt, seed) → identical stream.
    IF EXISTS (
        SELECT 1 FROM (
            SELECT g1.step, g1.entity AS t1, g2.entity AS t2
            FROM generation.walk_continuations(ARRAY[w_the], 6, 3, 0.7, 4, 42) g1
            JOIN generation.walk_continuations(ARRAY[w_the], 6, 3, 0.7, 4, 42) g2 USING (step)
        ) z WHERE z.t1 <> z.t2) THEN
        RAISE EXCEPTION 'FAIL: same (data, prompt, seed) produced different streams';
    END IF;

    -- Proposal context and semantic frontier are independent operands. A routed
    -- frontier must not be appended to the ordered suffix used by S6.
    IF EXISTS (
        SELECT 1 FROM (
            SELECT g1.step, g1.entity AS t1, g2.entity AS t2
            FROM generation.forward_walk_continuations(
                     ARRAY[w_the], 6, 3, 0.7, 4, 42,
                     ARRAY[w_the, w_capital]) g1
            JOIN generation.forward_walk_continuations(
                     ARRAY[w_the], 6, 3, 0.7, 4, 42,
                     ARRAY[w_the, w_capital]) g2 USING (step)
        ) z WHERE z.t1 <> z.t2) THEN
        RAISE EXCEPTION 'FAIL: routed live frontier is not deterministic';
    END IF;

    -- Consensus floor: a dead-end context continues through COMPLETES_TO with
    -- stride_used = 0.
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
    VALUES (laplace.relation_type_id('COMPLETES_TO'), 0, laplace.entity_type_id('RelationType'), src)
    ON CONFLICT (id, tier) DO NOTHING;
    INSERT INTO laplace.consensus (id, subject_id, type_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    VALUES (laplace.consensus_id(w_end, laplace.relation_type_id('COMPLETES_TO'), w_target),
            w_end, laplace.relation_type_id('COMPLETES_TO'), w_target,
            2000000000000, 100000000000, 60000000, 3, now());
    SELECT count(*) INTO n
    FROM generation.walk_continuations(ARRAY[w_end], 1, 3, 0.1, 4, 7) g
    WHERE g.stride_used = 0 AND g.entity = w_target;
    IF n <> 1 THEN
        RAISE EXCEPTION 'FAIL: dead-end context did not continue through the consensus floor (stride_used=0)';
    END IF;

    -- Foundry traversal reads one whole frontier per hop. Its budget bounds only
    -- output; zero hops/fanout preserve the seed without secretly expanding.
    INSERT INTO laplace.consensus (id, subject_id, type_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    VALUES
        (laplace.consensus_id(w_the, laplace.relation_type_id('COMPLETES_TO'), w_capital),
         w_the, laplace.relation_type_id('COMPLETES_TO'), w_capital,
         1900000000000, 50000000000, 60000000, 3, now()),
        (laplace.consensus_id(w_the, laplace.relation_type_id('COMPLETES_TO'), w_of),
         w_the, laplace.relation_type_id('COMPLETES_TO'), w_of,
         1800000000000, 50000000000, 60000000, 3, now()),
        (laplace.consensus_id(w_capital, laplace.relation_type_id('COMPLETES_TO'), w_france),
         w_capital, laplace.relation_type_id('COMPLETES_TO'), w_france,
         1900000000000, 50000000000, 60000000, 3, now());
    IF (SELECT count(*) FROM generation.foundry_crawl(ARRAY[w_the], 1, 2, 2, NULL)) <> 1 THEN
        RAISE EXCEPTION 'FAIL: foundry output budget is not exact';
    END IF;
    IF (SELECT count(*) FROM generation.foundry_crawl(ARRAY[w_the], 8, 0, 2, NULL)) <> 1
       OR (SELECT count(*) FROM generation.foundry_crawl(ARRAY[w_the], 8, 2, 0, NULL)) <> 1 THEN
        RAISE EXCEPTION 'FAIL: zero foundry hops/fanout did not preserve seed-only semantics';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM generation.foundry_crawl(ARRAY[w_the], 8, 2, 2, NULL) f
        WHERE f.entity_id = w_france) THEN
        RAISE EXCEPTION 'FAIL: batched foundry frontier did not reach the second hop';
    END IF;

    -- No cache, no invalidation: a trajectory written NOW is visible to the very
    -- next read. sent3 repeats capital of twice (no separator): every matching
    -- occurrence counts, so the new unseparated capital→of has weight 2.
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
    VALUES (sent3, 3, type_sent, src);
    INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (public.laplace_hash128_blake3('test/corpus/phys-sentence3'), sent3, 1,
            public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
            decode('00000000000000000000000000000000','hex'),
            public.ST_MakeLine(ARRAY[
                public.laplace_mantissa_pack(w_capital, 1, 1, t2flag),
                public.laplace_mantissa_pack(w_of, 2, 1, t2flag),
                public.laplace_mantissa_pack(w_capital, 3, 1, t2flag),
                public.laplace_mantissa_pack(w_of, 4, 1, t2flag)]),
            4, now());
    IF NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_capital], 8) t
        WHERE t.object_id = w_of AND t.weight = 2) THEN
        RAISE EXCEPTION 'FAIL: repeated matches or new trajectory missing (capital→of should be weight 2)';
    END IF;

    -- Two physical observations may attest the same entity. geometry_successors
    -- must keep their trajectories separate rather than interleaving equal
    -- ordinals under entity_id. Together with sent and sent3 this makes three
    -- containers whose first content successor/predecessor is capital↔of.
    INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (public.laplace_hash128_blake3('test/corpus/phys-sentence3-observation2'), sent3, 1,
            public.ST_SetSRID(public.ST_MakePoint(2,2,2,2), 0),
            decode('00000000000000000000000000000000','hex'),
            public.ST_MakeLine(ARRAY[
                public.laplace_mantissa_pack(w_capital, 1, 1, t2flag),
                public.laplace_mantissa_pack(sp, 2, 1, t2flag),
                public.laplace_mantissa_pack(w_of, 3, 1, t2flag)]),
            3, now());
    IF NOT EXISTS (
        SELECT 1 FROM structural.geometry_successors(w_capital, 8, 8, false) g
        WHERE g.successor_id = w_of AND g.seen = 3) THEN
        RAISE EXCEPTION 'FAIL: geometry successor did not count three physical observations separately';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM structural.geometry_successors(w_of, 8, 8, true) g
        WHERE g.successor_id = w_capital AND g.seen = 3) THEN
        RAISE EXCEPTION 'FAIL: geometry predecessor did not count three physical observations separately';
    END IF;
    IF EXISTS (
        SELECT 1 FROM structural.geometry_successors(w_capital, 8, 8, false) g
        WHERE g.successor_id = sp) THEN
        RAISE EXCEPTION 'FAIL: geometry successor leaked a separator';
    END IF;
    IF EXISTS (
        (SELECT b.point_id, b.successor_id, b.seen
         FROM structural.geometry_successors_batch(ARRAY[w_capital, w_of], 8, 8, false) b
         EXCEPT
         SELECT p.id, g.successor_id, g.seen
         FROM unnest(ARRAY[w_capital, w_of]) p(id)
         CROSS JOIN LATERAL structural.geometry_successors(p.id, 8, 8, false) g)
        UNION ALL
        (SELECT p.id, g.successor_id, g.seen
         FROM unnest(ARRAY[w_capital, w_of]) p(id)
         CROSS JOIN LATERAL structural.geometry_successors(p.id, 8, 8, false) g
         EXCEPT
         SELECT b.point_id, b.successor_id, b.seen
         FROM structural.geometry_successors_batch(ARRAY[w_capital, w_of], 8, 8, false) b)) THEN
        RAISE EXCEPTION 'FAIL: batched geometry successors differ from scalar results';
    END IF;
    IF EXISTS (
        (SELECT b.point_id, b.successor_id, b.seen
         FROM structural.geometry_successors_batch(ARRAY[w_capital, w_of], 8, 8, true) b
         EXCEPT
         SELECT p.id, g.successor_id, g.seen
         FROM unnest(ARRAY[w_capital, w_of]) p(id)
         CROSS JOIN LATERAL structural.geometry_successors(p.id, 8, 8, true) g)
        UNION ALL
        (SELECT p.id, g.successor_id, g.seen
         FROM unnest(ARRAY[w_capital, w_of]) p(id)
         CROSS JOIN LATERAL structural.geometry_successors(p.id, 8, 8, true) g
         EXCEPT
         SELECT b.point_id, b.successor_id, b.seen
         FROM structural.geometry_successors_batch(ARRAY[w_capital, w_of], 8, 8, true) b)) THEN
        RAISE EXCEPTION 'FAIL: batched geometry predecessors differ from scalar results';
    END IF;

    -- generation.corpus_whitespace_vocab_indices shipped with a PL/pgSQL syntax error that
    -- nothing caught, and the fix landed with no test (GH #1061). One invocation closes the
    -- gap: a plpgsql body is only parsed at first EXECUTE, so a call is the only thing that
    -- proves it compiles. Asserted on shape, not on membership -- the indices depend on which
    -- codepoints this fixture happens to stage.
    IF EXISTS (
        SELECT 1 FROM generation.corpus_whitespace_vocab_indices(ARRAY[w_capital, w_of]) i
        WHERE i.vocab_idx IS NULL OR i.vocab_idx < 1 OR i.vocab_idx > 2) THEN
        RAISE EXCEPTION 'FAIL: corpus_whitespace_vocab_indices returned an out-of-range vocab_idx';
    END IF;

    -- Vocabulary heads must not guess how many pre-render candidates will
    -- survive lexical filtering. Rendering remains set/batch based by ranked page.
    IF pg_get_functiondef('generation.corpus_word_vocab(integer,integer)'::regprocedure)
           LIKE '%LIMIT p_size * 4%'
       OR pg_get_functiondef('generation.grapheme_floor_vocab(integer,integer)'::regprocedure)
           LIKE '%LIMIT p_size * 2%'
       OR pg_get_functiondef('generation.foundry_vocab(integer,integer)'::regprocedure)
           LIKE '%LIMIT p_size * 3%'
       OR pg_get_functiondef('generation.foundry_vocab(integer,integer)'::regprocedure)
           LIKE '%LIMIT 400000%' THEN
        RAISE EXCEPTION 'FAIL: hidden vocabulary candidate multiplier/cap returned';
    END IF;
    IF pg_get_functiondef('generation.corpus_word_vocab(integer,integer)'::regprocedure)
           NOT LIKE '%render_text_batch%' THEN
        RAISE EXCEPTION 'FAIL: corpus vocabulary no longer uses set-based ranked render pages';
    END IF;
    IF EXISTS (SELECT 1 FROM generation.corpus_word_vocab(0, NULL))
       OR EXISTS (SELECT 1 FROM generation.grapheme_floor_vocab(0, 10))
       OR EXISTS (SELECT 1 FROM generation.foundry_vocab(0, NULL)) THEN
        RAISE EXCEPTION 'FAIL: zero vocabulary limit returned rows';
    END IF;
    IF EXISTS (SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_the], 0))
       OR EXISTS (SELECT 1 FROM structural.geometry_successors(w_the, 0, 8, false))
       OR EXISTS (SELECT 1 FROM structural.geometry_successors(w_the, 8, 0, false))
       OR EXISTS (SELECT 1 FROM generation.walk_continuations(ARRAY[w_the], 0, 3, 0.7, 4, 42))
       OR EXISTS (SELECT 1 FROM generation.walk_continuations(ARRAY[w_the], 6, 3, 0.7, 0, 42)) THEN
        RAISE EXCEPTION 'FAIL: zero generation capacity was promoted to a hidden default';
    END IF;

    -- RLE expansion and overlapping contexts preserve every ordinal match.
    INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index,
                                       trajectory, n_constituents, observed_at)
    VALUES (public.laplace_hash128_blake3('test/corpus/rle-overlap'), sent3, 1,
            public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
            decode('00000000000000000000000000000000','hex'),
            public.ST_MakeLine(ARRAY[
                public.laplace_mantissa_pack(w_capital, 1, 4, t2flag),
                public.laplace_mantissa_pack(w_of, 5, 1, t2flag)]),
            5, now());
    IF NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_capital, w_capital], NULL)
        WHERE object_id = w_capital AND weight = 2 AND sep_id IS NULL)
       OR NOT EXISTS (
        SELECT 1 FROM generation.trajectory_continuations(ARRAY[w_capital, w_capital], NULL)
        WHERE object_id = w_of AND weight = 1 AND sep_id IS NULL) THEN
        RAISE EXCEPTION 'FAIL: RLE context lost an overlapping ordinal successor';
    END IF;

    RAISE NOTICE '✓ generation_corpus: trajectories are the single source — exact separator ordinals, run boundaries, k-context match, seeded determinism, exact candidate steering, zero-capacity preservation, frontier-batched foundry crawl, exact batched vocabulary heads, the consensus floor (stride_used=0), and write-then-read visibility all hold with NO corpus cache';
END $$;

ROLLBACK;