\set ECHO none
\set QUIET on
\pset format unaligned
\pset tuples_only on
BEGIN;
SET LOCAL search_path=public,laplace;
CREATE TEMP TABLE native_display_words AS
SELECT s,public.laplace_id(s) AS id,public.laplace_tier(s) AS tier,public.laplace_coord(s) AS coord
FROM unnest(ARRAY['dog','cat','λ','中']) s;
INSERT INTO public.entity(id,tier,coord,hilbert)
SELECT id,tier,coord,public.laplace_hilbert(s) FROM native_display_words WHERE tier>0 ON CONFLICT DO NOTHING;
INSERT INTO public.physicality(entity,tier,hilbert,path)
SELECT id,tier,public.laplace_hilbert(s),public.laplace_path(public.laplace_parts(s)) FROM native_display_words WHERE tier>0 ON CONFLICT DO NOTHING;
CREATE FUNCTION pg_temp.display_tuple(parts public.blake3[],kind text) RETURNS public.blake3
LANGUAGE plpgsql AS $$
DECLARE id public.blake3=public.laplace_compose(parts); coord geometry; tier smallint; h bigint;
BEGIN
 SELECT ST_GeomFromEWKB(public.laplace_centroid4d_ewkb(coalesce(e.coord,w.coord))),
 (max(coalesce(e.tier,w.tier))+1)::smallint INTO coord,tier
 FROM unnest(parts) part LEFT JOIN public.entity e ON e.id=part LEFT JOIN native_display_words w ON w.id=part;
 h=public.laplace_hilbert4(coord) # '-9223372036854775808'::bigint;
 INSERT INTO public.entity VALUES(id,tier,coord,h,public.laplace_mask_slot('kind',kind)) ON CONFLICT DO NOTHING;
 INSERT INTO public.physicality VALUES(id,tier,h,public.laplace_path(parts),public.laplace_mask_slot('kind',kind)) ON CONFLICT DO NOTHING;
 RETURN id;
END $$;
DO $test$
DECLARE dog public.blake3=public.laplace_id('dog'); scalar public.blake3=public.laplace_id('λ');
 row_id public.blake3; nested public.blake3; opaque public.blake3; shared public.blake3; root_a public.blake3; root_b public.blake3; claim_id public.blake3;
 absent public.blake3=public.laplace_id('native-display-absent'); labels text[]; shifted bytea[]; got bytea[];
BEGIN
 IF NOT EXISTS(SELECT FROM structural.starts_with(ARRAY[decode(dog::text,'hex')],ARRAY[laplace.word_id('d')]) WHERE entity_id=decode(dog::text,'hex'))
 OR EXISTS(SELECT FROM structural.starts_with(ARRAY[decode(dog::text,'hex')],ARRAY[laplace.word_id('o')]) WHERE entity_id=decode(dog::text,'hex')) THEN
  RAISE EXCEPTION 'Native first-constituent closure lost order or a stored word';
 END IF;
 row_id=pg_temp.display_tuple(ARRAY[dog,scalar,public.laplace_id('中')],'tuple');
 nested=pg_temp.display_tuple(ARRAY[row_id,dog,dog],'tuple');
 opaque=pg_temp.display_tuple(ARRAY[nested,row_id],'file');
 SELECT array_agg(d.label ORDER BY d.ordinal) INTO labels
 FROM realize.display_label_batch(ARRAY[decode(dog::text,'hex'),decode(row_id::text,'hex'),decode(nested::text,'hex'),
 decode(dog::text,'hex'),decode(scalar::text,'hex'),decode(opaque::text,'hex'),decode(absent::text,'hex')]) WITH ORDINALITY d(id,label,tier,ordinal);
 IF labels IS DISTINCT FROM ARRAY['dog','[dog · λ · 中]','[[dog · λ · 中] · dog × 2]','dog','λ',opaque::text,absent::text] THEN
  RAISE EXCEPTION 'Native display lost selected identities, order, runs or Unicode: %',labels;
 END IF;
 shifted=array_fill(NULL::bytea,ARRAY[2],ARRAY[0]);
 shifted[0]=decode(dog::text,'hex'); shifted[1]=decode(scalar::text,'hex');
 SELECT array_agg(d.id ORDER BY d.ordinal) INTO got
 FROM realize.display_label_batch(shifted) WITH ORDINALITY d(id,label,tier,ordinal);
 IF got IS DISTINCT FROM ARRAY[decode(dog::text,'hex'),decode(scalar::text,'hex')] THEN
  RAISE EXCEPTION 'Native display changed IDs for a non-default array lower bound';
 END IF;
 -- A binary value claim has no relation ID. Mixed endpoint batches must keep
 -- its actual endpoints without passing absent relation IDs to Native.
 SELECT array_agg(d.id ORDER BY d.ordinal) INTO got
 FROM realize.display_label_batch(ARRAY[NULL::bytea,decode(dog::text,'hex'),NULL::bytea,
 decode(scalar::text,'hex')]) WITH ORDINALITY d(id,label,tier,ordinal);
 IF got IS DISTINCT FROM ARRAY[decode(dog::text,'hex'),decode(scalar::text,'hex')]
 OR EXISTS(SELECT FROM realize.display_label_batch(ARRAY[NULL::bytea])) THEN
  RAISE EXCEPTION 'Native display did not preserve the non-null endpoint batch';
 END IF;
 IF NOT EXISTS(SELECT FROM ops.entity_facets_batch(ARRAY[decode(dog::text,'hex')]) WHERE tier=2 AND type_id IS NULL) THEN
  RAISE EXCEPTION 'Native facet guessed a semantic type';
 END IF;
 IF NOT EXISTS(SELECT FROM ops.entity_facets_batch(ARRAY[decode(scalar::text,'hex')]) WHERE tier=0 AND type_id IS NOT NULL) THEN
  RAISE EXCEPTION 'Native facet lost the virtual scalar floor';
 END IF;
 IF EXISTS(SELECT FROM ops.entity_facets_batch(ARRAY[decode(absent::text,'hex')])) THEN
  RAISE EXCEPTION 'Native facet invented presence for an absent identity';
 END IF;
 IF (SELECT count(*) FROM consensus.relation_metadata(ARRAY[decode(public.laplace_id('~')::text,'hex')]) WHERE canonical='IS_A' AND inverse AND qualifier_bit=-1)<>1 THEN
  RAISE EXCEPTION 'Native original-ID alias metadata lost inversion';
 END IF;
 -- Two distinct roots refer to one shared composition. Upward navigation must
 -- recover both through the Native codec without inventing an exclusive type.
 shared=pg_temp.display_tuple(ARRAY[dog,scalar],'tuple');
 root_a=pg_temp.display_tuple(ARRAY[scalar,shared],'file');
 root_b=pg_temp.display_tuple(ARRAY[public.laplace_id('中'),shared],'file');
 IF (SELECT count(*) FROM structural.containers_of(decode(shared::text,'hex'),1,100)
     WHERE entity_id IN (decode(root_a::text,'hex'),decode(root_b::text,'hex')) AND type_id IS NULL)<>2 THEN
  RAISE EXCEPTION 'Native containment lost shared content roots or fabricated a type';
 END IF;
 claim_id=pg_temp.display_tuple(ARRAY[shared,scalar,dog],'claim');
 INSERT INTO public.consensus(claim,rating,deviation,volatility,matches)
 VALUES(claim_id,1500,350,0.06,0) ON CONFLICT DO NOTHING;
 IF NOT EXISTS(SELECT FROM consensus.explore_web_neighbors(ARRAY[decode(shared::text,'hex')],NULL,100,false)
     WHERE nbr=decode(dog::text,'hex') AND type_id=decode(scalar::text,'hex') AND outbound) THEN
  RAISE EXCEPTION 'Native typed claims disappeared from graph navigation';
 END IF;
