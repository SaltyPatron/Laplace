#!/usr/bin/env bash
# Tier sweep: exercise every installed SQL function at one call-graph tier and record
# what it costs. See docs/read-path.md and docs/sql-cascade.md.
#
#   scripts/sql-tier-sweep.sh 0
#
# WHY THIS IS A SHELL LOOP AND NOT A PROCEDURE. statement_timeout bounds the TOP-LEVEL
# statement. Inside a plpgsql procedure the inner EXECUTEs are not separately capped, so
# a per-iteration SET LOCAL caps nothing (converse.hypernyms ran 4.5 minutes under a 2s
# cap), and setting it at session level kills the whole CALL instead (the sweep exited
# after 31 of 130). One statement per psql invocation is the only form where the cap
# applies to the function being measured.
#
# Results land in laplace.sql_tier_sweep as each function completes.
set -uo pipefail
TIER="${1:-0}"
CAP="${2:-2s}"
PSQL=(psql -h /var/run/postgresql -U laplace_admin -d laplace -X -q -A -t)

"${PSQL[@]}" -c "
CREATE TABLE IF NOT EXISTS laplace.sql_tier_sweep(
    fq text, tier int, ms numeric, rows_out bigint, all_null boolean, err text,
    measured_at timestamptz DEFAULT now());
DELETE FROM laplace.sql_tier_sweep WHERE tier = ${TIER};" >/dev/null

