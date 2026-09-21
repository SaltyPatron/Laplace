-- SemLink instances are exact predicate OCCURRENCES (source file, sentence ordinal,
-- token ordinal). Historical ingest created those occurrence entities but then
-- projected their VN/FN/PB annotations onto the shared lemma, with the occurrence
-- only in context_id. A frequent word therefore accumulated thousands of direct
-- semantic evidence rows that the source actually asserted about occurrences.
--
-- Future ingest writes these relations on the occurrence. Rewrite the retained
-- estate in place without inventing or deleting source observations:
--   old: lemma --R--> target  [context = occurrence]
--   new: occurrence --R--> target [context = occurrence]
-- for the three predicate-level relations emitted by SemLinkInstanceIngest.
-- Argument-occurrence HAS_ROLE / ROLE_CORRESPONDS_TO rows already have the right
-- subject grain and are intentionally untouched.
DO $semlink_occurrence_grain$
DECLARE
    v_source bytea := laplace.source_id('SemLinkDecomposer');
    v_appears bytea := laplace.relation_type_id('APPEARS_IN');
    v_types bytea[] := ARRAY[
        laplace.relation_type_id('HAS_SENSE'),
        laplace.relation_type_id('EVOKES_FRAME'),
        laplace.relation_type_id('MEMBER_OF_VERBNET_CLASS')
    ];
    v_zero bytea := decode(repeat('00', 16), 'hex');
    v_rows bigint := 0;
    v_refolded bigint := 0;
    v_culled bigint := 0;
    v_ids bytea[];
    v_last bytea := NULL;
