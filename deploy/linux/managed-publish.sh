#!/usr/bin/env bash
# CI transaction for the API payload and managed-service pointers. Prior releases
# and backups are retained; no running STDIO client's directory is modified.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
HELPER=/usr/local/libexec/laplace-managed-deploy
RECEIPT="$ROOT/build/.managed-publish-backup"
source "$ROOT/deploy/linux/payload-sync.sh"

preflight() {
  [[ -x "$HELPER" ]] || {
    echo "::error::managed-service bootstrap required after CI safety checks: sudo python3 deploy/linux/laplace-managed-deploy bootstrap" >&2
    return 1
  }
  cmp -s "$ROOT/deploy/linux/laplace-managed-deploy" "$HELPER" || {
    echo "::error::root-owned managed deployment policy needs its explicit bootstrap upgrade" >&2
    return 1
  }
  sudo -n "$HELPER" preflight
}

case "${1:-}" in
  preflight) preflight ;;
  begin)
    preflight
    [[ ! -f "$RECEIPT" ]] || { echo "unresolved publish receipt" >&2; exit 1; }
    mkdir -p "$ROOT/build" /opt/laplace/app-backups
    backup="$(mktemp -d /opt/laplace/app-backups/managed.XXXXXX)"
    chmod 0700 "$backup"
    mkdir -m 0700 "$backup/app" "$backup/secrets"
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
    if [[ -f "$RECEIPT" ]]; then
      backup="$(<"$RECEIPT")"
      [[ "$backup" == /opt/laplace/app-backups/managed.* && -d "$backup" && ! -L "$backup" ]] || {
        echo "invalid recovery receipt; no files changed" >&2; exit 1;
      }
      sudo -n systemctl stop laplace-api
      laplace_sync_payload "$backup/app" "$APP_DIR" \
        --exclude 'laplace-api.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'chess-lab-work/' \
        --exclude 'mcp-runtime/' --exclude 'mcp/' --exclude 'releases/'
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
