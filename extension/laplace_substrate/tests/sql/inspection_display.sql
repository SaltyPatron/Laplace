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
    -- Nothing is recorded but the standings: the opaque type has no content and
    -- displays as its id; every row's type is what the shared display projection
    -- (consensus.relation_labels) gives its id.
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
                     WHERE type=(SELECT rl.label FROM consensus.relation_labels(ARRAY[registered_type]) rl)
                       AND object='a') THEN
        RAISE EXCEPTION 'Inspection disagrees with the shared display projection';
    END IF;
    IF (SELECT count(*) FROM ops.consensus_out_readable(subject,1))<>1
       OR EXISTS(SELECT FROM ops.consensus_out_readable(subject,0)) THEN
        RAISE EXCEPTION 'Inspection no longer respects the elected limit';
    END IF;
    RAISE NOTICE 'inspection display: opaque relation, the shared display projection, unary object and bounded election';
END
$inspection$;
ROLLBACK;
