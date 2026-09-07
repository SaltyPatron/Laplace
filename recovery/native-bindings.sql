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
