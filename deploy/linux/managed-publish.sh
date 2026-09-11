#!/usr/bin/env bash
# CI transaction for the API payload and managed-service pointers. Releases used
# by active pointers or running clients are retained; unreachable payloads are
# reclaimed before staging so repeated delivery cannot fill the application LV.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
HELPER=/usr/local/libexec/laplace-managed-deploy
RECEIPT="$ROOT/build/.managed-publish-backup"
BACKUP_ROOT=/opt/laplace/app-backups
source "$ROOT/deploy/linux/payload-sync.sh"

installed_policy() {
  [[ -x "$HELPER" ]] || {
    echo "::error::managed host policy missing; provision through sudo bash scripts/setup-host.sh managed-services after CI safety checks" >&2
    return 1
  }
  local name
  for name in laplace-managed-deploy laplace-service-control; do
    cmp -s "$ROOT/deploy/linux/$name" "/usr/local/libexec/$name" || {
      echo "::error::managed root policy version differs; update through sudo bash scripts/setup-host.sh managed-services" >&2
      return 1
    }
  done
}

preflight() {
  installed_policy
  sudo -n "$HELPER" host-status

  local output rc=0 ready=0
  output="$(sudo -n "$HELPER" preflight 2>&1)" || rc=$?
  if [[ "$rc" -eq 0 ]]; then
    [[ -z "$output" ]] || printf '%s\n' "$output"
    return 0
  fi

  # Initial managed-service cutover has one special state that the root helper
  # deliberately refuses: an in-process Lichess bot owned by the legacy API.
  # That bot's event stream is intentionally long-lived, so "let it finish" can
  # never become true on its own. The deploy would later restart this same API
  # during install anyway. Resolve ONLY that exact condition by using the API's
  # normal systemd shutdown/restart path, which runs the legacy bot's bounded
  # drain/dispose logic; standalone CLI bots and every other preflight failure
  # remain hard stops owned by the root policy.
  if grep -Fq 'legacy API Lichess bot is active; let it finish before managed deployment' <<<"$output"; then
    echo "::notice::legacy in-process Lichess bot active — performing graceful API-owned handoff before managed cutover"
    sudo -n systemctl restart laplace-api
    for _ in $(seq 1 30); do
      if curl -fsS http://127.0.0.1:5187/health >/dev/null 2>&1; then
        ready=1
        break
      fi
      sleep 1
    done
    if [[ "$ready" -ne 1 ]]; then
      echo "::error::laplace-api did not return to liveness after legacy Lichess handoff" >&2
      return 1
    fi
    # Prove the root-owned policy is now satisfied; do not convert any second
    # failure into a bypass.
    sudo -n "$HELPER" preflight
    return
  fi

  printf '%s\n' "$output" >&2
  return "$rc"
}

ensure_host() {
  installed_policy
  # Same installed/root-owned policy used by setup-host and boot/timer
  # maintenance. No runner-supplied script or arbitrary unit executes as root.
  sudo -n "$HELPER" reconcile-host
  preflight
}

