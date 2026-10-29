-- Canonical Native claims, not legacy cell fixtures. Endpoint indexes must
-- preserve direction and RLE constituents while ignoring predicate-only hits.
BEGIN;
CREATE FUNCTION pg_temp.endpoint_claim(parts public.blake3[]) RETURNS public.blake3
LANGUAGE plpgsql AS $$
DECLARE id public.blake3 := public.laplace_compose(parts);
        point public.geometry;
        mask bit(64) := public.laplace_mask_slot('kind','claim');
BEGIN
    SELECT public.ST_GeomFromEWKB(public.laplace_centroid4d_ewkb(
        public.ST_GeomFromEWKB(public.laplace_cp_coord_ewkb(public.laplace_codepoint(p)))))
    INTO point FROM unnest(parts) p;
    INSERT INTO public.entity(id,tier,coord,hilbert,mask)
    VALUES(id,1,point,public.laplace_hilbert4(point),mask);
    INSERT INTO public.physicality(entity,tier,hilbert,path,mask)
    VALUES(id,1,public.laplace_hilbert4(point),public.laplace_path(parts),mask);
    INSERT INTO public.consensus(claim,rating,deviation,volatility,matches)
    VALUES(id,1500,50,0.06,1);
    RETURN id;
END $$;
CREATE FUNCTION pg_temp.evidence_source(parts public.blake3[], metadata bigint[], named boolean DEFAULT true) RETURNS public.blake3
LANGUAGE plpgsql AS $$
DECLARE id public.blake3:=public.laplace_compose(parts); point public.geometry;
        path public.geometry:=public.laplace_path(parts); vertex public.geometry; altitude smallint;
        mask bit(64):=CASE WHEN named THEN public.laplace_mask_slot('kind','file') ELSE B'0'::bit(64) END;
BEGIN
 SELECT public.ST_GeomFromEWKB(public.laplace_centroid4d_ewkb(coalesce(e.coord,
   public.ST_GeomFromEWKB(public.laplace_cp_coord_ewkb(public.laplace_codepoint(p)))))),
   (max(coalesce(e.tier,0))+1)::smallint INTO point,altitude
 FROM unnest(parts) p LEFT JOIN public.entity e ON e.id=p;
 FOR i IN 1..cardinality(metadata) LOOP
  vertex:=public.ST_PointN(path,i);
  path:=public.ST_SetPoint(path,i-1,public.ST_MakePoint(public.ST_X(vertex),public.ST_Y(vertex),
    public.ST_Z(vertex),public.ST_M(vertex)+metadata[i]));
 END LOOP;
 INSERT INTO public.entity(id,tier,coord,hilbert,mask)
 VALUES(id,altitude,point,public.laplace_hilbert4(point),mask);
 INSERT INTO public.physicality(entity,tier,hilbert,path,mask)
 VALUES(id,altitude,public.laplace_hilbert4(point),path,mask);
 INSERT INTO public.witness(id,trust) VALUES(id,0.75);
 RETURN id;
END $$;
DO $$
DECLARE a public.blake3:=public.laplace_cp_id(ascii('α'));
        b public.blake3:=public.laplace_cp_id(ascii('β'));
        c public.blake3:=public.laplace_cp_id(ascii('γ'));
        r public.blake3:=public.laplace_cp_id(ascii('δ'));
        aa bytea:=public.blake3_send(a);
        bb bytea:=public.blake3_send(b);
        rr bytea:=public.blake3_send(r);
        claim public.blake3;
        witness public.blake3;
        n bigint;
