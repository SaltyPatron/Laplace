\set ON_ERROR_STOP on
\ir render-boundary-fixture.sql
SELECT realize.forward_text_batch(ARRAY[public.laplace_hash128_blake3('test/render-boundary/concept')]);
SELECT funcid::regprocedure,calls FROM pg_stat_xact_user_functions
WHERE funcid IN ('realize.batch(bytea[],bytea)'::regprocedure,'realize.render_text_batch(bytea[],integer)'::regprocedure)
ORDER BY funcid;
SELECT realize.forward_text_batch(ARRAY[public.laplace_hash128_blake3('test/render-boundary/named')],laplace.word_id('deu')) AS requested_german;
SELECT converse.label_is_content('New York') AS rejects_multiword,
       converse.label_is_content('i46360') AS accepts_bare_ili;
ROLLBACK;
