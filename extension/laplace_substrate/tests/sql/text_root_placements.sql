-- Native text placement preserves canonical Unicode content and input occurrence.
\set ECHO none
BEGIN;

CREATE TEMP TABLE placement_input(ord integer PRIMARY KEY, surface text) ON COMMIT DROP;
INSERT INTO placement_input
SELECT ord::integer, surface
FROM unnest(ARRAY[
    'A', '狼', ' ', NULL, '', 'A', U&'\00E9', U&'e\0301',
    U&'q\0301', U&'q\0301x', 'ab', 'ba', 'aa',
    'one two', ' one two ', 'one  two', E'one two.\nThree four.',
    U&'\3000', U&'\+01F469\200D\+01F4BB', 'word word', 'word'
]::text[]) WITH ORDINALITY AS i(surface, ord);

-- Keep this the first perfcache-consuming call in this backend. A cold backend
-- must initialize the native floor itself before word_id or prompt_tree runs.
CREATE TEMP TABLE placement_result ON COMMIT DROP AS
SELECT p.* FROM converse.text_root_placements(
    (SELECT array_agg(i.surface ORDER BY i.ord) FROM placement_input i)) p;

DO $batch$
BEGIN
    IF (SELECT count(*) FROM placement_result) <> 21
       OR (SELECT array_agg(p.ord ORDER BY p.ord) FROM placement_result p)
          IS DISTINCT FROM ARRAY(SELECT generate_series(1,21)) THEN
        RAISE EXCEPTION 'text placement lost or reordered an input occurrence';
    END IF;
    IF EXISTS (
        SELECT 1 FROM placement_result p JOIN placement_input i USING (ord)
        WHERE (i.surface IS NULL OR i.surface = '')
          AND (p.root_id IS NOT NULL OR p.tier IS NOT NULL OR p.coord IS NOT NULL
               OR p.hilbert_index IS NOT NULL OR p.radius_origin IS NOT NULL)) THEN
        RAISE EXCEPTION 'null/empty elements must retain ordinals with null placement';
    END IF;
    IF EXISTS (
        SELECT 1 FROM placement_result p JOIN placement_input i USING (ord)
        WHERE i.surface IS NOT NULL AND i.surface <> ''
          AND (p.root_id IS NULL OR octet_length(p.root_id) <> 16 OR p.tier IS NULL
               OR p.coord IS NULL OR array_ndims(p.coord) <> 1
               OR array_lower(p.coord,1) <> 1 OR cardinality(p.coord) <> 4
               OR p.hilbert_index IS NULL OR octet_length(p.hilbert_index) <> 16
               OR p.radius_origin IS NULL
               OR EXISTS (SELECT 1 FROM unnest(p.coord) c(v)
                          WHERE v IS NULL OR NOT (v > '-Infinity'::float8
                                                 AND v < 'Infinity'::float8)))) THEN
        RAISE EXCEPTION 'nonempty text must have one complete finite 4D placement';
    END IF;
    IF EXISTS (SELECT 1 FROM converse.text_root_placements(NULL::text[]))
       OR EXISTS (SELECT 1 FROM converse.text_root_placements(ARRAY[]::text[])) THEN
        RAISE EXCEPTION 'null/empty arrays must return no rows';
    END IF;
    IF (SELECT to_jsonb(p) - 'ord' FROM placement_result p WHERE ord=1)
       IS DISTINCT FROM
       (SELECT to_jsonb(p) - 'ord' FROM placement_result p WHERE ord=6) THEN
        RAISE EXCEPTION 'duplicate text occurrences changed placement';
    END IF;
    -- SQL array bounds do not replace the API occurrence ordinal.
    IF (SELECT array_agg(p.ord ORDER BY p.ord)
        FROM converse.text_root_placements('[0:2]={A,NULL,A}'::text[]) p)
       IS DISTINCT FROM ARRAY[1,2,3] THEN
        RAISE EXCEPTION 'non-default array bounds changed occurrence ordinals';
    END IF;
    BEGIN
        PERFORM * FROM converse.text_root_placements(ARRAY[['A','B'],['C','D']]);
        RAISE EXCEPTION 'multidimensional text arrays were accepted';
    EXCEPTION WHEN invalid_parameter_value THEN
        NULL;
    END;
    RAISE NOTICE 'text root placements: complete batches preserve ordinals, duplicates, nulls and empty-input contracts';
