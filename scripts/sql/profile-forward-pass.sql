-- Profile the installed native program and its text realization without retaining
-- prompt admission/session effects. No build, corpus ingest, or service restart.
-- psql ... -v prompt='your prompt' -f scripts/sql/profile-forward-pass.sql
\set ON_ERROR_STOP on
\if :{?prompt}
\else
\set prompt 'The opposite of hot is'
\endif
\timing on
BEGIN;
SET LOCAL statement_timeout = '30s';
SELECT tier, count(*) AS nodes FROM converse.prompt_tree(:'prompt') GROUP BY tier ORDER BY tier;
SELECT step, event, converse.label_or_hex(entity) AS entity,
       candidate_count, exact_channel_count, routing_round,
       required_obligations, satisfied_obligations, remaining_required,
       completion, disposition, encode(semantic_act_id,'hex') AS semantic_act,
       output_count
FROM generation.forward_program(:'prompt',24,5,0.0,10,NULL,2,8,NULL,NULL)
ORDER BY step, routing_round;
SELECT * FROM generation.forward_text(:'prompt',24,5,0.0,10,NULL,2,8,NULL)
ORDER BY step;
ROLLBACK;
