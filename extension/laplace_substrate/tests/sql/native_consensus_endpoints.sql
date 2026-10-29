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
DO $$
DECLARE a public.blake3:=public.laplace_cp_id(ascii('α'));
        b public.blake3:=public.laplace_cp_id(ascii('β'));
        c public.blake3:=public.laplace_cp_id(ascii('γ'));
        r public.blake3:=public.laplace_cp_id(ascii('δ'));
        aa bytea:=public.blake3_send(a);
        bb bytea:=public.blake3_send(b);
        rr bytea:=public.blake3_send(r);
        n bigint;
BEGIN
    PERFORM pg_temp.endpoint_claim(ARRAY[a,r,b]);
    PERFORM pg_temp.endpoint_claim(ARRAY[b,r,a]);
    PERFORM pg_temp.endpoint_claim(ARRAY[a,a,b]); -- first two parts share an RLE vertex
    PERFORM pg_temp.endpoint_claim(ARRAY[c,a,b]); -- a is only the predicate
    PERFORM pg_temp.endpoint_claim(ARRAY[a,b]);   -- a value claim, not a binary relation
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
        WHERE reply='δ: β' AND eff_mu=1400 AND witnesses=0) THEN
        RAISE EXCEPTION 'describe discarded a canonical source-defined relation';
    END IF;
END $$;
ROLLBACK;
SELECT 'Native endpoint reads: direction, type, RLE, duplicate inputs and value claims passed';
