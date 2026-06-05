#!/usr/bin/env bash
# scripts/converse.sh — conversation with the substrate.
#
# Loads the converse read surface (15_converse.sql.in) SESSION-LOCALLY into
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

DB="${LAPLACE_QUERY_DB:-laplace}"
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
SHIMS

    # The converse module from the BUILT extension SQL, retargeted at pg_temp.
    # pg_temp functions are only callable schema-qualified (PG searches the
    # temp schema for relations, never for functions), so every cross-
    # reference gets qualified.
    awk '/^CREATE OR REPLACE FUNCTION word_id/,0' "$BUILT" \
      | sed -e 's/^CREATE OR REPLACE FUNCTION /CREATE OR REPLACE FUNCTION pg_temp./' \
            -e 's/SET search_path = @extschema@, public/SET search_path = pg_temp, laplace, public/' \
      | grep -v '^COMMENT ON FUNCTION' | grep -v "^    '" \
      | sed -E 's/\b(word_id|label|prompt_words|word_language|senses|define|synonyms|translations|hypernyms|examples|resolve_last_word|kind_id|eff_mu|eff_mu_display)\(/pg_temp.\1(/g; s/pg_temp\.pg_temp\./pg_temp./g'

    # Convenience alias. (Loader stays QUIET — only answers print.)
    cat <<'ASK'
CREATE OR REPLACE FUNCTION pg_temp.ask(p text)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE AS $$ SELECT * FROM pg_temp.respond(p) $$;
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
