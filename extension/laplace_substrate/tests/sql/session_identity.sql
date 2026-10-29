\set ON_ERROR_STOP on
BEGIN;
DO $$
DECLARE before_count bigint; token bytea; prompt text:='unregistered_session_probe_67d039a12';
BEGIN
  SELECT count(*) INTO before_count FROM converse.session_topics;
  PERFORM converse.recall_session(prompt);
  PERFORM converse.recall_session(prompt,NULL);
  IF (SELECT count(*) FROM converse.session_topics)<>before_count THEN
    RAISE EXCEPTION 'stateless recall wrote backend-owned conversation state';
  END IF;
  IF EXISTS((SELECT * FROM converse.recall_session(prompt) EXCEPT ALL SELECT * FROM converse.recall(prompt))
      UNION ALL (SELECT * FROM converse.recall(prompt) EXCEPT ALL SELECT * FROM converse.recall_session(prompt))) THEN
    RAISE EXCEPTION 'stateless session entry diverged from the shared recall owner';
  END IF;
  FOREACH token IN ARRAY ARRAY[convert_to('s1','UTF8'),decode('0080ff','hex'),decode('98e46355d22e01d0a21c7fd235bd0a11','hex')] LOOP
    DELETE FROM converse.session_topics WHERE session_id=token;
    PERFORM converse.recall_session(prompt,token);
    PERFORM converse.recall_session(prompt,token);
    IF (SELECT array_agg(ord ORDER BY ord) FROM converse.session_topics WHERE session_id=token)
         IS DISTINCT FROM ARRAY[1,2] THEN
      RAISE EXCEPTION 'explicit opaque session lost identity or ordered turn recording';
    END IF;
  END LOOP;
END $$;
ROLLBACK;
