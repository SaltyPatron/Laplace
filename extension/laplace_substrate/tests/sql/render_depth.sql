\set ECHO none
\pset format unaligned
\pset tuples_only on
BEGIN;
SET LOCAL max_stack_depth='100kB';
-- Genuine content identities and indexed Content carriers. Only the explicit
-- corruption probes override an identity; no corpus or installed state changes.
CREATE FUNCTION pg_temp.render_fixture_node(children bytea[], runs integer[],
    flags bigint[] DEFAULT NULL, forced_id bytea DEFAULT NULL) RETURNS bytea
LANGUAGE plpgsql AS $$
DECLARE root bytea; expanded bytea[]; logical integer;
BEGIN
    SELECT array_agg(children[i] ORDER BY i,n),count(*)::integer INTO expanded,logical
    FROM generate_subscripts(children,1) i CROSS JOIN LATERAL generate_series(1,runs[i]) n;
    root:=coalesce(forced_id,public.laplace_hash128_merkle(255::smallint,expanded));
    INSERT INTO laplace.entities(id,tier,type_id)
    VALUES(root,255,laplace.entity_type_id('Text')) ON CONFLICT DO NOTHING;
    INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
    VALUES(public.laplace_hash128_blake3(root||decode('0100','hex')),root,1,
      public.ST_MakePoint(0,0,0,0),decode(repeat('00',16),'hex'),
      public.ST_MakeLine(ARRAY(SELECT public.laplace_mantissa_pack(children[i],i,runs[i],coalesce(flags[i],0))
          FROM generate_subscripts(children,1) i ORDER BY i)),logical);
    RETURN root;
END $$;
DO $$
DECLARE a bytea:=laplace.word_id('A'); x bytea:=laplace.word_id('x'); z bytea;
    root bytea:=a; repeated bytea; unknown bytea; spoof bytea; cycle_a bytea; cycle_b bytea;
    expected text; actual text[];
BEGIN
    FOR i IN 1..4096 LOOP
        root:=pg_temp.render_fixture_node(ARRAY[root,x],ARRAY[1,1]);
    END LOOP;
    expected:='A'||repeat('x',4096);
    actual:=realize.render_text_batch(ARRAY[root,root,a,NULL]);
    IF actual IS DISTINCT FROM ARRAY[expected,expected,'A',NULL] THEN
        RAISE EXCEPTION 'deep iterative realization lost order, shared roots, or null occurrences';
    END IF;
    IF realize.render_text(root,4) IS NOT NULL THEN
        RAISE EXCEPTION 'explicit depth preview invented missing constituents';
    END IF;
    repeated:=pg_temp.render_fixture_node(ARRAY[root,x],ARRAY[3,2]);
    IF realize.render_text(repeated) IS DISTINCT FROM repeat(expected,3)||'xx' THEN
        RAISE EXCEPTION 'realization lost a repeated composite or scalar run';
    END IF;
    z:=public.blake3_send(public.laplace_cp_id(0));
    repeated:=pg_temp.render_fixture_node(ARRAY[a,z,x],ARRAY[1,2,1]);
    IF realize.render_text(repeated) IS NOT NULL OR
       realize.render_bytes(repeated) IS DISTINCT FROM convert_to('A','UTF8')||decode('0000','hex')||convert_to('x','UTF8') THEN
        RAISE EXCEPTION 'length-bearing realization lost Unicode NUL';
    END IF;
    unknown:=public.laplace_hash128_blake3('render-depth-unrecorded-child');
    spoof:=pg_temp.render_fixture_node(ARRAY[a,unknown],ARRAY[1,1],ARRAY[0::bigint,1::bigint|(66::bigint<<31)]);
    IF realize.render_text(spoof) IS NOT NULL THEN
        RAISE EXCEPTION 'physicality annotation manufactured a scalar with a different content identity';
    END IF;
    cycle_a:=public.laplace_hash128_blake3('render-depth-corrupt-cycle-a');
    cycle_b:=public.laplace_hash128_blake3('render-depth-corrupt-cycle-b');
    PERFORM pg_temp.render_fixture_node(ARRAY[cycle_b,x],ARRAY[1,1],NULL,cycle_a);
    PERFORM pg_temp.render_fixture_node(ARRAY[cycle_a,x],ARRAY[1,1],NULL,cycle_b);
    actual:=realize.render_text_batch(ARRAY[cycle_a,root,cycle_b,root]);
    IF actual IS DISTINCT FROM ARRAY[NULL,expected,NULL,expected] THEN
        RAISE EXCEPTION 'cycle handling poisoned an independent selected root';
    END IF;
END $$;
ROLLBACK;
SELECT 'realization: deep occurrences, ordered runs, identity-bound scalars, NUL and cycles passed';
