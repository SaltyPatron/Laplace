BEGIN;
DO $$
DECLARE program jsonb:=converse.query_program(); malformed jsonb;
BEGIN
 IF EXISTS(SELECT FROM ops.declared_paths('{}',program))
 OR EXISTS(SELECT FROM ops.declared_paths(ARRAY[decode(repeat('00',16),'hex')],program))
 OR EXISTS(SELECT FROM ops.declared_paths(NULL,program)) THEN
  RAISE EXCEPTION 'Empty or missing declared roots produced a path';
 END IF;
 FOREACH malformed IN ARRAY ARRAY['[]','{}','{"chains":[]}',
   '{"chains":[[]]}','{"chains":[["bad"]]}',
   '{"chains":[[{"entity":"01"}]]}']::jsonb[] LOOP
  BEGIN
   PERFORM * FROM ops.declared_paths('{}',malformed);
   RAISE EXCEPTION 'Malformed query program was accepted';
  EXCEPTION WHEN invalid_parameter_value THEN NULL;
  END;
 END LOOP;
 BEGIN
  PERFORM * FROM ops.declared_paths(ARRAY[decode('01','hex')],program);
  RAISE EXCEPTION 'Short query root was accepted';
 EXCEPTION WHEN invalid_parameter_value THEN NULL;
 END;
 BEGIN
  PERFORM * FROM ops.declared_paths(ARRAY[NULL::bytea],program);
  RAISE EXCEPTION 'Null query root was accepted';
 EXCEPTION WHEN null_value_not_allowed THEN NULL;
 END;
 BEGIN
  PERFORM * FROM ops.declared_paths('{}',program,0);
  RAISE EXCEPTION 'Invalid query capacity was accepted';
 EXCEPTION WHEN invalid_parameter_value THEN NULL;
 END;
 BEGIN
  PERFORM * FROM ops.declared_paths(ARRAY[decode(repeat('00',16),'hex'),decode(repeat('01',16),'hex')],program,1);
  RAISE EXCEPTION 'Oversized initial frontier was accepted';
 EXCEPTION WHEN program_limit_exceeded THEN NULL;
 END;
END $$;
ROLLBACK;
