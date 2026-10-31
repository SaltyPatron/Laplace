\set ON_ERROR_STOP on
DO $bench$
DECLARE started timestamptz;
BEGIN
  FOR i IN 1..10 LOOP
    started := clock_timestamp();
    PERFORM * FROM realize.display_label_batch(ARRAY[decode('b7b840fb843e7b1098cd32a101a2978d','hex'),decode('be7353b51f2d16f48fcdbe7d88b48e34','hex')]);
    RAISE NOTICE 'display iteration %: % ms', i, extract(epoch from clock_timestamp()-started)*1000;
  END LOOP;
END $bench$;
