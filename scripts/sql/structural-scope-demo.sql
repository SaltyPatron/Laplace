-- Original Laplace database; fixed SQL over canonical native set operations.
-- psql -X -h /var/run/postgresql -U laplace_admin -d laplace -f this-file
-- Override these content IDs with psql -v captain=... -v ahab=... if needed.
\set ON_ERROR_STOP on
\if :{?captain}
\else
\set captain cd63dbb3003ad455d56b27109987e746
\endif
\if :{?ahab}
\else
\set ahab f98a946cab2c85d7e3824ec8e0e11db5
\endif
\timing on
BEGIN READ ONLY;
SET LOCAL statement_timeout = '15s';

-- Co-occurrence: one witnessed composition contains both exact identities.
SELECT encode(entity_id, 'hex') AS containing_composition
FROM structural.containers_containing_all(
    ARRAY[decode(:'captain','hex'), decode(:'ahab','hex')])
ORDER BY entity_id;

-- Ordered adjacency with an attested separator carried explicitly.
-- Gap 1 permits adjacent endpoints or one separator between endpoints.
-- This is the native operation's ordinal rule, not a geometric metric or a
-- general count of words after stripping arbitrary punctuation/whitespace.
-- NULL witness limit selects the whole available matching witness set.
SELECT encode(subject_id,'hex') AS subject,
       encode(sep_id,'hex') AS separator,
       encode(object_id,'hex') AS object,
       encode(witness_id,'hex') AS witness
FROM generation.word_adjacency(
    ARRAY[decode(:'captain','hex'),decode(:'ahab','hex')], NULL, 1)
WHERE subject_id = decode(:'captain','hex')
  AND object_id = decode(:'ahab','hex')
ORDER BY witness_id, sep_id;

ROLLBACK;

-- Additional complete-set query (not part of the fast co-occurrence demo).
-- The live two-hop probe exceeded 15 seconds on 2026-09-12; performance remains
-- an implementation obligation. Run separately to inspect the full result.
-- Exactly two upward composition hops, with no caller top-K ceiling.
-- These are shortest containment hops, not arbitrary semantic relations.
-- SELECT encode(entity_id,'hex') AS containing_composition, tier,
--        encode(type_id,'hex') AS type, hops
-- FROM structural.containers_of(decode(:'captain','hex'), 2, NULL)
-- WHERE hops = 2
-- ORDER BY entity_id;
