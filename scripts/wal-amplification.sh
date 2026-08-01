#!/usr/bin/env bash
# Measure WAL write amplification across an ingest.
#
# The whole "is the seed slow because of the write path" argument has been run on
# inference for months. It is settleable with two numbers nobody ever collected:
# how many BYTES of WAL a seed generates, and what fraction of them are full-page
# images rather than record payload.
#
# A full-page image is the entire 8 KiB page written because it was the first touch
# after a checkpoint. When checkpoints are forced by WAL volume rather than by the
# timer, every one of them re-arms FPI for every page touched afterwards -- so heavy
# FPI share is the signature of the checkpoint spiral, and a low FPI share means the
# volume is genuine row traffic and the spiral theory is wrong.
#
#   wal-amplification.sh before            # snapshot LSN + checkpoint counters
#   ...run the ingest...
#   wal-amplification.sh after             # delta, plus the FPI/record split
#
# State lives in a temp file so the two calls can be minutes or hours apart.

set -euo pipefail

PSQL_ARGS=(-U "${PGUSER:-laplace_admin}" -d "${PGDATABASE:-laplace}" -Atc)
[[ -n "${PGHOST:-}" ]] && PSQL_ARGS=(-h "$PGHOST" "${PSQL_ARGS[@]}")
STATE="${WAL_AMP_STATE:-/tmp/laplace-wal-amplification.state}"

q() { psql "${PSQL_ARGS[@]}" "$1"; }

snapshot() {
    q "SELECT pg_current_wal_lsn()::text || '|' || num_timed || '|' || num_requested
         FROM pg_stat_checkpointer;"
}

case "${1:-}" in
before)
    snapshot > "$STATE"
    echo "wal-amplification: snapshot -> $STATE"
    cat "$STATE"
    ;;

after)
    [[ -f "$STATE" ]] || { echo "no snapshot at $STATE — run 'before' first" >&2; exit 2; }
    IFS='|' read -r lsn0 timed0 req0 < "$STATE"
    IFS='|' read -r lsn1 timed1 req1 < <(snapshot)

    bytes=$(q "SELECT pg_wal_lsn_diff('$lsn1'::pg_lsn, '$lsn0'::pg_lsn)::bigint;")
    echo "WAL bytes generated : $bytes  ($(awk -v b="$bytes" 'BEGIN{printf "%.2f GiB", b/1073741824}'))"
    echo "checkpoints timed   : $((timed1 - timed0))"
    echo "checkpoints forced  : $((req1 - req0))   <- volume-driven; high means max_wal_size is small for the rate"

    # pg_waldump gives the authoritative FPI/record split. It reads WAL SEGMENTS, which
    # are recycled once a checkpoint passes them, so this only works when the segments
    # spanning the run still exist -- report that honestly instead of printing a number
    # derived from whatever happens to be left on disk.
    waldir=$(q "SELECT setting FROM pg_settings WHERE name='data_directory';")/pg_wal
    dump=$(command -v pg_waldump || echo "${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}/bin/pg_waldump")
    if [[ -x "$dump" ]]; then
        start_seg=$(q "SELECT pg_walfile_name('$lsn0'::pg_lsn);")
        if [[ -f "$waldir/$start_seg" ]]; then
            echo
            echo "--- pg_waldump --stats (FPI vs record bytes) ---"
            "$dump" --stats -p "$waldir" -s "$lsn0" -e "$lsn1" 2>/dev/null \
                | grep -iE "FPI|Total|Record size|record" | head -20 \
                || echo "(pg_waldump produced no summary for that range)"
        else
            echo
            echo "segment $start_seg for the start LSN has been recycled — the FPI split"
            echo "cannot be recovered after the fact. Re-run with a larger max_wal_size or"
            echo "snapshot closer to the ingest to keep the range on disk."
        fi
    else
        echo "pg_waldump not found (looked at: $dump) — byte totals above still hold."
    fi
    ;;

*)
    echo "usage: $0 before|after" >&2
    exit 2
    ;;
esac
