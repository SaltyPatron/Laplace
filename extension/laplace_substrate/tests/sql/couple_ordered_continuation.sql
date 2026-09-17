-- Exact whole-observation continuation participates in COUPLE before ORIENT.
\set ECHO none
BEGIN;
DO $couple_ordered_continuation$
DECLARE
    prompt text := 'zzcouplealphaqv zzcouplebetaqv';
    successor bytea := laplace.word_id('zzcouplegammaqv');
    sequence_root bytea := public.laplace_hash128_blake3('test/coupling/ordered-sequence');
    context_ids bytea[];
    context_tiers smallint[];
    context_count int;
    trajectory geometry;
    before_program bytea;
    after_program bytea;
    emitted bytea;
    emitted_stride int;
BEGIN
    -- Keep the request byte-identical. Before the observation exists there is
    -- no exact ordered continuation in its physicality response field.
    SELECT p.program_id
      INTO before_program
      FROM generation.forward_program(prompt,1,5,0.0,8,7,0,64,NULL,NULL) p
     WHERE p.event IN ('complete','unresolved')
     ORDER BY p.step DESC
     LIMIT 1;
    IF before_program IS NULL THEN
        RAISE EXCEPTION 'FAIL: baseline forward program produced no receipt';
    END IF;

    -- Reconstruct the exact tier-2 execution cut owned by laplace_prompt_input.
    -- Whitespace or lower-tier leaves remain present whenever no tier-2 parent
    -- covers them, so the observed sequence is the exact admitted operand order.
    WITH tree AS MATERIALIZED (
        SELECT * FROM converse.prompt_tree(prompt,false)
    ), cut AS (
        SELECT t.id,t.tier,t.byte_offset,t.node_index
          FROM tree t
          LEFT JOIN tree parent ON parent.node_index=t.parent_index
         WHERE t.tier <= 2
           AND (t.parent_index IS NULL OR parent.tier > 2)
    )
    SELECT array_agg(id ORDER BY byte_offset,node_index),
           array_agg(tier ORDER BY byte_offset,node_index)
      INTO context_ids,context_tiers
      FROM cut;
    context_count := cardinality(context_ids);
    IF context_count IS NULL OR context_count < 2 THEN
        RAISE EXCEPTION 'FAIL: exact prompt execution cut is unexpectedly empty: %',context_count;
    END IF;

    SELECT public.ST_MakeLine(array_agg(point ORDER BY ordinal))
      INTO trajectory
      FROM (
          SELECT i AS ordinal,
                 public.laplace_mantissa_pack(
                     context_ids[i],i,1,(context_tiers[i]::bigint << 1)) AS point
            FROM generate_subscripts(context_ids,1) AS g(i)
          UNION ALL
          SELECT context_count + 1,
                 public.laplace_mantissa_pack(successor,context_count + 1,1,(2::bigint << 1))
      ) observed;

    INSERT INTO laplace.physicalities
        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
    VALUES
        (public.laplace_hash128_blake3('test/coupling/ordered-physicality'),
         sequence_root,1,public.ST_MakePoint(11,11,11,11),
         decode(repeat('00',16),'hex'),trajectory,context_count + 1,now());

    -- The same request must now fingerprint different pre-ORIENT state because
    -- cognition_program fingerprints intent->structural before task orientation.
    -- The exact continuation must also remain the later proposal/selection route.
    SELECT (array_agg(p.program_id ORDER BY p.step DESC))[1],
           (array_agg(p.entity ORDER BY p.step) FILTER (WHERE p.event='emit'))[1],
           (array_agg(p.stride_used ORDER BY p.step) FILTER (WHERE p.event='emit'))[1]
      INTO after_program,emitted,emitted_stride
      FROM generation.forward_program(prompt,1,5,0.0,8,7,0,64,NULL,NULL) p;

    IF after_program IS NULL OR after_program = before_program THEN
        RAISE EXCEPTION
            'FAIL: exact ordered observation did not alter the pre-ORIENT coupling program';
    END IF;
    IF emitted IS DISTINCT FROM successor THEN
        RAISE EXCEPTION
            'FAIL: exact ordered continuation was absent from the selected physicality route: %',emitted;
    END IF;
    IF emitted_stride IS DISTINCT FROM context_count THEN
        RAISE EXCEPTION
            'FAIL: ordered continuation lost whole-observation stride: got %, expected %',
            emitted_stride,context_count;
    END IF;

    RAISE NOTICE 'ordered continuation: exact observation changed pre-ORIENT program state and remained the selected sequence route';
END
$couple_ordered_continuation$;
ROLLBACK;