BEGIN
    CREATE TEMP TABLE _semlink_occurrence_old (
        old_id bytea NOT NULL,
        old_subject bytea NOT NULL,
        type_id bytea NOT NULL,
        object_id bytea NOT NULL,
        source_id bytea NOT NULL,
        occurrence_id bytea NOT NULL,
        outcome smallint NOT NULL,
        last_observed_at timestamptz NOT NULL,
        observation_count bigint NOT NULL,
        sum_score_fp1e9 bigint NOT NULL,
        opponent_rd_fp1e9 bigint NOT NULL,
        opponent_rating_fp1e9 bigint NOT NULL,
        fold_replayable boolean NOT NULL,
        highway_mask bytea,
        new_id bytea NOT NULL,
        PRIMARY KEY (old_id, type_id, old_subject)
    ) ON COMMIT DROP;

    INSERT INTO _semlink_occurrence_old
        (old_id, old_subject, type_id, object_id, source_id, occurrence_id,
         outcome, last_observed_at, observation_count, sum_score_fp1e9,
         opponent_rd_fp1e9, opponent_rating_fp1e9, fold_replayable,
         highway_mask, new_id)
    SELECT a.id, a.subject_id, a.type_id, a.object_id, a.source_id, a.context_id,
           a.outcome, a.last_observed_at, a.observation_count, a.sum_score_fp1e9,
           a.opponent_rd_fp1e9, a.opponent_rating_fp1e9, a.fold_replayable,
           a.highway_mask,
           public.laplace_hash128_blake3(
               a.context_id || a.type_id || a.object_id || a.source_id || a.context_id)
    FROM laplace.attestations a
    WHERE a.source_id = v_source
      AND a.type_id = ANY(v_types)
      AND a.object_id IS NOT NULL
      AND a.context_id IS NOT NULL
      AND a.subject_id <> a.context_id
      AND EXISTS (
          SELECT 1
          FROM laplace.attestations occ
          WHERE occ.source_id = v_source
            AND occ.type_id = v_appears
            AND occ.subject_id = a.context_id
            AND occ.context_id = a.context_id
      );

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    IF v_rows = 0 THEN
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM _semlink_occurrence_old WHERE NOT fold_replayable) THEN
        RAISE EXCEPTION
            'SemLink occurrence-grain repair touches non-replayable evidence; refusing lossy rewrite';
    END IF;

    CREATE TEMP TABLE _semlink_occurrence_cells (
        subject_id bytea NOT NULL,
        type_id bytea NOT NULL,
        object_id bytea NOT NULL,
        PRIMARY KEY (subject_id, type_id, object_id)
    ) ON COMMIT DROP;

    -- Both the old lemma cell and the new occurrence cell must be recomputed
    -- from the evidence that exists AFTER the rewrite.
    INSERT INTO _semlink_occurrence_cells(subject_id, type_id, object_id)
    SELECT old_subject, type_id, object_id FROM _semlink_occurrence_old
    UNION
    SELECT occurrence_id, type_id, object_id FROM _semlink_occurrence_old
    ON CONFLICT DO NOTHING;

    -- Preserve the exact observed outcome/calibration. This is an identity-grain
    -- correction, not a new observation. If a correctly-grained row already exists,
    -- keep it rather than counting the same source occurrence twice.
    INSERT INTO laplace.attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count, sum_score_fp1e9,
         opponent_rd_fp1e9, opponent_rating_fp1e9, fold_replayable, highway_mask)
    SELECT new_id, occurrence_id, type_id, object_id, source_id, occurrence_id,
           outcome, last_observed_at, observation_count, sum_score_fp1e9,
           opponent_rd_fp1e9, opponent_rating_fp1e9, fold_replayable, highway_mask
    FROM _semlink_occurrence_old
    ON CONFLICT (id, type_id, subject_id) DO NOTHING;

    DELETE FROM laplace.attestations a
    USING _semlink_occurrence_old old
    WHERE a.id = old.old_id
      AND a.type_id = old.type_id
      AND a.subject_id = old.old_subject;

    -- Canonical grouped refold of every touched cell from its CURRENT evidence.
    INSERT INTO laplace.consensus AS c
        (id, subject_id, type_id, object_id,
         rating, rd, volatility, witness_count, last_observed_at)
    SELECT laplace.consensus_id(f.subject_id, f.type_id, f.object_id),
           f.subject_id, f.type_id, f.object_id,
           (f.acc).rating, (f.acc).rd, (f.acc).volatility,
           (f.acc).witness_count, f.last_ts
    FROM (
        SELECT t.subject_id, t.type_id, t.object_id,
               laplace.consensus_fold(
                   false, NULL, NULL, NULL,
                   a.opponent_rating_fp1e9,
                   a.opponent_rd_fp1e9,
                   GREATEST(a.observation_count, 1),
                   a.sum_score_fp1e9,
                   consensus.glicko2_tau()
                   ORDER BY a.last_observed_at, a.id) AS acc,
               max(a.last_observed_at) AS last_ts
        FROM _semlink_occurrence_cells t
        JOIN laplace.attestations a
          ON a.type_id = t.type_id
         AND a.subject_id = t.subject_id
         AND a.object_id = t.object_id
        GROUP BY t.subject_id, t.type_id, t.object_id
    ) f
    ON CONFLICT (id, type_id, subject_id) DO UPDATE
    SET object_id        = EXCLUDED.object_id,
        rating           = EXCLUDED.rating,
        rd               = EXCLUDED.rd,
        volatility       = EXCLUDED.volatility,
        witness_count    = EXCLUDED.witness_count,
        last_observed_at = EXCLUDED.last_observed_at;
    GET DIAGNOSTICS v_refolded = ROW_COUNT;

    WITH deleted AS (
        DELETE FROM laplace.consensus c
        USING _semlink_occurrence_cells t
        WHERE c.type_id = t.type_id
          AND c.subject_id = t.subject_id
          AND c.id = laplace.consensus_id(t.subject_id, t.type_id, t.object_id)
          AND NOT EXISTS (
              SELECT 1
              FROM laplace.attestations a
              WHERE a.type_id = t.type_id
                AND a.subject_id = t.subject_id
                AND a.object_id = t.object_id
          )
        RETURNING c.subject_id, c.object_id
    )
    SELECT count(*) INTO v_culled FROM deleted;

    -- The rewrite both clears relation bits from global lemmas and adds them to
    -- occurrence identities. Refresh all touched endpoints in bounded native sets.
    CREATE TEMP TABLE _semlink_occurrence_mask_ids (
        id bytea PRIMARY KEY
    ) ON COMMIT DROP;
    INSERT INTO _semlink_occurrence_mask_ids(id)
    SELECT subject_id FROM _semlink_occurrence_cells
    UNION
    SELECT object_id FROM _semlink_occurrence_cells
    ON CONFLICT DO NOTHING;

    LOOP
        SELECT array_agg(q.id ORDER BY q.id) INTO v_ids
        FROM (
            SELECT id
            FROM _semlink_occurrence_mask_ids
            WHERE v_last IS NULL OR id > v_last
            ORDER BY id
            LIMIT 50000
        ) q;
        EXIT WHEN v_ids IS NULL;
        PERFORM consensus.highway_mask_refresh(v_ids);
        v_last := v_ids[cardinality(v_ids)];
    END LOOP;

    -- Hard postcondition: no instance annotation remains sprayed onto a lemma.
    IF EXISTS (
        SELECT 1
        FROM laplace.attestations a
        WHERE a.source_id = v_source
          AND a.type_id = ANY(v_types)
          AND a.context_id IS NOT NULL
          AND a.subject_id <> a.context_id
          AND EXISTS (
              SELECT 1
              FROM laplace.attestations occ
              WHERE occ.source_id = v_source
                AND occ.type_id = v_appears
                AND occ.subject_id = a.context_id
                AND occ.context_id = a.context_id
          )
        LIMIT 1
    ) THEN
        RAISE EXCEPTION
            'SemLink occurrence-grain repair incomplete: predicate annotation remains on global lemma';
    END IF;

    RAISE NOTICE
        'SemLink occurrence-grain repair: % evidence row(s) moved, % consensus cell(s) refolded, % unsupported old cell(s) culled',
        v_rows, v_refolded, v_culled;
END
$semlink_occurrence_grain$;
