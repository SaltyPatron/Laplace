\set ECHO none
\set ON_ERROR_STOP 1
\set QUIET 1
\pset format unaligned
\pset tuples_only on
SET client_min_messages=warning;

BEGIN;
-- Exercise the real native selector with eight simultaneously eligible typed
-- results. A single successor or spread=0 would conceal a broken seed policy.
-- This independent SQL spelling preserves the former text/chat byte contract;
-- it deliberately does not call a helper shared with the C implementation.
DO $seed_parity$
DECLARE
    prompt text;
    root bytea;
    relation_id bytea := laplace.relation_type_id('CAUSES');
    expected_seed bigint;
    explicit_seed bigint;
    implicit_trace jsonb;
    explicit_trace jsonb;
    expected_ids bytea[];
    actual_ids bytea[];
    fixed_ids bytea[];
    distinguishes_fixed_default boolean := false;
BEGIN
    FOREACH prompt IN ARRAY ARRAY['a','β','é',U&'e\0301','猫','😀'] LOOP
        root := laplace.word_id(prompt);
        INSERT INTO laplace.consensus
            (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
        SELECT laplace.consensus_id(root,relation_id,target),root,relation_id,target,
               3000000000000,30000000000,60000000,5,now()
        FROM (SELECT laplace.word_id(value) AS target
              FROM unnest(ARRAY['κ','λ','μ','ν','ξ','ο','π','ρ']) value) candidates;

        expected_seed := laplace.hash128_lo(
            public.laplace_hash128_blake3(convert_to(prompt,'UTF8')));
        SELECT jsonb_agg(to_jsonb(g) ORDER BY g.step,g.event,g.entity)
        INTO implicit_trace
        FROM generation.forward_program(prompt,1,0,10.0,8,NULL,0,8,NULL,ARRAY[relation_id]) g;
        SELECT jsonb_agg(to_jsonb(g) ORDER BY g.step,g.event,g.entity),
               array_agg(g.entity ORDER BY g.step) FILTER (WHERE g.event='emit')
        INTO explicit_trace,expected_ids
        FROM generation.forward_program(prompt,1,0,10.0,8,expected_seed,0,8,NULL,ARRAY[relation_id]) g;
        IF cardinality(expected_ids) IS DISTINCT FROM 1 OR NOT EXISTS (
            SELECT 1 FROM jsonb_array_elements(explicit_trace) r
            WHERE r->>'event'='emit' AND (r->>'candidate_count')::int=8) THEN
            RAISE EXCEPTION 'seed parity fixture did not execute a competitive native election';
        END IF;
        IF implicit_trace IS DISTINCT FROM explicit_trace THEN
            RAISE EXCEPTION 'omitted prompt seed differs from exact UTF-8 seed for %',prompt;
        END IF;
        SELECT array_agg(entity ORDER BY step) INTO actual_ids
        FROM generation.forward_prompt(prompt,1,0,10.0,8,NULL,0,8,NULL,ARRAY[relation_id]);
        IF actual_ids IS DISTINCT FROM expected_ids THEN
            RAISE EXCEPTION 'prompt projection changed the native omitted-seed election';
        END IF;
        SELECT array_agg(entity ORDER BY step) INTO actual_ids
        FROM generation.forward_trace(prompt,1,0,10.0,8,NULL,0,8,NULL,ARRAY[relation_id])
        WHERE event='emit';
        IF actual_ids IS DISTINCT FROM expected_ids THEN
            RAISE EXCEPTION 'trace projection changed the native omitted-seed election';
        END IF;

        SELECT array_agg(entity ORDER BY step) INTO fixed_ids
        FROM generation.forward_walk_continuations(
            ARRAY[root],1,0,10.0,8,6364136223846793005,NULL,NULL,8,NULL,NULL,ARRAY[relation_id]);
        distinguishes_fixed_default := distinguishes_fixed_default OR
            fixed_ids IS DISTINCT FROM expected_ids;
        FOREACH explicit_seed IN ARRAY ARRAY[
            0::bigint,-1::bigint,'-9223372036854775808'::bigint,'9223372036854775807'::bigint
        ] LOOP
            SELECT array_agg(entity ORDER BY step) INTO expected_ids
            FROM generation.forward_walk_continuations(
                ARRAY[root],1,0,10.0,8,explicit_seed,NULL,NULL,8,NULL,NULL,ARRAY[relation_id]);
            SELECT array_agg(entity ORDER BY step) INTO actual_ids
            FROM generation.forward_program(prompt,1,0,10.0,8,explicit_seed,0,8,NULL,ARRAY[relation_id])
            WHERE event='emit';
            IF cardinality(actual_ids) IS DISTINCT FROM 1 OR actual_ids IS DISTINCT FROM expected_ids THEN
                RAISE EXCEPTION 'prompt wrapper changed explicit signed seed %',explicit_seed;
            END IF;
        END LOOP;
        DELETE FROM laplace.consensus WHERE subject_id=root AND type_id=relation_id;
    END LOOP;
    IF NOT distinguishes_fixed_default THEN
        RAISE EXCEPTION 'seed parity fixture cannot distinguish the previous fixed default';
    END IF;
END
$seed_parity$;
ROLLBACK;
SELECT 'FORWARD_SEED_PARITY_OK';
