#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILT="$ROOT/build/extension/laplace_substrate/laplace_substrate--0.1.0.sql"
[[ -f "$BUILT" ]] || {
    echo "built extension SQL not found: $BUILT" >&2
    echo "build it first: cmake --build build --target laplace_substrate" >&2
    exit 1
}

DB="${LAPLACE_QUERY_DB:-laplace}"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

{
    cat <<'SHIMS'
\set QUIET on
SET check_function_bodies = off;
CREATE OR REPLACE FUNCTION pg_temp.relation_type_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT laplace.canonical_id('substrate/type/' || p_name || '/v1')
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
CREATE OR REPLACE FUNCTION pg_temp.generate_greedy(
    p_prompt bytea, p_type bytea DEFAULT NULL, p_depth int DEFAULT 8)
    RETURNS TABLE(step int, type_id bytea, entity_id bytea, eff_mu numeric)
    LANGUAGE plpgsql STABLE AS $$
DECLARE
    cur  bytea := p_prompt;
    nxt  record;
    seen bytea[] := ARRAY[p_prompt];
BEGIN
    FOR i IN 1..p_depth LOOP
        SELECT c.object_id, c.type_id AS step_type, pg_temp.eff_mu_display(c.rating, c.rd) AS mu
        INTO nxt
        FROM laplace.consensus c
        WHERE c.subject_id = cur AND c.object_id IS NOT NULL
          AND (p_type IS NULL OR c.type_id = p_type)
          AND NOT pg_temp.refuted(c.rating, c.rd)
          AND NOT (c.object_id = ANY (seen))
        ORDER BY pg_temp.eff_mu(c.rating, c.rd) DESC
        LIMIT 1;
        EXIT WHEN nxt IS NULL OR nxt.object_id IS NULL;
        step := i; type_id := nxt.step_type; entity_id := nxt.object_id; eff_mu := nxt.mu;
        RETURN NEXT;
        seen := seen || nxt.object_id;
        cur  := nxt.object_id;
    END LOOP;
END;
$$;
SHIMS

    awk '/^CREATE OR REPLACE FUNCTION word_id/,0' "$BUILT" \
      | sed -e 's/^CREATE OR REPLACE FUNCTION /CREATE OR REPLACE FUNCTION pg_temp./' \
            -e 's/^CREATE UNLOGGED TABLE converse_turns/CREATE TEMP TABLE converse_turns/' \
            -e 's/SET search_path = @extschema@, public/SET search_path = pg_temp, laplace, public/' \
      | grep -v '^COMMENT ON' | grep -v "^    '" | grep -v '^SELECT pg_extension_config_dump' \
      | sed -E 's/\b(word_id|label|prompt_words|word_language|senses|define|synonyms|translations|hypernyms|examples|resolve_last_word|prompt_state|expansion|type_label|realize_path|realize|relatedness|related_in|related|usage_overlap|reason|contrast|describe|isa_path|route_prompt|resolve_topic|respond|converse|generate_greedy|generate_tree|refuted|type_id|eff_mu|eff_mu_display)\(/pg_temp.\1(/g; s/pg_temp\.pg_temp\./pg_temp./g'

    cat <<'ASK'
CREATE OR REPLACE FUNCTION pg_temp.ask(p text)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql VOLATILE AS $$ SELECT * FROM pg_temp.converse(p) $$;
\timing on
ASK
} > "$TMP"

PSQL=(psql -h /var/run/postgresql -U laplace_admin -d "$DB")

if [[ $# -gt 0 ]]; then
    echo "SELECT * FROM pg_temp.ask(:'prompt');" >> "$TMP"
    "${PSQL[@]}" -X -q -v prompt="$*" -f "$TMP"
    exit $?
fi

{
    echo "\\echo converse surface loaded (session-local) on DB '$DB'."
    echo "\\echo ask with:  SELECT * FROM pg_temp.ask('what is a dog');"
} >> "$TMP"
PSQLRC="$TMP" "${PSQL[@]}"
