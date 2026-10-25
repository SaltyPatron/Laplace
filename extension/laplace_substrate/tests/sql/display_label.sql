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
 row_id public.blake3; nested public.blake3; opaque public.blake3; absent public.blake3=public.laplace_id('native-display-absent'); labels text[]; shifted bytea[]; got bytea[];
BEGIN
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
 IF NOT EXISTS(SELECT FROM ops.entity_facets_batch(ARRAY[decode(dog::text,'hex')]) WHERE tier=1 AND type_id IS NULL) THEN
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
END $test$;
ROLLBACK;
SELECT 'native_display_label: ok';
