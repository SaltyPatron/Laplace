\set ECHO none
\pset format unaligned
\pset tuples_only on
SET client_min_messages = warning;
BEGIN;
CREATE EXTENSION IF NOT EXISTS laplace WITH SCHEMA public;
SET LOCAL search_path = public,laplace;
CREATE TEMP TABLE claim_words AS SELECT unnest(ARRAY['canine','hypernym','qualify','animal','antonym','source','file','fixture']) content;
INSERT INTO public.entity(id,tier,coord,hilbert)
SELECT laplace_id(content),laplace_tier(content),laplace_coord(content),laplace_hilbert(content) FROM claim_words ON CONFLICT DO NOTHING;
INSERT INTO public.physicality(entity,tier,hilbert,path)
SELECT laplace_id(content),laplace_tier(content),laplace_hilbert(content),laplace_path(laplace_parts(content)) FROM claim_words ON CONFLICT DO NOTHING;

-- Native's real canonical composition/containment fixture, with no synthesized
-- hashes or copied standings. The reader must preserve all four operands.
CREATE FUNCTION pg_temp.claim_node(parts blake3[], kind text, metadata bigint[] DEFAULT NULL) RETURNS blake3
LANGUAGE plpgsql AS $$
DECLARE id blake3=laplace_compose(parts); coord geometry; altitude smallint; h bigint;
        path geometry=laplace_path(parts); point geometry; i integer;
BEGIN
  SELECT ST_GeomFromEWKB(laplace_centroid4d_ewkb(e.coord)),(max(e.tier)+1)::smallint INTO coord,altitude
  FROM unnest(parts) part JOIN public.entity e ON e.id=part;
  h=laplace_hilbert4(coord) # '-9223372036854775808'::bigint;
  IF metadata IS NOT NULL THEN
    IF cardinality(metadata)<>ST_NPoints(path) THEN RAISE EXCEPTION 'fixture metadata does not match runs'; END IF;
    FOR i IN 1..cardinality(metadata) LOOP
      point=ST_PointN(path,i);
      path=ST_SetPoint(path,i-1,ST_MakePoint(ST_X(point),ST_Y(point),ST_Z(point),ST_M(point)+metadata[i]));
    END LOOP;
  END IF;
  INSERT INTO public.entity VALUES(id,altitude,coord,h,laplace_mask_slot('kind',kind)) ON CONFLICT DO NOTHING;
  INSERT INTO public.physicality VALUES(id,altitude,h,path,laplace_mask_slot('kind',kind)) ON CONFLICT DO NOTHING;
  RETURN id;
END $$;
CREATE TEMP TABLE leader_claims(name text,id blake3);
DO $$
DECLARE a blake3; b blake3; other blake3; rec blake3; fm blake3; ft blake3; sm blake3; witness_id blake3;
BEGIN
  a=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('hypernym'),laplace_id('qualify'),laplace_id('animal')],'claim');
  b=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('hypernym'),laplace_id('hypernym'),laplace_id('animal')],'claim');
  other=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('antonym'),laplace_id('animal')],'claim');
  INSERT INTO leader_claims VALUES('four',a),('run',b),('other',other);
  rec=pg_temp.claim_node(array_fill(a,ARRAY[1000])||array_fill(b,ARRAY[1000])||array_fill(other,ARRAY[1000])||ARRAY[laplace_id('fixture')],
                        'record',ARRAY[2147483648,2147483648,2147483648,0]::bigint[]);
  fm=pg_temp.claim_node(ARRAY[laplace_id('file'),laplace_id('fixture')],'tuple');
  ft=pg_temp.claim_node(ARRAY[fm,rec],'file',ARRAY[4294967296,5368709120]::bigint[]);
  sm=pg_temp.claim_node(ARRAY[laplace_id('source'),laplace_id('fixture')],'tuple');
  witness_id=pg_temp.claim_node(ARRAY[sm,ft],'file',ARRAY[4294967296,5368709120]::bigint[]);
  INSERT INTO public.witness VALUES(witness_id,NULL,0.85);
END $$;
CREATE UNLOGGED TABLE stage.leader_pending (claim blake3,witness blake3,lineage blake3,games bigint,score double precision,trust double precision);
DO $$ BEGIN PERFORM public.laplace_attest_series('stage.leader_pending',ARRAY(SELECT id FROM leader_claims)); END $$;
CREATE TEMP TABLE leader_page AS
SELECT * FROM ops.claim_leaders(ARRAY[(SELECT band::int FROM laplace.relation_registry() WHERE canonical='IS_A')],20);
DO $$
DECLARE a record; b record;
BEGIN
  SELECT p.* INTO STRICT a FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='four';
  SELECT p.* INTO STRICT b FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='run';
  IF jsonb_array_length(a.parts)<>4 OR a.parts->2->>'label'<>'qualify'
     OR a.parts->0->>'label'<>'canine' OR a.parts->3->>'label'<>'animal'
     OR jsonb_array_length(b.parts)<>3 OR b.parts->1->>'run'<>'2' THEN
    RAISE EXCEPTION 'leader read reduced or reordered canonical constituents';
  END IF;
  IF a.witnesses<>1 OR b.witnesses<>1 OR a.confidence NOT BETWEEN 0 AND 1
     OR a.trajectory<>(SELECT encode(ST_AsEWKB(path),'hex') FROM public.physicality WHERE entity=(SELECT id FROM leader_claims WHERE name='four')) THEN
    RAISE EXCEPTION 'leader read lost Native confidence, attribution or trajectory';
  END IF;
  IF EXISTS (SELECT FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='other') THEN
    RAISE EXCEPTION 'an alias from another declared arena entered the selected band';
  END IF;
END $$;
SELECT 'native_claim_leaders: declared aliases, full arity, runs and real attribution passed';
ROLLBACK;