# The call list: fixtures chosen by PARAMETER NAME first, then by type. Type alone is
# not enough -- a bytea named p_type_id wants a relation type id, and handing it a word
# id makes the function correctly return NULL, which a null-probe then calls a defect.
mapfile -t CALLS < <("${PSQL[@]}" -c "
WITH fns AS (
  SELECT p.oid, n.nspname||'.'||p.proname AS fq, p.proargtypes, p.proargnames, p.proretset,
         regexp_replace(regexp_replace(pg_get_functiondef(p.oid),'--[^\n]*',' ','g'),'/\*.*?\*/',' ','gs') AS def
  FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN ('consensus','converse','generation','structural','taxonomy','lexical','realize','ops','chess')
    AND p.prokind='f'),
edges AS (SELECT DISTINCT f.fq AS caller, g.fq AS callee FROM fns f JOIN fns g ON g.fq<>f.fq AND position(g.fq||'(' in f.def)>0),
depth AS (
  WITH RECURSIVE walk(root,node,d) AS (
    SELECT f.fq,f.fq,0 FROM (SELECT DISTINCT fq FROM fns) f
    UNION ALL SELECT w.root,e.callee,w.d+1 FROM walk w JOIN edges e ON e.caller=w.node WHERE w.d<20)
  SELECT root AS fq, max(d) AS tier FROM walk GROUP BY root),
fixture(match,typ,expr) AS (VALUES
  ('type','bytea','laplace.relation_type_id(''IS_A'')'),
  ('relation','bytea','laplace.relation_type_id(''IS_A'')'),
  ('lang','bytea','converse.prompt_language_top(''what is a wolf'')'),
  ('context','bytea','converse.prompt_language_top(''what is a wolf'')'),
  ('source','bytea','(SELECT source_id FROM ops.source_status() WHERE source=''WordNetDecomposer'')'),
  (NULL,'bytea','laplace.word_id(''wolf'')'),
  ('name','text','''IS_A'''),
  ('prompt','text','''what is a wolf'''),
  ('phrase','text','''what is a wolf'''),
  (NULL,'text','''wolf'''),
  (NULL,'integer','8'), (NULL,'smallint','2::smallint'), (NULL,'bigint','1500000000000'),
  (NULL,'double precision','1.0'), (NULL,'numeric','1.0'), (NULL,'boolean','false'),
  ('type','bytea[]','ARRAY[laplace.relation_type_id(''IS_A'')]'),
  (NULL,'bytea[]','ARRAY[laplace.word_id(''hot''), laplace.word_id(''dog'')]'),
  (NULL,'text[]','ARRAY[''wolf'']'), (NULL,'integer[]','ARRAY[1]'),
  (NULL,'geometry','(SELECT trajectory FROM laplace.v_word_points WHERE trajectory IS NOT NULL LIMIT 1)'),
  (NULL,'timestamp with time zone','now()'), (NULL,'jsonb','''{}''::jsonb'))
SELECT DISTINCT ON (f.fq) f.fq||'|'||f.proretset::text||'|'||
       COALESCE((SELECT string_agg(COALESCE(x.expr,'<<unfixtured>>'), ', ' ORDER BY x.ord)
        FROM (SELECT t.ord, format_type(t.typid,NULL) AS typ,
                COALESCE((SELECT fx.expr FROM fixture fx WHERE fx.typ=format_type(t.typid,NULL)
                            AND fx.match IS NOT NULL AND COALESCE(f.proargnames[t.ord],'') ILIKE '%'||fx.match||'%' LIMIT 1),
                         (SELECT fx.expr FROM fixture fx WHERE fx.typ=format_type(t.typid,NULL) AND fx.match IS NULL LIMIT 1)) AS expr
              FROM unnest(f.proargtypes) WITH ORDINALITY AS t(typid,ord)) x), '')
FROM fns f JOIN depth d ON d.fq=f.fq
WHERE d.tier = ${TIER} ORDER BY f.fq;")

echo "tier ${TIER}: ${#CALLS[@]} functions, cap ${CAP}"
for row in "${CALLS[@]}"; do
  fq="${row%%|*}"; rest="${row#*|}"; setof="${rest%%|*}"; args="${rest#*|}"
  if [[ "$args" == *"<<unfixtured>>"* ]]; then
    "${PSQL[@]}" -c "INSERT INTO laplace.sql_tier_sweep(fq,tier,err) VALUES ('$fq',${TIER},'skipped: unfixtured');" >/dev/null
    continue
  fi
  call="$fq($args)"
  if [[ "$setof" == "t" ]]; then
    probe="SELECT count(*), count(*) FILTER (WHERE q IS NOT DISTINCT FROM NULL) FROM ${call} q"
  else
    probe="SELECT count(*), count(*) FILTER (WHERE q.v IS NULL) FROM (SELECT (${call})::text AS v) q"
  fi
  # TWICE, and the SECOND time is recorded. A first call is dominated by cold IO and
  # plan construction, not by query shape: realize.resolve_name measures 90ms cold and
  # 15.3ms warm, so a cold-only sweep reports plan+cache warmup as a defect.
  out=$("${PSQL[@]}" -v ON_ERROR_STOP=0 \
        -c "SET statement_timeout='${CAP}'; SET lock_timeout='${CAP}';" \
        -c "\timing on" -c "$probe" -c "$probe" 2>&1)
  ms=$(sed -n 's/^Time: \([0-9.]*\) ms.*/\1/p' <<<"$out" | tail -1)
  vals=$(grep -E '^[0-9]+\|[0-9]+$' <<<"$out" | tail -1)
  err=$(grep -m1 '^ERROR:' <<<"$out" | cut -c1-60 | sed "s/'/''/g")
  if [[ -n "$err" ]]; then
    "${PSQL[@]}" -c "INSERT INTO laplace.sql_tier_sweep(fq,tier,ms,err) VALUES ('$fq',${TIER},${ms:-NULL},'$err');" >/dev/null
  else
    n="${vals%%|*}"; nn="${vals##*|}"
    "${PSQL[@]}" -c "INSERT INTO laplace.sql_tier_sweep(fq,tier,ms,rows_out,all_null)
       VALUES ('$fq',${TIER},${ms:-NULL},${n:-NULL}, ${n:-0} > 0 AND ${nn:-0} = ${n:-0});" >/dev/null
  fi
done

"${PSQL[@]}" -c "
SELECT fq||' | '||COALESCE(round(ms,1)::text,'-')||' ms | '||COALESCE(rows_out::text,'-')||' rows | '||
       CASE WHEN err IS NOT NULL THEN err
            WHEN all_null THEN 'ALL NULL -- fast and answers nothing'
            WHEN ms > 200 THEN 'OVER BUDGET'
            ELSE 'ok' END
FROM laplace.sql_tier_sweep WHERE tier=${TIER}
ORDER BY (err IS NOT NULL AND err NOT LIKE 'skipped%') DESC, all_null DESC, ms DESC NULLS LAST;"
