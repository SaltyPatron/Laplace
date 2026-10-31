\set ON_ERROR_STOP on
\timing on
INSERT INTO laplace.entities(id,tier,type_id) VALUES
(decode('b7b840fb843e7b1098cd32a101a2978d','hex'),4,decode('aee37bba88f3c36bb240adab0a7444ee','hex')),
(decode('be7353b51f2d16f48fcdbe7d88b48e34','hex'),4,decode('aee37bba88f3c36bb240adab0a7444ee','hex')) ON CONFLICT DO NOTHING;
LOAD 'auto_explain';
SET auto_explain.log_min_duration=0;
SET auto_explain.log_nested_statements=on;
SET auto_explain.log_analyze=on;
SET auto_explain.log_buffers=on;
SET auto_explain.log_format=json;
SET auto_explain.log_level=notice;
SET client_min_messages=notice;
SELECT * FROM realize.display_label_batch(ARRAY[decode('b7b840fb843e7b1098cd32a101a2978d','hex'),decode('be7353b51f2d16f48fcdbe7d88b48e34','hex')]);
SELECT * FROM realize.display_label_batch(ARRAY[decode('b7b840fb843e7b1098cd32a101a2978d','hex'),decode('be7353b51f2d16f48fcdbe7d88b48e34','hex')]);
RESET auto_explain.log_min_duration;
