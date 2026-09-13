CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

\set ECHO none
BEGIN;
DO $browse_query_constituents$
DECLARE
    type_t     bytea := public.laplace_hash128_blake3('Type');
    type_word  bytea := public.laplace_hash128_blake3('Word');
    type_sent  bytea := public.laplace_hash128_blake3('Sentence');
    src        bytea := public.laplace_hash128_blake3('test/browse/source');
    w_sodium   bytea := public.laplace_hash128_blake3('test/browse/word-sodium');
    w_chloride bytea := public.laplace_hash128_blake3('test/browse/word-chloride');
    sent       bytea := public.laplace_hash128_blake3('test/browse/sentence');
    t2flag     bigint := (2::bigint << 1);
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (w_sodium, 2, type_word, src),
        (w_chloride, 2, type_word, src),
        (sent, 3, type_sent, src);

    INSERT INTO laplace.physicalities
        (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents, observed_at)
    VALUES
        (public.laplace_hash128_blake3('test/browse/phys-sentence'), sent, 1,
         public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
         decode('00000000000000000000000000000000','hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(w_sodium, 1, 1, t2flag),
             public.laplace_mantissa_pack(w_chloride, 2, 1, t2flag)]),
         2, now());

    IF NOT EXISTS (
        SELECT 1
        FROM consensus.browse_named_entities(
            ARRAY[w_sodium, w_chloride], NULL, 0, 20, 0)
        WHERE entity_id = w_sodium AND match_kind = 'constituent') THEN
        RAISE EXCEPTION 'FAIL: witnessed Sodium query constituent was omitted';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM consensus.browse_named_entities(
            ARRAY[w_sodium, w_chloride], NULL, 0, 20, 0)
        WHERE entity_id = w_chloride AND match_kind = 'constituent') THEN
        RAISE EXCEPTION 'FAIL: witnessed Chloride query constituent was omitted';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM consensus.browse_named_entities(
            ARRAY[w_sodium, w_chloride], NULL, 0, 20, 0)
        WHERE entity_id = sent AND match_kind = 'contains_all') THEN
        RAISE EXCEPTION 'FAIL: shared containing composition was omitted or mislabeled';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM consensus.browse_named_entities(
            ARRAY[w_sodium, w_chloride], NULL, 0, 20, 0)
        WHERE match_kind = 'surface') THEN
        RAISE EXCEPTION 'FAIL: browse fabricated an exact surface without an exact root arm';
    END IF;

    IF (SELECT count(*)
        FROM consensus.browse_named_entities(
            ARRAY[w_sodium, w_chloride], NULL, 0, 20, 0)) <> 3 THEN
        RAISE EXCEPTION 'FAIL: browse returned an unexpected result set for constituent fallback';
    END IF;

    RAISE NOTICE 'browse query constituents: witnessed words and shared container pass';
END
$browse_query_constituents$;
ROLLBACK;
\set ECHO all
