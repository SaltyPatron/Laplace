\set ECHO none
\pset format unaligned
\pset tuples_only on
SET client_min_messages = warning;
BEGIN;
CREATE EXTENSION IF NOT EXISTS laplace WITH SCHEMA public;
SET LOCAL search_path = public,laplace;
CREATE TEMP TABLE claim_words AS
SELECT content,laplace_id(content) AS id,laplace_tier(content) AS tier,laplace_coord(content) AS coord
FROM unnest(ARRAY['canine','hypernym','qualify','animal','antonym','source','file','fixture','@','~','#m','%s']) content;
INSERT INTO public.entity(id,tier,coord,hilbert)
SELECT id,tier,coord,laplace_hilbert(content) FROM claim_words WHERE tier>0 ON CONFLICT DO NOTHING;
INSERT INTO public.physicality(entity,tier,hilbert,path)
SELECT id,tier,laplace_hilbert(content),laplace_path(laplace_parts(content)) FROM claim_words WHERE tier>0 ON CONFLICT DO NOTHING;

-- Native's real canonical composition/containment fixture, with no synthesized
-- hashes or copied standings. The reader must preserve all four operands.
CREATE FUNCTION pg_temp.claim_node(parts blake3[], kind text, metadata bigint[] DEFAULT NULL) RETURNS blake3
LANGUAGE plpgsql AS $$
DECLARE id blake3=laplace_compose(parts); coord geometry; altitude smallint; h bigint;
        path geometry=laplace_path(parts); point geometry; i integer;
BEGIN
  -- Tier-0 pointer symbols use the same cached coordinates without storing
  -- self-referencing entity/path rows for the virtual leaf floor.
  SELECT ST_GeomFromEWKB(laplace_centroid4d_ewkb(coalesce(e.coord,w.coord))),
         (max(coalesce(e.tier,w.tier))+1)::smallint INTO coord,altitude
  FROM unnest(parts) part LEFT JOIN public.entity e ON e.id=part
  LEFT JOIN claim_words w ON w.id=part;
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
CREATE TEMP TABLE leader_witness(id blake3);
DO $$
DECLARE a blake3; b blake3; other blake3; pointer blake3; inverse blake3; holo blake3; mero blake3; rec blake3; fm blake3; ft blake3; sm blake3; witness_id blake3;
BEGIN
  a=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('hypernym'),laplace_id('qualify'),laplace_id('animal')],'claim');
  b=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('hypernym'),laplace_id('hypernym'),laplace_id('animal')],'claim');
  other=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('antonym'),laplace_id('animal')],'claim');
  pointer=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('@'),laplace_id('animal')],'claim');
  inverse=pg_temp.claim_node(ARRAY[laplace_id('animal'),laplace_id('~'),laplace_id('canine')],'claim');
  holo=pg_temp.claim_node(ARRAY[laplace_id('canine'),laplace_id('#m'),laplace_id('animal')],'claim');
  mero=pg_temp.claim_node(ARRAY[laplace_id('animal'),laplace_id('%s'),laplace_id('canine')],'claim');
  INSERT INTO leader_claims VALUES('holo',holo),('mero',mero);
  INSERT INTO leader_claims VALUES('four',a),('run',b),('other',other),('wndb',pointer),('wndb-inverse',inverse);
  rec=pg_temp.claim_node(array_fill(a,ARRAY[1000])||array_fill(b,ARRAY[1000])||array_fill(other,ARRAY[1000])||array_fill(pointer,ARRAY[1000])||array_fill(inverse,ARRAY[1000])||array_fill(holo,ARRAY[1000])||array_fill(mero,ARRAY[1000])||ARRAY[laplace_id('fixture')],
                        'record',ARRAY[2147483648,2147483648,2147483648,2147483648,2147483648,2147483648,2147483648,0]::bigint[]);
  fm=pg_temp.claim_node(ARRAY[laplace_id('file'),laplace_id('fixture')],'tuple');
  ft=pg_temp.claim_node(ARRAY[fm,rec],'file',ARRAY[4294967296,5368709120]::bigint[]);
  sm=pg_temp.claim_node(ARRAY[laplace_id('source'),laplace_id('fixture')],'tuple');
  witness_id=pg_temp.claim_node(ARRAY[sm,ft],'file',ARRAY[4294967296,5368709120]::bigint[]);
  INSERT INTO public.witness VALUES(witness_id,NULL,0.85);
  INSERT INTO leader_witness VALUES(witness_id);
