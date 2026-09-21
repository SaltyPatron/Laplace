-- CILI's ili.ttl republishes Princeton WordNet concepts and glosses while
-- naming the originating PWN synset in dc:source. Those copied glosses were
-- historically admitted as CILIDecomposer HAS_DEFINITION testimony on the same
-- canonical ILI/PWN concept that WordNetDecomposer also witnesses, making one
-- authority look like two independent sources.
--
-- New ingest no longer emits the CILI witness for PWN-backed records. Repair the
-- already-recorded estate without dropping shared identities or native CILI-only
-- definitions: select only CILI definitions whose subject also has a CILI-owned
-- HAS_SYNSET_KEY mapping, retract those evidence rows, and refold exactly the
-- touched consensus cells from surviving replayable testimony.
DO $cili_pwn_gloss_repair$
DECLARE
    v_source bytea := laplace.source_id('CILIDecomposer');
    v_definition bytea := laplace.relation_type_id('HAS_DEFINITION');
    v_synset_key bytea := laplace.relation_type_id('HAS_SYNSET_KEY');
    v_removed bigint := 0;
    v_refolded bigint := 0;
    v_culled bigint := 0;
BEGIN
    CREATE TEMP TABLE _cili_pwn_gloss_cells (
        subject_id bytea NOT NULL,
        object_id bytea NOT NULL,
        PRIMARY KEY (subject_id, object_id)
    ) ON COMMIT DROP;

    INSERT INTO _cili_pwn_gloss_cells(subject_id, object_id)
    SELECT DISTINCT d.subject_id, d.object_id
    FROM laplace.attestations d
    WHERE d.type_id = v_definition
      AND d.source_id = v_source
      AND d.object_id IS NOT NULL
      AND EXISTS (
          SELECT 1
          FROM laplace.attestations m
          WHERE m.type_id = v_synset_key
            AND m.source_id = v_source
            AND m.subject_id = d.subject_id
      );

    IF EXISTS (
        SELECT 1
        FROM laplace.attestations a
        JOIN _cili_pwn_gloss_cells t
          ON t.subject_id = a.subject_id
         AND t.object_id = a.object_id
        WHERE a.type_id = v_definition
          AND a.fold_replayable = false
    ) THEN
        RAISE EXCEPTION
            'CILI PWN gloss provenance repair touches non-replayable evidence; refusing lossy refold';
    END IF;

    DELETE FROM laplace.attestations d
    USING _cili_pwn_gloss_cells t
    WHERE d.type_id = v_definition
      AND d.source_id = v_source
      AND d.subject_id = t.subject_id
      AND d.object_id = t.object_id;
    GET DIAGNOSTICS v_removed = ROW_COUNT;

    IF v_removed = 0 THEN
        RAISE NOTICE 'CILI PWN gloss provenance repair: no packaged gloss witnesses found';
        RETURN;
    END IF;

    -- Rebuild every still-supported cell from its surviving evidence using the
    -- same canonical grouped fold used by ops.evict_source.
    INSERT INTO laplace.consensus AS c
        (id, subject_id, type_id, object_id,
         rating, rd, volatility, witness_count, last_observed_at)
    SELECT laplace.consensus_id(f.subject_id, v_definition, f.object_id),
           f.subject_id, v_definition, f.object_id,
           (f.acc).rating, (f.acc).rd, (f.acc).volatility,
           (f.acc).witness_count, f.last_ts
    FROM (
        SELECT t.subject_id, t.object_id,
               laplace.consensus_fold(
                   false, NULL, NULL, NULL,
                   a.opponent_rating_fp1e9,
                   a.opponent_rd_fp1e9,
                   GREATEST(a.observation_count, 1),
                   a.sum_score_fp1e9,
                   consensus.glicko2_tau()
                   ORDER BY a.last_observed_at, a.id) AS acc,
               max(a.last_observed_at) AS last_ts
        FROM _cili_pwn_gloss_cells t
        JOIN laplace.attestations a
          ON a.type_id = v_definition
         AND a.subject_id = t.subject_id
         AND a.object_id = t.object_id
        GROUP BY t.subject_id, t.object_id
    ) f
    ON CONFLICT (id, type_id, subject_id) DO UPDATE
    SET object_id        = EXCLUDED.object_id,
        rating           = EXCLUDED.rating,
        rd               = EXCLUDED.rd,
        volatility       = EXCLUDED.volatility,
        witness_count    = EXCLUDED.witness_count,
        last_observed_at = EXCLUDED.last_observed_at;
    GET DIAGNOSTICS v_refolded = ROW_COUNT;

    -- Unsupported cells are claims nobody makes after retraction: delete them,
    -- never retain a zero-witness consensus row.
    CREATE TEMP TABLE _cili_pwn_gloss_culled (
        subject_id bytea NOT NULL,
        object_id bytea,
        PRIMARY KEY (subject_id, object_id)
    ) ON COMMIT DROP;

    WITH deleted AS (
        DELETE FROM laplace.consensus c
        USING _cili_pwn_gloss_cells t
        WHERE c.type_id = v_definition
          AND c.subject_id = t.subject_id
          AND c.id = laplace.consensus_id(t.subject_id, v_definition, t.object_id)
          AND NOT EXISTS (
              SELECT 1
              FROM laplace.attestations a
              WHERE a.type_id = v_definition
                AND a.subject_id = t.subject_id
                AND a.object_id = t.object_id
          )
        RETURNING c.subject_id, c.object_id
    )
    INSERT INTO _cili_pwn_gloss_culled(subject_id, object_id)
    SELECT d.subject_id, d.object_id FROM deleted d;

    SELECT count(*) INTO v_culled FROM _cili_pwn_gloss_culled;

    INSERT INTO laplace.highway_mask_dirty(id)
    SELECT u.id
    FROM (
        SELECT c.subject_id AS id FROM _cili_pwn_gloss_culled c
        UNION
        SELECT c.object_id FROM _cili_pwn_gloss_culled c
        WHERE c.object_id IS NOT NULL
    ) u
    ON CONFLICT (id) DO NOTHING;

    RAISE NOTICE
        'CILI PWN gloss provenance repair: % packaged evidence row(s) removed, % cell(s) refolded, % unsupported cell(s) culled',
        v_removed, v_refolded, v_culled;
END
$cili_pwn_gloss_repair$;
