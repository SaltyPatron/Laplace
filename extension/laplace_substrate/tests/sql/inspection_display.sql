\set ECHO none
-- A stored relation without a renderable surface still has a display identity.
BEGIN;
DO $inspection$
DECLARE
    subject bytea := public.blake3_send(public.laplace_text_id('L'));
    peer bytea := public.blake3_send(public.laplace_text_id('a'));
    opaque_type bytea := decode('2fd6a1e32aa77b3583a46ca38763050b','hex');
    registered_type bytea := laplace.relation_type_id('IS_A');
    opaque_label text;
BEGIN
    -- A relation's display is the Native realization of its identity. The
    -- registered relation's id is the content id of its name, so recording that
    -- name (and its constituent word IS) as content, entity + physicality as
    -- ingest records them, is what makes it render. The opaque type has no
    -- recorded content and renders as its id.
    INSERT INTO public.entity(id,tier,coord,hilbert)
    SELECT public.laplace_id(n),public.laplace_tier(n),public.laplace_coord(n),public.laplace_hilbert(n)
    FROM (VALUES ('IS'),('IS_A')) v(n) ON CONFLICT DO NOTHING;
    INSERT INTO public.physicality(entity,tier,hilbert,path)
    SELECT public.laplace_id(n),public.laplace_tier(n),public.laplace_hilbert(n),
           public.laplace_path(public.laplace_parts(n))
    FROM (VALUES ('IS'),('IS_A')) v(n) ON CONFLICT DO NOTHING;
    IF public.laplace_text_id('IS_A') <> public.blake3_from_bytea(registered_type) THEN
        RAISE EXCEPTION 'Registered relation id is not the content id of its name';
    END IF;

    INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,
        rating,rd,volatility,witness_count,last_observed_at)
    SELECT public.blake3_send(public.laplace_compose(array_remove(ARRAY[subject, t, o]::public.blake3[], NULL))),subject,t,o,
        2176851004240,139542473703,60009621,8,now()
    FROM (VALUES (opaque_type,peer),(registered_type,peer),(opaque_type,NULL::bytea)) v(t,o);

    SELECT d.label INTO opaque_label FROM (SELECT public.blake3_send(lp_l.entity) AS id,lp_l.content AS label,coalesce(lp_e.tier,CASE WHEN public.laplace_codepoint(lp_l.entity)>=0 THEN 0::smallint END) AS tier FROM public.laplace_labels(array_remove((ARRAY[opaque_type])::bytea[],NULL)::public.blake3[]) lp_l LEFT JOIN public.laplace_entities(array_remove((ARRAY[opaque_type])::bytea[],NULL)::public.blake3[]) lp_e ON lp_e.ordinal=lp_l.i) d;
    IF opaque_label IS NULL OR position(left(encode(opaque_type,'hex'),12) IN opaque_label)=0 THEN
        RAISE EXCEPTION 'Opaque display lost its identity';
    END IF;
    IF (SELECT count(*) FROM ops.consensus_out_readable(subject,40))<>3
       OR EXISTS(SELECT FROM ops.consensus_out_readable(subject,40) WHERE type IS NULL) THEN
        RAISE EXCEPTION 'Readable inspection dropped a relation or returned NULL type';
    END IF;
    IF (SELECT count(*) FROM ops.consensus_out_readable(subject,40) WHERE object IS NULL)<>1 THEN
        RAISE EXCEPTION 'Unary inspection row lost its NULL object';
    END IF;
    IF NOT EXISTS(SELECT FROM ops.consensus_out_readable(subject,40)
                  WHERE type=opaque_label AND object='a')
       OR NOT EXISTS(SELECT FROM ops.consensus_out_readable(subject,40)
                     WHERE type='IS_A' AND object='a') THEN
        RAISE EXCEPTION 'Inspection disagrees with the shared display projection';
    END IF;
    IF (SELECT count(*) FROM ops.consensus_out_readable(subject,1))<>1
       OR EXISTS(SELECT FROM ops.consensus_out_readable(subject,0)) THEN
        RAISE EXCEPTION 'Inspection no longer respects the elected limit';
    END IF;
    RAISE NOTICE 'inspection display: opaque relation, recorded relation name, unary object and bounded election';
END
$inspection$;
ROLLBACK;