case "${1:-}" in
  preflight) ensure_host ;;
  begin)
    ensure_host
    [[ ! -f "$RECEIPT" ]] || { echo "unresolved publish receipt" >&2; exit 1; }
    # Reclaim transaction backups and only mechanically-dead immutable releases
    # before allocating the next rollback snapshot. Release liveness/legacy
    # completeness has one owner in payload-sync.sh; deploy must not carry a
    # second, drifting garbage-collection implementation.
    laplace_prune_managed_backups "$BACKUP_ROOT"
    laplace_prune_unreferenced_releases "$APP_DIR"
    mkdir -p "$ROOT/build" "$BACKUP_ROOT"
    backup="$(mktemp -d "$BACKUP_ROOT/managed.XXXXXX")"
    chmod 0700 "$backup"
    mkdir -m 0700 "$backup/app" "$backup/secrets"
    python3 "$ROOT/scripts/install-stockfish.py" --prefix "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}" --snapshot "$backup/stockfish.json"
    # Snapshot before replacing any app file. Preserve runtime config, logs,
    # user work, and all prior immutable runtime directories IN PLACE.
    #
    # The rollback snapshot lives on the same /opt/laplace filesystem as APP_DIR.
    # Do not allocate a second physical copy of every unchanged runtime/native file:
    # --link-dest links the snapshot to the current inode. The deploy path below uses
    # laplace_sync_payload's ordinary rsync replacement semantics (never --inplace),
    # so a changed/deleted live file gets a new inode while the snapshot keeps the old
    # bytes. This makes rollback space proportional to changed payload instead of the
    # complete installed application and prevents ENOSPC while creating the backup.
    rsync -a --link-dest="$APP_DIR" \
      --exclude 'laplace-api.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'chess-lab-work/' \
      --exclude 'mcp-runtime/' --exclude 'mcp/' --exclude 'releases/' "$APP_DIR/" "$backup/app/"
    for name in mcp operator lichess stripe; do
      if [[ -f "/opt/laplace/secrets/$name.env" ]]; then
        cp -p "/opt/laplace/secrets/$name.env" "$backup/secrets/$name.env"
      fi
    done
    printf '%s\n' "$backup" > "$RECEIPT"
    sudo -n "$HELPER" begin
    ;;
  reconcile) preflight; sudo -n "$HELPER" reconcile ;;
  activate) preflight; sudo -n "$HELPER" activate ;;
  verify) preflight; sudo -n "$HELPER" verify ;;
  commit)
    sudo -n "$HELPER" commit
    if [[ -f "$RECEIPT" ]]; then
      mv "$RECEIPT" "$ROOT/build/.managed-publish-committed"
    fi
    # Commit is the point at which rollback ownership ends. Keep the receipt,
    # not another payload clone.
    laplace_prune_managed_backups "$BACKUP_ROOT"
    ;;
  rollback)
    # The workflow's semantic/election eval runs AFTER publish, readiness, the
    # substrate floor, live smoke, and endpoint contract tests. It exercises the
    # installed extension + standing substrate as well as the API. Restoring an
    # older API payload cannot revert either of those layers, so an eval-only
    # failure used to throw away a smoke-verified application while leaving the
    # state that actually failed the eval untouched.
    if [[ "${PUBLISH_RESULT:-}" == "success" \
       && "${SMOKE_RESULT:-}" == "success" \
       && "${EVAL_RESULT:-}" == "failure" ]]; then
      echo "::warning::semantic eval failed after publish+smoke passed; retaining the verified API payload (API rollback cannot revert extension/substrate semantics)"
      sudo -n "$HELPER" commit
      if [[ -f "$RECEIPT" ]]; then
        mv "$RECEIPT" "$ROOT/build/.managed-publish-committed"
      fi
      laplace_prune_managed_backups "$BACKUP_ROOT"
      exit 0
    fi

    if [[ -f "$RECEIPT" ]]; then
      backup="$(<"$RECEIPT")"
      [[ "$backup" == "$BACKUP_ROOT"/managed.* && -d "$backup" && ! -L "$backup" ]] || {
        echo "invalid recovery receipt; no files changed" >&2; exit 1;
      }
      sudo -n systemctl stop laplace-api
      laplace_sync_payload "$backup/app" "$APP_DIR" \
        --exclude 'laplace-api.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'chess-lab-work/' \
        --exclude 'mcp-runtime/' --exclude 'mcp/' --exclude 'releases/'
      if [[ -f "$backup/stockfish.json" ]]; then
        python3 "$ROOT/scripts/install-stockfish.py" --prefix "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}" --restore "$backup/stockfish.json"
      fi
      for name in mcp operator lichess stripe; do
        if [[ -f "$backup/secrets/$name.env" ]]; then
          cp -p "$backup/secrets/$name.env" "/opt/laplace/secrets/$name.env.restore"
          mv "/opt/laplace/secrets/$name.env.restore" "/opt/laplace/secrets/$name.env"
        fi
      done
      sudo -n "$HELPER" rollback
      mv "$RECEIPT" "$ROOT/build/.managed-publish-rolled-back"
      echo "restored previous API payload and managed units"
      laplace_prune_managed_backups "$BACKUP_ROOT"
    elif [[ -x "$HELPER" ]]; then
      sudo -n "$HELPER" rollback
    fi
    ;;
  *) echo "usage: managed-publish.sh preflight|begin|reconcile|activate|verify|commit|rollback" >&2; exit 2 ;;
esac