END
$batch$;

CREATE TEMP TABLE placement_tree ON COMMIT DROP AS
SELECT i.ord, t.*, NULL::geometry AS composed_coord
FROM placement_input i CROSS JOIN LATERAL converse.prompt_tree(i.surface) t;

DO $identity$
BEGIN
    IF EXISTS (
        SELECT 1 FROM placement_result p JOIN placement_input i USING (ord)
        WHERE p.root_id IS DISTINCT FROM laplace.word_id(i.surface)) THEN
        RAISE EXCEPTION 'placement root differs from canonical word_id';
    END IF;
    IF EXISTS (
        SELECT 1 FROM placement_result p
        WHERE p.root_id IS NOT NULL AND (
            NOT EXISTS (SELECT 1 FROM placement_tree t
                        WHERE t.ord=p.ord AND t.parent_index IS NULL
                          AND t.root_id=p.root_id AND t.id=p.root_id)
            OR p.tier IS DISTINCT FROM
               (SELECT min(t.tier) FROM placement_tree t
                WHERE t.ord=p.ord AND t.id=p.root_id))) THEN
        RAISE EXCEPTION 'placement root or collapsed tier differs from complete prompt_tree';
    END IF;
    IF EXISTS (
        SELECT 1 FROM placement_result p JOIN placement_input i USING (ord)
        WHERE p.ord IN (1,2,3,18)
          AND (p.tier <> 0 OR p.root_id IS DISTINCT FROM
               public.laplace_hash128_blake3(convert_to(i.surface,'UTF8'))
               OR realize.codepoint_for_id(p.root_id) IS DISTINCT FROM ascii(i.surface))) THEN
        RAISE EXCEPTION 'single-codepoint roots must collapse to the native Unicode floor';
    END IF;
    IF (SELECT to_jsonb(p) - 'ord' FROM placement_result p WHERE ord=7)
       IS DISTINCT FROM
       (SELECT to_jsonb(p) - 'ord' FROM placement_result p WHERE ord=8)
       OR (SELECT tier FROM placement_result WHERE ord=7) <> 0
       OR (SELECT root_id FROM placement_result WHERE ord=7) IS DISTINCT FROM
          public.laplace_hash128_blake3(convert_to(U&'\00E9','UTF8')) THEN
        RAISE EXCEPTION 'NFC/NFD equivalents must share exact canonical root placement';
    END IF;
    IF (SELECT tier FROM placement_result WHERE ord=9) <> 1
       OR (SELECT tier FROM placement_result WHERE ord=10) <> 2
       OR (SELECT tier FROM placement_result WHERE ord=19) <> 1
       OR (SELECT tier FROM placement_result WHERE ord=14) <> 3 THEN
        RAISE EXCEPTION 'grapheme, word or multiword root tier was flattened';
    END IF;
    IF (SELECT count(DISTINCT root_id) FROM placement_result WHERE ord IN (14,15,16)) <> 3
       OR (SELECT root_id FROM placement_result WHERE ord=20)
          IS NOT DISTINCT FROM (SELECT root_id FROM placement_result WHERE ord=21) THEN
        RAISE EXCEPTION 'whitespace or repeated lexical content was discarded';
    END IF;
    IF (SELECT root_id FROM placement_result WHERE ord=11)
       IS NOT DISTINCT FROM (SELECT root_id FROM placement_result WHERE ord=12)
       OR (SELECT coord FROM placement_result WHERE ord=11)
          IS DISTINCT FROM (SELECT coord FROM placement_result WHERE ord=12)
       OR (SELECT hilbert_index FROM placement_result WHERE ord=11)
          IS DISTINCT FROM (SELECT hilbert_index FROM placement_result WHERE ord=12) THEN
        RAISE EXCEPTION 'ordered content identity and commutative centroid placement were conflated';
    END IF;
    RAISE NOTICE 'text root placements: canonical IDs, singleton tiers, Unicode normalization and full whitespace content agree';
