-- Exact source/context witness identity remains typed COUPLE state before ORIENT.
\set ECHO none
BEGIN;
DO $couple_provenance_state$
DECLARE
    prompt text := 'zzprovenancestateqv';
    anchor bytea;
    candidate bytea := laplace.word_id('zzprovenancecandidateqv');
    relation bytea := laplace.relation_type_id('IS_A');
    source_a bytea := public.laplace_hash128_blake3('test/couple-provenance/source-a');
    source_b bytea := public.laplace_hash128_blake3('test/couple-provenance/source-b');
    context_a bytea := public.laplace_hash128_blake3('test/couple-provenance/context-a');
    context_b bytea := public.laplace_hash128_blake3('test/couple-provenance/context-b');
    witness_a bytea := public.laplace_hash128_blake3('test/couple-provenance/witness-a');
    witness_b bytea := public.laplace_hash128_blake3('test/couple-provenance/witness-b');
    before_program bytea;
    after_program bytea;
BEGIN
    -- Address one exact occurrence from the same tier-2 execution cut consumed
    -- by laplace_prompt_input. Between executions the relation cell, standing,
    -- outcome totals and source/context cardinalities remain identical.
    WITH tree AS MATERIALIZED (
        SELECT * FROM converse.prompt_tree(prompt,false)
    ), cut AS (
        SELECT t.id,t.byte_offset,t.node_index
          FROM tree t
          LEFT JOIN tree parent ON parent.node_index=t.parent_index
         WHERE t.tier <= 2
           AND (t.parent_index IS NULL OR parent.tier > 2)
    )
    SELECT id INTO anchor
      FROM cut
     ORDER BY byte_offset,node_index
     LIMIT 1;
    IF anchor IS NULL THEN
        RAISE EXCEPTION 'FAIL: exact prompt execution cut is empty';
    END IF;

    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES
        (laplace.consensus_id(anchor,relation,candidate),
         anchor,relation,candidate,
         1800000000000,30000000000,60000000,1,now());

    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,context_id,
         outcome,last_observed_at,observation_count,
         sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES
        (witness_a,anchor,relation,candidate,source_a,context_a,
         2,now(),1,1000000000,30000000000);

    SELECT p.program_id INTO before_program
      FROM generation.forward_program(prompt,1,0,0.0,8,7,0,64,NULL,NULL) p
     WHERE p.event IN ('complete','unresolved','ambiguous','budget_exhausted')
     ORDER BY p.step DESC
     LIMIT 1;
    IF before_program IS NULL THEN
        RAISE EXCEPTION 'FAIL: baseline provenance program produced no receipt';
    END IF;

    -- Keep all aggregate response coordinates equal while replacing the exact
    -- witness route. Count-only coupling cannot distinguish these two states.
    UPDATE laplace.attestations
       SET id=witness_b, source_id=source_b, context_id=context_b
     WHERE id=witness_a
       AND subject_id=anchor
       AND type_id=relation
       AND object_id=candidate;

    SELECT p.program_id INTO after_program
      FROM generation.forward_program(prompt,1,0,0.0,8,7,0,64,NULL,NULL) p
     WHERE p.event IN ('complete','unresolved','ambiguous','budget_exhausted')
     ORDER BY p.step DESC
     LIMIT 1;
    IF after_program IS NULL THEN
        RAISE EXCEPTION 'FAIL: updated provenance program produced no receipt';
    END IF;
    IF after_program = before_program THEN
        RAISE EXCEPTION
            'FAIL: replacing exact source/context witness identity did not change the coupled program identity';
    END IF;

    RAISE NOTICE 'exact provenance: preserved in pre-ORIENT typed response state';
END
$couple_provenance_state$;
ROLLBACK;
