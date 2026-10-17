#!/usr/bin/env bash
# ops.analyze_substrate never reports a partial run as complete (Laplace#1879).
# ANALYZE by a role without MAINTAIN on a table or a leaf partition skips it with only a
# WARNING. The installed procedure (extension/.../ops/repair.sql.in) is loaded into an
# isolated database and called as an unprivileged role: it must raise, naming every
# relation it may not analyze, analyze nothing then, and succeed once MAINTAIN is granted.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
name="laplace_analyze_${GITHUB_RUN_ID:-$$}_${RANDOM}"
role="${name}_seed"
P(){ psql -X -v ON_ERROR_STOP=1 -Atq -d "$1" "${@:2}"; }
printf 'CREATE DATABASE "%s";\n' "$name" | P postgres
cleanup(){
  printf 'DROP DATABASE "%s" WITH (FORCE);\nDROP ROLE IF EXISTS "%s";\n' "$name" "$role" | P postgres
}
trap cleanup EXIT
printf 'CREATE ROLE "%s" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;\n' "$role" | P postgres
P "$name" <<'SQL'
CREATE SCHEMA laplace; CREATE SCHEMA realize; CREATE SCHEMA consensus; CREATE SCHEMA generation; CREATE SCHEMA ops;
CREATE TABLE laplace.attestations(id integer) PARTITION BY HASH (id);
CREATE TABLE laplace.attestations_h0 PARTITION OF laplace.attestations FOR VALUES WITH (MODULUS 2, REMAINDER 0);
CREATE TABLE laplace.attestations_h1 PARTITION OF laplace.attestations FOR VALUES WITH (MODULUS 2, REMAINDER 1);
CREATE TABLE realize.labels(id integer);
INSERT INTO laplace.attestations SELECT generate_series(1, 120);
INSERT INTO realize.labels SELECT generate_series(1, 40);
SQL
sed 's/@extschema@/laplace/g' "$root/extension/laplace_substrate/sql/functions/ops/repair.sql.in" | P "$name"
P "$name" -v role="$role" <<'SQL'
GRANT USAGE ON SCHEMA laplace, realize, consensus, generation, ops TO :"role";
GRANT EXECUTE ON PROCEDURE ops.analyze_substrate(text[]) TO :"role";
-- MAINTAIN on the parent and the plain table, not on the leaves: ANALYZE would skip the leaves.
GRANT MAINTAIN ON laplace.attestations, realize.labels TO :"role";
SQL

expect_refusal(){ # $1 = CALL, $2 = the relation list the error must name
  local out
  if out=$(P "$name" -v role="$role" 2>&1 <<SQL
SET ROLE :"role";
$1
SQL
  ); then
    echo "FAIL $1 succeeded as $role without MAINTAIN on every leaf: $out" >&2; exit 1
  fi
  grep -q "ERROR:  analyze_substrate: role $role may not analyze" <<<"$out" \
    || { echo "FAIL $1 did not raise the refusal: $out" >&2; exit 1; }
  grep -qF "$2" <<<"$out" || { echo "FAIL $1 did not name $2: $out" >&2; exit 1; }
  if grep -q 'WARNING' <<<"$out"; then echo "FAIL $1 analyzed (WARNING) before refusing: $out" >&2; exit 1; fi
}
expect_refusal 'CALL ops.analyze_substrate();' '2 relation(s); nothing was analyzed: laplace.attestations_h0, laplace.attestations_h1'
expect_refusal "CALL ops.analyze_substrate(ARRAY['attestations']);" 'laplace.attestations_h0, laplace.attestations_h1'
P "$name" <<'SQL'
SELECT count(*) = 0 AS nothing_analyzed FROM pg_class
WHERE relname IN ('attestations_h0', 'attestations_h1', 'labels') AND reltuples >= 0 \gset
\if :nothing_analyzed
\else
\echo 'FAIL a refused run analyzed some table'
\quit 1
\endif
SQL

# A table named that the substrate schemas do not have is not a success either.
if out=$(P "$name" -c "CALL ops.analyze_substrate(ARRAY['attestations', 'no_such_table']);" 2>&1); then
  echo "FAIL a missing table was reported as analyzed: $out" >&2; exit 1
fi
grep -q '2 table(s) named, 1 found' <<<"$out" || { echo "FAIL missing-table error: $out" >&2; exit 1; }

# With MAINTAIN on every leaf, the same call as the same role analyzes everything, with no warning.
printf 'GRANT MAINTAIN ON laplace.attestations_h0, laplace.attestations_h1 TO "%s";\n' "$role" | P "$name"
out=$(P "$name" -v role="$role" 2>&1 <<'SQL'
SET ROLE :"role";
CALL ops.analyze_substrate();
RESET ROLE;
SELECT (SELECT sum(reltuples) FROM pg_class WHERE relname IN ('attestations_h0', 'attestations_h1')) = 120
   AND (SELECT reltuples FROM pg_class WHERE relname = 'labels') = 40 AS analyzed \gset
\if :analyzed
\else
\echo 'FAIL analyze_substrate left reltuples unset'
\quit 1
\endif
SQL
) || { echo "FAIL $out" >&2; exit 1; }
if grep -q 'WARNING' <<<"$out"; then echo "FAIL analyze warned: $out" >&2; exit 1; fi
# A table whose ANALYZE errors (an index expression that raises while fixture.boom is on)
# fails the run, named, after every other table was still analyzed.
P "$name" <<'SQL'
CREATE FUNCTION realize.boom(integer) RETURNS integer IMMUTABLE LANGUAGE plpgsql AS $$
BEGIN
    IF current_setting('fixture.boom', true) = 'on' THEN RAISE EXCEPTION 'boom %', $1; END IF;
    RETURN $1;
END$$;
CREATE TABLE realize.broken(id integer);
CREATE INDEX broken_boom ON realize.broken (realize.boom(id));
INSERT INTO realize.broken VALUES (1), (2);
UPDATE pg_class SET reltuples = -1 WHERE relname IN ('attestations_h0', 'attestations_h1', 'labels');
SQL
if out=$(PGOPTIONS='-c fixture.boom=on' P "$name" -c 'CALL ops.analyze_substrate();' 2>&1); then
  echo "FAIL a failed ANALYZE was reported as success: $out" >&2; exit 1
fi
grep -q 'analyze_substrate: 1 of 5 table(s) failed ANALYZE: realize.broken (boom' <<<"$out" \
  || { echo "FAIL failed-table error: $out" >&2; exit 1; }
P "$name" <<'SQL'
SELECT (SELECT sum(reltuples) FROM pg_class WHERE relname IN ('attestations_h0', 'attestations_h1')) = 120
   AND (SELECT reltuples FROM pg_class WHERE relname = 'labels') = 40 AS others_analyzed \gset
\if :others_analyzed
\else
\echo 'FAIL the tables after a failed one were not analyzed'
\quit 1
\endif
SQL
echo 'PASS analyze_substrate refuses, naming every relation the caller may not analyze (leaves included), analyzes nothing then, refreshes reltuples once the role may MAINTAIN them, and raises naming any table whose ANALYZE failed'