END $$;
DO $$
DECLARE receipt uuid=gen_random_uuid(); published bytea;
BEGIN
  SELECT decode(id::text,'hex') INTO published FROM leader_witness;
  IF ops.native_source_completed('native-completion-fixture') THEN
    RAISE EXCEPTION 'missing Native receipt completed a source';
  END IF;
  INSERT INTO laplace.ingest_run_journal(run_id,source_name,source_id,layer,status,ended_at,files_done,files_total,attestations_measured)
  VALUES(receipt,'native-completion-fixture',published,0,'ok',clock_timestamp(),1,1,false);
  IF NOT ops.native_source_completed('native-completion-fixture') THEN
    RAISE EXCEPTION 'published Native witness and completed receipt were ignored';
  END IF;
  UPDATE laplace.ingest_run_journal SET source_id=(SELECT decode(id::text,'hex') FROM leader_claims WHERE name='four') WHERE run_id=receipt;
  IF ops.native_source_completed('native-completion-fixture') THEN
    RAISE EXCEPTION 'an unregistered claim was accepted as a published source witness';
  END IF;
  UPDATE laplace.ingest_run_journal SET source_id=NULL WHERE run_id=receipt;
  IF ops.native_source_completed('native-completion-fixture') THEN
    RAISE EXCEPTION 'a partial-file receipt completed a source';
  END IF;
  UPDATE laplace.ingest_run_journal SET source_id=published,files_done=0 WHERE run_id=receipt;
  IF ops.native_source_completed('native-completion-fixture') THEN
    RAISE EXCEPTION 'incomplete file inventory completed a source';
  END IF;
  UPDATE laplace.ingest_run_journal SET files_done=1 WHERE run_id=receipt;
  INSERT INTO laplace.ingest_run_journal(run_id,source_name,source_id,layer,status,started_at,attestations_measured)
  VALUES(gen_random_uuid(),'native-completion-fixture',published,0,'failed',clock_timestamp()+interval '1 second',false);
  IF ops.native_source_completed('native-completion-fixture') THEN
    RAISE EXCEPTION 'an older success concealed a newer failed Native ingest';
  END IF;
END $$;
CREATE UNLOGGED TABLE stage.leader_pending (claim blake3,witness blake3,lineage blake3,games bigint,score double precision,trust double precision);
DO $$ BEGIN PERFORM public.laplace_attest_series('stage.leader_pending',ARRAY(SELECT id FROM leader_claims)); END $$;
CREATE TEMP TABLE leader_page AS
SELECT * FROM ops.claim_leaders(ARRAY[(SELECT band::int FROM laplace.relation_registry() WHERE canonical='IS_A')],20);
DO $$
DECLARE a record; b record; pointer record; inverse record;
BEGIN
  SELECT p.* INTO STRICT a FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='four';
  SELECT p.* INTO STRICT b FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='run';
  SELECT p.* INTO STRICT pointer FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='wndb';
  SELECT p.* INTO STRICT inverse FROM leader_page p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text WHERE c.name='wndb-inverse';
  IF jsonb_array_length(pointer.parts)<>3 OR pointer.parts->1->>'label'<>'@'
     OR inverse.parts->0->>'label'<>'animal' OR inverse.parts->1->>'label'<>'~'
     OR inverse.parts->2->>'label'<>'canine' OR pointer.witnesses<>1 OR inverse.witnesses<>1 THEN
    RAISE EXCEPTION 'WNDB pointer selection changed source constituents or attribution';
  END IF;
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
-- The actual League request spans several bands. Exercise every declared band
-- and require the partitive page to retain both source pointer trajectories.
CREATE TEMP TABLE all_band_leaders AS
SELECT * FROM ops.claim_leaders(ARRAY(SELECT band::int FROM converse.relation_band_catalog()),20);
DO $league$
BEGIN
  IF (SELECT count(DISTINCT band) FROM all_band_leaders)<>
     (SELECT count(*) FROM converse.relation_band_catalog()) THEN
    RAISE EXCEPTION 'the League read omitted a declared band';
  END IF;
  IF (SELECT count(*) FROM all_band_leaders p JOIN leader_claims c ON encode(p.claim_id,'hex')=c.id::text
      WHERE c.name IN ('holo','mero') AND p.band=4 AND p.witnesses=1
      AND p.parts->1->>'label'=CASE c.name WHEN 'holo' THEN '#m' ELSE '%s' END)<>2 THEN
    RAISE EXCEPTION 'partitive pointer claims lost their identity, surface or attribution';
  END IF;
END $league$;
SELECT 'native_claim_leaders: declared aliases, full arity, runs and real attribution passed';
ROLLBACK;
