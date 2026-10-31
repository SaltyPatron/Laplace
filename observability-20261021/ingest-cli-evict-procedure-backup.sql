CREATE OR REPLACE PROCEDURE ops.evict_source(IN p_source bytea, IN p_relations bytea[] DEFAULT NULL::bytea[], IN p_marker_types bytea[] DEFAULT NULL::bytea[], IN p_batch integer DEFAULT 100000, IN p_drain boolean DEFAULT true)
 LANGUAGE plpgsql
AS $procedure$
DECLARE
    rels bytea[];
    operational_types bytea[];
    operational_total bigint := 0;
    rel bytea;
    n_del bigint;
    n_cells bigint;
    retraction_counts bigint[];
    n bigint;
    evidence_total bigint := 0;
    refold_total bigint := 0;
    culled_total bigint := 0;
    layer_completion_total bigint := 0;
    unit_completion_total bigint := 0;
    replay_total bigint := 0;
    t0 timestamptz := clock_timestamp();
BEGIN
    IF p_batch IS NULL OR p_batch <= 0 THEN
        RAISE EXCEPTION 'evict_source: p_batch must be positive' USING ERRCODE='22023';
    END IF;
    IF p_source IS NULL OR octet_length(p_source) <> 16 THEN
        RAISE EXCEPTION 'evict_source: source must be a 16-byte identity' USING ERRCODE='22023';
    END IF;
    IF EXISTS (SELECT FROM unnest(p_relations) AS requested(id)
               WHERE id IS NULL OR octet_length(id) <> 16) THEN
        RAISE EXCEPTION 'evict_source: relations must be 16-byte identities' USING ERRCODE='22023';
    END IF;
    IF row_security_active('laplace.attestations'::regclass) THEN
        RAISE EXCEPTION 'evict_source: retraction requires complete visibility of retained testimony'
            USING ERRCODE='42501';
    END IF;
    IF cardinality(p_marker_types) > 0 THEN
        RAISE WARNING 'evict_source: marker entity deletion is retired; canonical content is source-independent; witness completion receipts are cleared';
    END IF;
    -- Plain TEMP table (per-batch COMMIT would drop ON COMMIT DROP), truncated
    -- per batch and dropped at the end.
    CREATE TEMP TABLE IF NOT EXISTS _evict_cells (
        subject_id bytea NOT NULL,
        object_id bytea,
        consensus_leaf oid NOT NULL,
        UNIQUE NULLS NOT DISTINCT (subject_id, object_id));
    TRUNCATE _evict_cells;
    -- A session's existing temp table may lack consensus_leaf; add it once.
    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute a
        WHERE a.attrelid = 'pg_temp._evict_cells'::regclass
          AND a.attname = 'consensus_leaf'
          AND NOT a.attisdropped
    ) THEN
        ALTER TABLE _evict_cells ADD COLUMN consensus_leaf oid;
    END IF;
    ALTER TABLE _evict_cells
        ALTER COLUMN consensus_leaf SET NOT NULL;
    CREATE INDEX IF NOT EXISTS _evict_cells_leaf_idx
        ON _evict_cells (consensus_leaf);

    IF p_relations IS NOT NULL THEN
        rels := p_relations;
    ELSE
        -- Every HASH(subject_id) leaf can hold every relation type. Discover
        -- the complete set in one source-indexed statement; LIMIT 1 per leaf
        -- silently omitted testimony from unrestricted eviction.
        SELECT array_agg(DISTINCT a.type_id ORDER BY a.type_id) INTO rels
        FROM laplace.attestations a WHERE a.source_id=p_source;
    END IF;
    -- Operational relation types (the writer's admission receipts) carry no
    -- rating observations and are retracted separately below.
    operational_types := ARRAY[realize.canonical_id('substrate/type/HasFileMetadata/v1')];

    SELECT COALESCE(array_agg(candidate ORDER BY ordinal), ARRAY[]::bytea[]) INTO rels
    FROM unnest(rels) WITH ORDINALITY AS selected(candidate, ordinal)
    WHERE NOT (candidate = ANY(operational_types));

    RAISE LOG 'evict_source: start — source %, % relation(s)',
        encode(p_source, 'hex'), COALESCE(cardinality(rels), 0);

    -- A cell carrying a transient-calibration receipt cannot be refolded: its
    -- continuous score was never persisted. Refuse before the first DELETE.
    IF EXISTS (
        SELECT 1
        FROM laplace.attestations source_evidence
        JOIN laplace.attestations transient_evidence
          ON transient_evidence.type_id = source_evidence.type_id
         AND transient_evidence.subject_id = source_evidence.subject_id
         AND transient_evidence.object_id IS NOT DISTINCT FROM source_evidence.object_id
        WHERE source_evidence.source_id = p_source
          AND transient_evidence.fold_replayable = false
    ) THEN
        RAISE EXCEPTION 'evict_source: source % touches transient-calibration evidence; no score replay exists',
            encode(p_source, 'hex');
    END IF;

    -- Replay tokens are applied source state; left in place they would make a
    -- re-derive a no-op. Deleted first, so an interrupted eviction can re-admit
    -- every row removed so far. Tokens with no owning source are not touched.
    DELETE FROM laplace.ingest_flush_journal receipt
    WHERE receipt.source_id = p_source
       OR EXISTS (
           SELECT 1
           FROM laplace.ingest_flush_journal_sources owner
           WHERE owner.working_set_id = receipt.working_set_id
             AND owner.source_id = p_source);
    GET DIAGNOSTICS replay_total = ROW_COUNT;
    IF replay_total > 0 THEN COMMIT; END IF;

    -- Completion belongs to the witness; any retraction invalidates it, even a
    -- partial one. Committed before the first evidence deletion, so no
    -- completion row can hide partially retracted output.
    DELETE FROM laplace.ingest_unit_completion WHERE witness_id = p_source;
    GET DIAGNOSTICS unit_completion_total = ROW_COUNT;
    DELETE FROM laplace.ingest_layer_completion WHERE witness_id = p_source;
    GET DIAGNOSTICS layer_completion_total = ROW_COUNT;
    IF unit_completion_total > 0 OR layer_completion_total > 0 THEN COMMIT; END IF;

    -- Operational rows owned by the source are retracted whatever p_relations
    -- selects; they have no fold.
    LOOP
        WITH pick AS (
            SELECT a.id, a.type_id, a.subject_id
            FROM laplace.attestations a
            WHERE a.source_id = p_source AND a.type_id = ANY(operational_types)
            LIMIT p_batch),
        del AS (
            DELETE FROM laplace.attestations a
            USING pick
            WHERE a.id = pick.id AND a.type_id = pick.type_id
              AND a.subject_id = pick.subject_id
            RETURNING 1)
        SELECT count(*) INTO n FROM del;
        EXIT WHEN n = 0;
        operational_total := operational_total + n;
        COMMIT;
    END LOOP;
    IF operational_total > 0 THEN
        RAISE LOG 'evict_source: % owned operational row(s) removed', operational_total;
    END IF;

    FOREACH rel IN ARRAY COALESCE(rels, ARRAY[]::bytea[]) LOOP
        LOOP
            TRUNCATE _evict_cells;

            -- (a) delete one batch of the source's evidence under this relation;
            -- queue every touched entity for mask repair; accumulate the
            -- touched cells for the refold below.
            EXECUTE format($q$
                WITH pick AS (
                    SELECT a.id, a.subject_id
                    FROM laplace.attestations a
                    WHERE a.type_id = %L::bytea AND a.source_id = $1
                    LIMIT %s),
                del AS (
                    DELETE FROM laplace.attestations a
                    USING pick
                    WHERE a.type_id = %L::bytea
                      AND a.id = pick.id AND a.subject_id = pick.subject_id
                    RETURNING a.subject_id, a.object_id),
                dirty AS (
                    INSERT INTO laplace.highway_mask_dirty (id)
                    SELECT DISTINCT u.id
                    FROM (SELECT d.subject_id AS id FROM del d
                          UNION
                          SELECT d.object_id FROM del d WHERE d.object_id IS NOT NULL) u
                    ON CONFLICT (id) DO NOTHING),
                cells AS (
                    INSERT INTO _evict_cells (subject_id, object_id, consensus_leaf)
                    SELECT DISTINCT d.subject_id, d.object_id,
                           consensus.partition_leaf(%L::bytea, d.subject_id)::oid
                    FROM del d
                    ON CONFLICT DO NOTHING
                    RETURNING 1)
                SELECT (SELECT count(*) FROM del), (SELECT count(*) FROM cells)
            $q$, rel, p_batch, rel, rel)
            INTO n_del, n_cells
            USING p_source;

            EXIT WHEN n_del = 0;
            evidence_total := evidence_total + n_del;

            -- The native owner locks the exact target set, reads a fresh
            -- snapshot after any concurrent admission, refolds survivors and
            -- removes zero-survivor cells in this same evidence transaction.
            SELECT consensus.refold_after_retraction_type(
                rel,
                array_agg(subject_id ORDER BY subject_id,object_id NULLS FIRST),
                array_agg(object_id ORDER BY subject_id,object_id NULLS FIRST))
            INTO retraction_counts FROM _evict_cells;
            refold_total := refold_total + retraction_counts[1];
            culled_total := culled_total + retraction_counts[2];
            RAISE LOG 'evict_source: relation % — batch of % evidence row(s) over % cell(s) (running: % deleted, % refolded, % culled) — %s elapsed',
                encode(rel, 'hex'), n_del, n_cells,
                evidence_total, refold_total, culled_total,
                round(extract(epoch FROM clock_timestamp() - t0)::numeric, 1);
            COMMIT;
        END LOOP;
    END LOOP;



    -- (d) drain the mask-repair queue: refresh recomputes from scratch and can
    -- clear bits.
    IF p_drain THEN
        CALL laplace.highway_mask_drain(p_batch);
    END IF;

    IF p_relations IS NULL THEN
        SELECT count(*) INTO n FROM laplace.attestations WHERE source_id=p_source;
        IF n <> 0 THEN
            RAISE EXCEPTION 'evict_source: unrestricted retraction left % testimony row(s) for source %',
                n,encode(p_source,'hex');
        END IF;
    END IF;
    RAISE LOG 'evict_source: done — source %: % evidence row(s) deleted, % cell(s) refolded, % cell(s) culled, % layer completion(s) removed, % unit completion(s) removed, % replay claim(s) removed in %s',
        encode(p_source, 'hex'), evidence_total, refold_total, culled_total,
        layer_completion_total, unit_completion_total, replay_total,
        round(extract(epoch FROM clock_timestamp() - t0)::numeric, 1);

    DROP TABLE _evict_cells;
END $procedure$

