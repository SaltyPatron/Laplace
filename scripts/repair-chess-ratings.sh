#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
PSQL="$LAPLACE_PG_PREFIX/bin/psql"
PGDATABASE="${PGDATABASE:-laplace}"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
BATCH_SIZE="${LAPLACE_CHESS_RATING_REPAIR_BATCH:-256}"

[[ -x "$PSQL" ]] || { echo "::error::missing Laplace psql at $PSQL" >&2; exit 127; }
[[ "$BATCH_SIZE" =~ ^[1-9][0-9]*$ ]] || { echo "::error::invalid repair batch size: $BATCH_SIZE" >&2; exit 2; }

export PGHOST PGUSER PGDATABASE

# One worker per installed substrate. The database journal is the durable truth;
# flock only prevents two local workers from wasting time on the same next batch.
exec 9>"/tmp/laplace-chess-rating-repair.lock"
flock 9

psqlq() {
  "$PSQL" -X -w -d "$PGDATABASE" -U "$PGUSER" -v ON_ERROR_STOP=1 "$@"
}

repair_id="$(psqlq -tAX -c "SELECT chess.rating_repair_generation()")"
[[ -n "$repair_id" ]] || { echo "::error::empty chess rating repair generation" >&2; exit 1; }

# A manually dispatched worker may arrive before deployment has invoked the cheap
# enqueue procedure. Make the same generation visible without inventing another id.
psqlq -v repair_id="$repair_id" -c \
  "INSERT INTO chess.rating_repair_journal(repair_id, status)
   VALUES (:'repair_id', 'pending')
   ON CONFLICT (repair_id) DO NOTHING" >/dev/null

status="$(psqlq -v repair_id="$repair_id" -tAX -c \
  "SELECT status FROM chess.rating_repair_journal WHERE repair_id = :'repair_id'")"
if [[ "$status" == "ok" ]]; then
  echo "chess-rating-repair: generation=$repair_id already complete"
  exit 0
fi

psqlq -v repair_id="$repair_id" -c \
  "UPDATE chess.rating_repair_journal
      SET status='running',
          attempts=attempts+1,
          started_at=COALESCE(started_at, now()),
          heartbeat_at=now(),
          ended_at=NULL,
          error=NULL
    WHERE repair_id=:'repair_id'" >/dev/null

echo "chess-rating-repair: generation=$repair_id batch_size=$BATCH_SIZE status=running"

while :; do
  cursor="$(psqlq -v repair_id="$repair_id" -tAX -c \
    "SELECT COALESCE(encode(last_subject, 'hex'), '')
       FROM chess.rating_repair_journal
      WHERE repair_id = :'repair_id'")"

  if [[ -n "$cursor" ]]; then
    cursor_pred="AND e.subject_id > decode('$cursor', 'hex')"
  else
    cursor_pred=""
  fi

  mapfile -t subjects < <(psqlq -tAX -c \
    "SELECT encode(s.subject_id, 'hex')
       FROM (
         SELECT DISTINCT e.subject_id
           FROM laplace.attestations e
          WHERE (
                  e.type_id = laplace.relation_type_id('PLAYED_BY')
                  OR (
                    e.type_id = laplace.relation_type_id('OUTCOME')
                    AND EXISTS (
                      SELECT 1
                        FROM laplace.attestations pairing
                       WHERE pairing.subject_id = e.subject_id
                         AND pairing.context_id IS NOT DISTINCT FROM e.context_id
                         AND pairing.type_id = laplace.relation_type_id('PLAYED_BY')
                    )
                  )
                )
                $cursor_pred
          ORDER BY e.subject_id
          LIMIT $BATCH_SIZE
       ) s
      ORDER BY s.subject_id")

  if (( ${#subjects[@]} == 0 )); then
    psqlq -v repair_id="$repair_id" -c \
      "UPDATE chess.rating_repair_journal
          SET status='ok', heartbeat_at=now(), ended_at=now(), error=NULL
        WHERE repair_id=:'repair_id'" >/dev/null
    done_count="$(psqlq -v repair_id="$repair_id" -tAX -c \
      "SELECT subjects_done FROM chess.rating_repair_journal WHERE repair_id=:'repair_id'")"
    echo "chess-rating-repair: generation=$repair_id complete subjects=$done_count"
    exit 0
  fi

  parts=()
  for subject in "${subjects[@]}"; do
    [[ "$subject" =~ ^[0-9a-fA-F]{32}$ ]] || {
      echo "::error::non-canonical subject id returned by repair scope: $subject" >&2
      exit 1
    }
    parts+=("decode('$subject','hex')")
  done
  subject_array="$(IFS=,; echo "${parts[*]}")"
  first="${subjects[0]}"
  last="${subjects[${#subjects[@]}-1]}"

  echo "chess-rating-repair: batch subjects=${#subjects[@]} first=$first last=$last"
  if ! psqlq -c \
    "CALL chess.repair_player_ratings_batch(
       laplace.relation_type_id('OUTCOME'),
       laplace.relation_type_id('PLAYED_BY'),
       laplace.relation_type_id('HAS_RATING'),
       ARRAY[$subject_array]::bytea[])"; then
    psqlq -v repair_id="$repair_id" -v err="batch failed at $first..$last" -c \
      "UPDATE chess.rating_repair_journal
          SET status='failed', heartbeat_at=now(), ended_at=now(), error=:'err'
        WHERE repair_id=:'repair_id'" >/dev/null || true
    exit 1
  fi

  # Cursor advancement is deliberately a separate transaction after the batch.
  # If cancellation lands between these commits, rerun repeats that batch from
  # durable evidence and converges to the same consensus before advancing.
  psqlq -v repair_id="$repair_id" -v last="$last" -v n="${#subjects[@]}" -c \
    "UPDATE chess.rating_repair_journal
        SET last_subject=decode(:'last','hex'),
            subjects_done=subjects_done + :'n'::bigint,
            heartbeat_at=now(),
            status='running'
      WHERE repair_id=:'repair_id'" >/dev/null

done