BEGIN
    PERFORM pg_temp.endpoint_claim(ARRAY[a,r,b]);
    PERFORM pg_temp.endpoint_claim(ARRAY[b,r,a]);
    PERFORM pg_temp.endpoint_claim(ARRAY[a,a,b]); -- first two parts share an RLE vertex
    PERFORM pg_temp.endpoint_claim(ARRAY[c,a,b]); -- a is only the predicate
    PERFORM pg_temp.endpoint_claim(ARRAY[a,b]);   -- a value claim, not a binary relation
    claim:=public.laplace_compose(ARRAY[a,r,b]);
    -- Two actual source records: one affirmation and one refutation of the
    -- same canonical claim. One witness is not two observations, and the
    -- combined series must never be presented as a confirmation.
    witness:=pg_temp.evidence_source(ARRAY[public.laplace_cp_id(ascii('Ω')),claim,c,claim],
        ARRAY[0,2147483648,0,19327352832]::bigint[]);
    IF NOT EXISTS(SELECT FROM ops.evidence_series_receipt(aa,100,false)
        WHERE claim_id=public.blake3_send(claim) AND source_id=public.blake3_send(witness)
          AND games=2 AND tokens=2 AND score=1 AND trust=0.75 AND source_label='Ω')
       OR NOT EXISTS(SELECT FROM ops.evidence_series_receipt(bb,100,true)
        WHERE claim_id=public.blake3_send(claim) AND object_id=aa AND games=2 AND score=1)
       OR NOT EXISTS(SELECT FROM ops.evidence_receipt(aa,100)
        WHERE type_id=rr AND object_id=bb AND witness_count=1 AND source_labels='Ω') THEN
        RAISE EXCEPTION 'shared evidence receipt lost direction, source, series or witness cardinality';
    END IF;
    -- Single-entity inspection and the UI's batch geometry query share the
    -- Native exact-ID reader. A repeated request stays a repeated occurrence.
    IF (SELECT array_agg(p.ordinal ORDER BY p.ordinal)
        FROM ops.entity_physicalities(ARRAY[
          public.blake3_send(public.laplace_compose(ARRAY[a,r,b])),
          NULL,
          decode(repeat('00',16),'hex'),
          public.blake3_send(public.laplace_compose(ARRAY[a,r,b]))]) p WHERE p.type=1)
       IS DISTINCT FROM ARRAY[1,4]::bigint[] THEN
        RAISE EXCEPTION 'batch geometry lost duplicate positions or invented a missing entity';
    END IF;
    IF (SELECT count(*) FROM ops.entity_physicalities(
          public.blake3_send(public.laplace_compose(ARRAY[a,r,b]))))<>1
       OR EXISTS(SELECT FROM ops.entity_physicalities(ARRAY[]::bytea[])) THEN
        RAISE EXCEPTION 'single and batch geometry projections disagree';
    END IF;
    -- Reuse the actual SPI plans beyond PostgreSQL's custom-plan warmup.
    FOR reading IN 1..8 LOOP
    SELECT count(*) INTO n FROM consensus.explore_induced_edges(ARRAY[aa,bb,aa,NULL]);
    IF n<>3 THEN RAISE EXCEPTION 'endpoint read lost or duplicated canonical edges: %',n; END IF;
    IF NOT EXISTS(SELECT FROM consensus.explore_induced_edges(ARRAY[aa,bb])
        WHERE source_id=aa AND type_id=aa AND object_id=bb) THEN
        RAISE EXCEPTION 'endpoint read lost repeated subject/predicate constituents';
    END IF;
    IF NOT EXISTS(SELECT FROM consensus.explore_web_neighbors(ARRAY[bb],100)
        WHERE nbr=public.blake3_send(c) AND type_id=aa AND NOT outbound) THEN
        RAISE EXCEPTION 'object-only endpoint read lost an inbound relation';
    END IF;
    SELECT count(*) INTO n FROM consensus.explore_web_neighbors(ARRAY[aa],ARRAY[rr],100,false);
    IF n<>1 THEN RAISE EXCEPTION 'endpoint type restriction leaked another predicate: %',n; END IF;
    END LOOP;
    -- The public query shape must consume these canonical claims as inspection
    -- does, even though none of their source-defined predicates has a legacy
    -- registry row. Unknown legacy labels must not manufacture an empty answer.
    IF NOT EXISTS(SELECT FROM converse.recall_intent('describe',aa)
        WHERE reply='δ: β' AND eff_mu=1400 AND witnesses=1) THEN
        RAISE EXCEPTION 'describe discarded a canonical source-defined relation';
    END IF;
    witness:=pg_temp.evidence_source(ARRAY[claim,c],ARRAY[1073741824,0]::bigint[],false);
    IF NOT EXISTS(SELECT FROM ops.evidence_series_receipt(aa,100,false)
        WHERE source_id=public.blake3_send(witness) AND source_label=witness::text) THEN
        RAISE EXCEPTION 'an unnamed witness was mislabeled as its first claim';
    END IF;
END $$;
ROLLBACK;
SELECT 'Native endpoint reads: direction, type, RLE, duplicate inputs and value claims passed';
