CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;

SET search_path TO laplace, public;


SELECT count(*) = 0 AS no_legacy_type_column
FROM information_schema.columns
WHERE table_schema = 'laplace'
  AND column_name = convert_from(decode('6b696e64', 'hex'), 'UTF8');

SELECT count(*) = 0 AS no_legacy_type_functions
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'laplace'
  AND p.proname IN (
    convert_from(decode('6b696e645f6964', 'hex'), 'UTF8'),
    convert_from(decode('656e746974795f6b696e64', 'hex'), 'UTF8'));

SELECT count(*) AS physicality_type_entities FROM entities
WHERE id IN (
    laplace_hash128_blake3('substrate/physicality_type/CONTENT/v1'::bytea),
    laplace_hash128_blake3('substrate/physicality_type/BUILDING_BLOCK/v1'::bytea),
    laplace_hash128_blake3('substrate/physicality_type/PROJECTION/v1'::bytea),
    laplace_hash128_blake3('substrate/physicality_type/PROJECTION_OUTPUT/v1'::bytea)
);

SELECT relation_type_id('IS_A')
       = laplace_hash128_blake3('IS_A'::bytea) AS relation_type_path_law;

SELECT relation_type_resolve('HAS_UPOS') = relation_type_id('HAS_POS') AS pos_alias_resolve_law;

SELECT relation_type_in_family(relation_type_id('HAS_XPOS'), 'HAS_POS') AS xpos_in_pos_family;

SELECT NOT relation_type_in_family(relation_type_id('HAS_LEX_CATEGORY'), 'HAS_POS') AS lex_not_pos_family;

SELECT EXISTS (
    SELECT 1 FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'laplace' AND c.relname = 'attestations' AND a.attname = 'type_id' AND NOT a.attisdropped
) AS attestations_has_type_id;

SELECT EXISTS (
    SELECT 1 FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'laplace' AND c.relname = 'physicalities' AND a.attname = 'type' AND NOT a.attisdropped
) AS physicalities_has_type;

SELECT count(*) = 0 AS no_legacy_attestations_index
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'laplace'
  AND c.relname = convert_from(decode('6174746573746174696f6e735f6b696e645f6274726565', 'hex'), 'UTF8');



SELECT count(*) = 0 AS no_codepoint_render_shadow
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'laplace' AND c.relname = 'codepoint_render';
