-- Glicko standing coordinates remain typed COUPLE state before ORIENT.
\set ECHO none
BEGIN;
DO $couple_standing_state$
DECLARE
    prompt text := 'zzstandingvolatilityqv';
    anchor bytea;
    candidate bytea := laplace.word_id('zzstandingcandidateqv');
    relation bytea := laplace.relation_type_id('IS_A');
    before_program bytea;
    after_program bytea;
BEGIN
    -- Address one exact occurrence from the same tier-2 execution cut consumed
    -- by laplace_prompt_input. The test changes no prompt, relation, endpoint,
    -- rating, RD or witness count between executions.
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
         1800000000000,30000000000,60000000,5,now());

    SELECT p.program_id INTO before_program
      FROM generation.forward_program(prompt,1,0,0.0,8,7,0,64,NULL,NULL) p
     WHERE p.event IN ('complete','unresolved','ambiguous','budget_exhausted')
     ORDER BY p.step DESC
     LIMIT 1;
    IF before_program IS NULL THEN
        RAISE EXCEPTION 'FAIL: baseline standing program produced no receipt';
    END IF;

    UPDATE laplace.consensus
       SET volatility=70000000
     WHERE id=laplace.consensus_id(anchor,relation,candidate);

    SELECT p.program_id INTO after_program
      FROM generation.forward_program(prompt,1,0,0.0,8,7,0,64,NULL,NULL) p
     WHERE p.event IN ('complete','unresolved','ambiguous','budget_exhausted')
     ORDER BY p.step DESC
     LIMIT 1;
    IF after_program IS NULL THEN
        RAISE EXCEPTION 'FAIL: updated standing program produced no receipt';
    END IF;
    IF after_program = before_program THEN
        RAISE EXCEPTION
            'FAIL: changing only standing volatility did not change the coupled program identity';
    END IF;

    RAISE NOTICE 'standing volatility: preserved in pre-ORIENT typed response state';
END
$couple_standing_state$;
ROLLBACK;
