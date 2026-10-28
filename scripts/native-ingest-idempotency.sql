-- Stream an exact, ordered Native source snapshot to the operator's checksum.
-- COPY keeps memory bounded; no full-corpus string/array aggregate is built.
COPY (
    WITH latest AS MATERIALIZED (
        SELECT encode(source_id,'hex')::public.blake3 AS id
        FROM laplace.ingest_run_journal
        WHERE source_name=:'source' ORDER BY started_at DESC LIMIT 1
    ), series AS MATERIALIZED (
        SELECT to_jsonb(s) AS value
        FROM public.laplace_contained_series(ARRAY(SELECT id FROM latest)) s
    ), snapshot AS (
        SELECT 0 AS kind,w.id::text AS id,to_jsonb(w)::text AS value
        FROM latest JOIN public.witness w USING(id)
        UNION ALL
        SELECT 1,s.value->>'claim',jsonb_build_object(
            'series',s.value,'standing',to_jsonb(c))::text
        FROM series s LEFT JOIN public.consensus c
          ON c.claim=(s.value->>'claim')::public.blake3
    )
    SELECT kind,id,value FROM snapshot ORDER BY kind,id,value
) TO STDOUT (FORMAT binary);
