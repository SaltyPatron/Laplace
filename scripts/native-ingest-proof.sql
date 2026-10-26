-- Native source proof: actual published witness and every selected file receipt.
-- The caller replaces __SOURCE_LITERAL__ with one SQL-quoted catalog name.
WITH latest AS MATERIALIZED (
    SELECT * FROM laplace.ingest_run_journal
    WHERE source_name=__SOURCE_LITERAL__ ORDER BY started_at DESC LIMIT 1
), files AS MATERIALIZED (
    SELECT f.* FROM laplace.ingest_file_journal f JOIN latest j USING(run_id)
), ids AS MATERIALIZED (
    SELECT encode(file_id,'hex')::public.blake3 AS id FROM files WHERE file_id IS NOT NULL
    UNION SELECT encode(source_id,'hex')::public.blake3 FROM latest WHERE source_id IS NOT NULL
), pairs AS MATERIALIZED (
    SELECT count(*) AS requested,
           count(*) FILTER(WHERE e.id IS NOT NULL AND p.entity IS NOT NULL) AS present
    FROM ids LEFT JOIN public.entity e ON e.id=ids.id
             LEFT JOIN public.physicality p ON p.entity=ids.id
)
SELECT jsonb_build_object(
    'run_id',j.run_id,'source_id',encode(j.source_id,'hex'),'layer',j.layer,
    'source_complete',ops.native_source_completed(__SOURCE_LITERAL__) AND j.phase='complete',
    'files_total',j.files_total,'files_received',(SELECT count(*) FROM files),
    'files_complete',(SELECT count(*) FROM files WHERE status='ok' AND ended_at IS NOT NULL
                      AND (records=0 OR file_id IS NOT NULL)),
    'ids_requested',pairs.requested,'ids_present',pairs.present,
    'series',CASE WHEN j.status='ok' AND j.ended_at IS NOT NULL AND j.source_id IS NOT NULL
             THEN (SELECT count(*) FROM public.laplace_contained_series(
                    ARRAY[encode(j.source_id,'hex')::public.blake3])) ELSE 0 END)
FROM latest j CROSS JOIN pairs;
