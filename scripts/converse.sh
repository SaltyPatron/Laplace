#!/usr/bin/env bash
# scripts/converse.sh — conversation with the substrate.
#
# Loads the converse read surface (20_converse.sql.in) SESSION-LOCALLY into
# pg_temp — zero schema footprint on the target DB, gone at disconnect. The
# schema-of-record path for these functions is the extension itself (db-fresh
# / db-deploy); this wrapper exists so a data-rich DB whose installed
# extension predates the module can converse TODAY.
#
# Two ways to use it:
#   scripts/converse.sh "what is a dog"     # one-shot: answer and exit
#   scripts/converse.sh                      # no args: drop into psql
# In the interactive session, ask with:
#   SELECT * FROM pg_temp.ask('what is a dog');
#
# Target DB: $LAPLACE_QUERY_DB (default: laplace — the CI substrate).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILT="$ROOT/build/extension/laplace_substrate/laplace_substrate--0.1.0.sql"
[[ -f "$BUILT" ]] || {
    echo "built extension SQL not found: $BUILT" >&2
    echo "build it first: cmake --build build --target laplace_substrate" >&2
    exit 1
}

DB="${LAPLACE_QUERY_DB:-laplace-dev}"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

{
    # Shims for helpers an older installed extension may predate.
    cat <<'SHIMS'
\set QUIET on
SET check_function_bodies = off;
CREATE OR REPLACE FUNCTION pg_temp.kind_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT laplace.canonical_id('substrate/kind/' || p_name || '/v1')
$$;
CREATE OR REPLACE FUNCTION pg_temp.eff_mu(p_rating bigint, p_rd bigint) RETURNS bigint
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_rating - 2 * p_rd
$$;
CREATE OR REPLACE FUNCTION pg_temp.eff_mu_display(p_rating bigint, p_rd bigint) RETURNS numeric
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT round(((p_rating - 2 * p_rd) / 1e9)::numeric, 3)
$$;
CREATE OR REPLACE FUNCTION pg_temp.refuted(p_rating bigint, p_rd bigint) RETURNS boolean
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_rating + 2 * p_rd < 1500000000000
$$;
-- generate_greedy in its kind-emitting, refuted-pruning shape (the installed
-- extension may still carry the old signature).
CREATE OR REPLACE FUNCTION pg_temp.generate_greedy(
    p_prompt bytea, p_kind bytea DEFAULT NULL, p_depth int DEFAULT 8)
    RETURNS TABLE(step int, kind_id bytea, entity_id bytea, eff_mu numeric)
    LANGUAGE plpgsql STABLE AS $$
DECLARE
    cur  bytea := p_prompt;
    nxt  record;
    seen bytea[] := ARRAY[p_prompt];
BEGIN
    FOR i IN 1..p_depth LOOP
        SELECT c.object_id, c.kind_id AS step_kind, pg_temp.eff_mu_display(c.rating, c.rd) AS mu
        INTO nxt
        FROM laplace.consensus c
        WHERE c.subject_id = cur AND c.object_id IS NOT NULL
          AND (p_kind IS NULL OR c.kind_id = p_kind)
          AND NOT pg_temp.refuted(c.rating, c.rd)
          AND NOT (c.object_id = ANY (seen))
        ORDER BY pg_temp.eff_mu(c.rating, c.rd) DESC
        LIMIT 1;
        EXIT WHEN nxt IS NULL OR nxt.object_id IS NULL;
        step := i; kind_id := nxt.step_kind; entity_id := nxt.object_id; eff_mu := nxt.mu;
        RETURN NEXT;
        seen := seen || nxt.object_id;
        cur  := nxt.object_id;
    END LOOP;
END;
$$;
SHIMS

    # The converse module from the BUILT extension SQL, retargeted at pg_temp.
    # pg_temp functions are only callable schema-qualified (PG searches the
    # temp schema for relations, never for functions), so every cross-
    # reference gets qualified. The converse_turns session table becomes a
    # TEMP table — the session-local loader must never create real tables.
    awk '/^CREATE OR REPLACE FUNCTION word_id/,0' "$BUILT" \
      | sed -e 's/^CREATE OR REPLACE FUNCTION /CREATE OR REPLACE FUNCTION pg_temp./' \
            -e 's/^CREATE UNLOGGED TABLE converse_turns/CREATE TEMP TABLE converse_turns/' \
            -e 's/SET search_path = @extschema@, public/SET search_path = pg_temp, laplace, public/' \
      | grep -v '^COMMENT ON' | grep -v "^    '" | grep -v '^SELECT pg_extension_config_dump' \
      | sed -E 's/\b(word_id|label|prompt_words|word_language|senses|define|synonyms|translations|hypernyms|examples|resolve_last_word|prompt_state|expansion|kind_label|realize_path|realize|related_in|related|describe|isa_path|route_prompt|resolve_topic|respond|converse|generate_greedy|generate_tree|refuted|kind_id|eff_mu|eff_mu_display)\(/pg_temp.\1(/g; s/pg_temp\.pg_temp\./pg_temp./g'

    # Convenience alias — ask() IS the loop (session = this psql connection,
    # turns in the TEMP table, anaphora works: "what about its synonyms?").
    cat <<'ASK'
CREATE OR REPLACE FUNCTION pg_temp.ask(p text)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql VOLATILE AS $$ SELECT * FROM pg_temp.converse(p) $$;
\timing on
ASK
} > "$TMP"

PSQL=(psql -h /var/run/postgresql -U laplace_admin -d "$DB")

if [[ $# -gt 0 ]]; then
    # One-shot: append the answer query to the loaded surface and run the whole
    # thing as one -f script (pg_temp persists; :'…' variable interpolation
    # works in -f files — unlike -c). The prompt is passed as a psql variable
    # and quoted with :'…' — safe for any text, apostrophes included.
    echo "SELECT * FROM pg_temp.ask(:'prompt');" >> "$TMP"
    "${PSQL[@]}" -X -q -v prompt="$*" -f "$TMP"
    exit $?
fi

# No args: load via psqlrc, print a hint, drop into the interactive prompt.
{
    echo "\\echo converse surface loaded (session-local) on DB '$DB'."
    echo "\\echo ask with:  SELECT * FROM pg_temp.ask('what is a dog');"
} >> "$TMP"
PSQLRC="$TMP" "${PSQL[@]}"
