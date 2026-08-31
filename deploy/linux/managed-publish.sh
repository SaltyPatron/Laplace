#!/usr/bin/env bash
# CI transaction for the API payload and managed-service pointers. Prior releases
# and backups are retained; no running STDIO client's directory is modified.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
HELPER=/usr/local/libexec/laplace-managed-deploy
RECEIPT="$ROOT/build/.managed-publish-backup"
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
    mkdir -p "$ROOT/build" /opt/laplace/app-backups
    backup="$(mktemp -d /opt/laplace/app-backups/managed.XXXXXX)"
    chmod 0700 "$backup"
    mkdir -m 0700 "$backup/app" "$backup/secrets"
    python3 "$ROOT/scripts/install-stockfish.py" --prefix "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}" --snapshot "$backup/stockfish.json"
    # Snapshot before replacing any app file. Preserve runtime config, logs,
    # user work, and all prior immutable runtime directories IN PLACE.
    rsync -a --exclude 'laplace-api.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'chess-lab-work/' \
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
    ;;
  rollback)
    # Rollback is activation recovery only. The workflow invokes it when publish
    # failed or was cancelled before the transaction committed. Post-delivery QA,
    # smoke, and evaluation failures remain visible as red evidence but do not own
    # this payload transaction and cannot replace the code under investigation with
    # an older release.
    if [[ -f "$RECEIPT" ]]; then
      backup="$(<"$RECEIPT")"
      [[ "$backup" == /opt/laplace/app-backups/managed.* && -d "$backup" && ! -L "$backup" ]] || {
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
      echo "restored previous API payload and managed units; backup retained at $backup"
    elif [[ -x "$HELPER" ]]; then
      sudo -n "$HELPER" rollback
    fi
    ;;
  *) echo "usage: managed-publish.sh preflight|begin|reconcile|activate|verify|commit|rollback" >&2; exit 2 ;;
esac