END $test$;
-- Recorded names may still reside on the compatibility storage face. The
-- shared display operation must preserve their semantic realization alongside
-- Native text and duplicate input positions.
DO $names$
DECLARE person bytea=public.laplace_hash128_blake3('display/recorded-person');
 name bytea=laplace.word_id('Ada'); labels text[];
BEGIN
 INSERT INTO laplace.entities(id,tier,type_id) VALUES
 (person,2,laplace.entity_type_id('Source')),(name,2,laplace.entity_type_id('Word'));
 INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
 VALUES(public.laplace_hash128_blake3('display/recorded-name'),name,1,
 'POINT ZM (0 0 0 0)'::geometry,decode(repeat('00',16),'hex'),
 public.laplace_trajectory_build(ARRAY[laplace.word_id('A'),laplace.word_id('d'),laplace.word_id('a')]),3,now());
 INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
 VALUES(laplace.consensus_id(person,laplace.relation_type_id('HAS_NAME'),name),person,
 laplace.relation_type_id('HAS_NAME'),name,1700000000000,30000000000,60000000,1,now());
 SELECT array_agg(label ORDER BY ord) INTO labels FROM
 realize.display_label_batch(ARRAY[person,laplace.word_id('dog'),person]) WITH ORDINALITY d(id,label,tier,ord);
 IF labels IS DISTINCT FROM ARRAY['Ada','dog','Ada'] THEN
  RAISE EXCEPTION 'Recorded human name lost in mixed Native display batch: %',labels;
 END IF;
END $names$;
DO $first_paths$
DECLARE cyclic bytea=public.laplace_hash128_blake3('display/cyclic-name'); got bytea[];
BEGIN
 INSERT INTO laplace.entities(id,tier,type_id) VALUES(cyclic,2,laplace.entity_type_id('Word'));
 INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
 VALUES(public.laplace_hash128_blake3('display/cyclic-name/path'),cyclic,1,
 'POINT ZM (0 0 0 0)'::geometry,decode(repeat('00',16),'hex'),
 public.laplace_trajectory_build(ARRAY[cyclic]),1,now());
 SELECT array_agg(entity_id) INTO got FROM structural.starts_with(
 ARRAY[cyclic,laplace.word_id('dog')],ARRAY[laplace.word_id('d')]);
 IF got IS DISTINCT FROM ARRAY[laplace.word_id('dog')] THEN
  RAISE EXCEPTION 'Scoped first paths lost a match or failed to terminate a cycle: %',got;
 END IF;
END $first_paths$;
ROLLBACK;
SELECT 'native_display_label: ok';