END
$identity$;

-- Reconstruct coordinates from the independently exposed complete tree. Only
-- leaf coordinates come from the adapter; every internal node is composed via
-- the existing native geometry centroid over its actual ordered child points.
-- Core tests separately compare these leaves with codepoint_table_resolve_atom.
DO $coordinates$
DECLARE
    node record;
    leaf record;
    expected_coord geometry;
    expected_id bytea;
    child_ids bytea[];
    child_coords geometry[];
BEGIN
    FOR node IN SELECT * FROM placement_tree ORDER BY ord,node_index LOOP
        IF node.tier=0 THEN
            SELECT p.* INTO STRICT leaf FROM converse.text_root_placements(ARRAY[node.surface]) p;
            IF leaf.tier <> 0 OR leaf.root_id IS DISTINCT FROM node.id THEN
                RAISE EXCEPTION 'tree atom and independently requested floor root disagree';
            END IF;
            expected_coord := public.ST_MakePoint(
                leaf.coord[1],leaf.coord[2],leaf.coord[3],leaf.coord[4]);
        ELSE
            SELECT array_agg(t.id ORDER BY t.node_index),
                   array_agg(t.composed_coord ORDER BY t.node_index)
            INTO child_ids,child_coords
            FROM placement_tree t WHERE t.ord=node.ord AND t.parent_index=node.node_index;
            IF child_ids IS NULL OR array_position(child_coords,NULL::geometry) IS NOT NULL THEN
                RAISE EXCEPTION 'tree composition requires every child before its parent';
            END IF;
            expected_id := CASE WHEN cardinality(child_ids)=1 THEN child_ids[1]
                ELSE public.laplace_hash128_merkle(node.tier,child_ids) END;
            IF expected_id IS DISTINCT FROM node.id THEN
                RAISE EXCEPTION 'complete tree child sequence does not reproduce node identity';
            END IF;
            expected_coord := public.laplace_centroid_4d(public.ST_Collect(child_coords));
        END IF;
        UPDATE placement_tree t SET composed_coord=expected_coord
        WHERE t.ord=node.ord AND t.node_index=node.node_index;
    END LOOP;
    IF EXISTS (
        SELECT 1 FROM placement_result p JOIN placement_tree t USING (ord)
        WHERE t.parent_index IS NULL
          AND public.ST_AsEWKB(t.composed_coord) IS DISTINCT FROM
              public.ST_AsEWKB(public.ST_MakePoint(p.coord[1],p.coord[2],p.coord[3],p.coord[4]))) THEN
        RAISE EXCEPTION 'root coordinate differs bitwise from complete native centroid composition';
    END IF;
    IF EXISTS (
        SELECT 1 FROM placement_result p
        CROSS JOIN LATERAL (SELECT public.ST_MakePoint(
            p.coord[1],p.coord[2],p.coord[3],p.coord[4]) AS coord) g
        WHERE p.root_id IS NOT NULL
          AND (p.hilbert_index IS DISTINCT FROM public.laplace_hilbert_encode(g.coord)
               OR pg_catalog.float8send(p.radius_origin) IS DISTINCT FROM
                  pg_catalog.float8send(public.laplace_radius_origin(g.coord)))) THEN
        RAISE EXCEPTION 'Hilbert key or radius differs from native geometry functions';
    END IF;
    -- Duplicate constituents retain their multiplicity in identity even when
    -- their arithmetic centroid coincides exactly with the repeated atom.
    SELECT p.* INTO STRICT leaf FROM converse.text_root_placements(ARRAY['a']) p;
    IF (SELECT root_id FROM placement_result WHERE ord=13) IS NOT DISTINCT FROM leaf.root_id
       OR (SELECT coord FROM placement_result WHERE ord=13) IS DISTINCT FROM leaf.coord THEN
        RAISE EXCEPTION 'repeated constituent identity was collapsed into its coincident placement';
    END IF;
    RAISE NOTICE 'text root placements: complete native tree composition, exact Hilbert keys and radius agree';
END
$coordinates$;

ROLLBACK;
