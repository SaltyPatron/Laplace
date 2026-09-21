-- Postcondition for 20260921000000_cili_pwn_gloss_provenance.sql.
-- This is intentionally one bounded data invariant, not a qualification suite:
-- PWN-backed CILI packaging must no longer exist as an independent CILI
-- definition witness, and the targeted refold must not leave unsupported
-- consensus cells behind.
DO $cili_pwn_gloss_postcondition$
DECLARE
    v_source bytea := laplace.source_id('CILIDecomposer');
    v_definition bytea := laplace.relation_type_id('HAS_DEFINITION');
    v_synset_key bytea := laplace.relation_type_id('HAS_SYNSET_KEY');
BEGIN
    IF EXISTS (
        SELECT 1
        FROM laplace.attestations d
        WHERE d.type_id = v_definition
          AND d.source_id = v_source
          AND EXISTS (
              SELECT 1
              FROM laplace.attestations m
              WHERE m.type_id = v_synset_key
                AND m.source_id = v_source
                AND m.subject_id = d.subject_id
          )
        LIMIT 1
    ) THEN
        RAISE EXCEPTION
            'CILI/PWN provenance repair incomplete: packaged PWN gloss still counted as CILI testimony';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM laplace.consensus c
        WHERE c.type_id = v_definition
          AND EXISTS (
              SELECT 1
              FROM laplace.attestations m
              WHERE m.type_id = v_synset_key
                AND m.source_id = v_source
                AND m.subject_id = c.subject_id
          )
          AND NOT EXISTS (
              SELECT 1
              FROM laplace.attestations a
              WHERE a.type_id = v_definition
                AND a.subject_id = c.subject_id
                AND a.object_id IS NOT DISTINCT FROM c.object_id
          )
        LIMIT 1
    ) THEN
        RAISE EXCEPTION
            'CILI/PWN provenance repair incomplete: unsupported definition consensus cell remains';
    END IF;
END
$cili_pwn_gloss_postcondition$;
