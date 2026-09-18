#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

managed() { bash "$ROOT/deploy/linux/managed-publish.sh" "$@"; }

application_revision_expected() {
  local expected built
  expected="$(git -C "$ROOT" rev-parse HEAD)"
  built="$(cat "$ROOT/build/.laplace-source-revision" 2>/dev/null || true)"
  [[ "$built" == "$expected" ]] || {
    echo "::error::application publication build does not belong to this checkout (expected $expected, found ${built:-missing})" >&2
    return 1
  }
  printf '%s\n' "$expected"
}

application_revision_install() {
  local expected app_dir receipt temporary
  expected="$(application_revision_expected)" || return $?
  app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  receipt="$app_dir/.laplace-source-revision"
  temporary="$app_dir/.laplace-source-revision.tmp.$$"
  install -m 0644 "$ROOT/build/.laplace-source-revision" "$temporary"
  mv -f "$temporary" "$receipt"
  bash "$ROOT/scripts/check-deployed-revision.sh" "$expected"
}

application_revision_verify() {
  local expected
  expected="$(application_revision_expected)" || return $?
  bash "$ROOT/scripts/check-deployed-revision.sh" "$expected"
}


application_web_backup_root() {
  printf '%s\n' "${LAPLACE_APP_BACKUP_ROOT:-/opt/laplace/app-backups}"
}

application_web_restore_revision() {
  local state="$1" app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  local receipt="$app_dir/.laplace-source-revision" temporary
  if [[ -f "$state/previous-revision" && ! -L "$state/previous-revision" ]]; then
    temporary="$app_dir/.laplace-source-revision.restore.$"
    install -m 0644 "$state/previous-revision" "$temporary"
    mv -f "$temporary" "$receipt"
  elif [[ -f "$state/previous-revision-absent" ]]; then
    rm -f "$receipt"
  else
    echo "::error::web recovery lost prior application revision disposition" >&2
    return 1
  fi
}

application_web_recover() {
  local marker="$ROOT/build/.web-publish-pending"
  [[ -e "$marker" || -L "$marker" ]] || return 0
  [[ -f "$marker" && ! -L "$marker" ]] || {
    echo "::error::invalid web publication recovery marker" >&2
    return 1
  }

  local app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}" backup_root state swap
  local stage_inode live_inode swap_inode previous_digest current_digest
  backup_root="$(application_web_backup_root)"
  state="$(<"$marker")"
  [[ -d "$backup_root" && ! -L "$backup_root" ]] || {
    echo "::error::web publication backup root is unavailable" >&2
    return 1
  }
  case "$state" in
    "$backup_root"/web.*) ;;
    *) echo "::error::invalid web publication recovery state: $state" >&2; return 1 ;;
  esac
  [[ -d "$state" && ! -L "$state" && -f "$state/stage-inode" ]] || {
    echo "::error::web publication recovery state is incomplete" >&2
    return 1
  }
  swap="$state/swap-wwwroot"
  [[ -d "$swap" && ! -L "$swap" && -d "$app_dir/wwwroot" && ! -L "$app_dir/wwwroot" ]] || {
    echo "::error::web publication directories are not recoverable real directories" >&2
    return 1
  }

  stage_inode="$(<"$state/stage-inode")"
  live_inode="$(stat -c '%i' "$app_dir/wwwroot")"
  swap_inode="$(stat -c '%i' "$swap")"
  if [[ "$live_inode" == "$stage_inode" ]]; then
    python3 "$ROOT/scripts/atomic-directory-exchange.py" "$app_dir/wwwroot" "$swap"
  elif [[ "$swap_inode" != "$stage_inode" ]]; then
    echo "::error::web publication ownership is ambiguous; refusing recovery" >&2
    return 1
  fi

  application_web_restore_revision "$state"

  previous_digest="$(<"$state/previous-web-digest")"
  current_digest="$(python3 "$ROOT/scripts/web-artifact.py" digest --directory "$app_dir/wwwroot")"
  [[ "$current_digest" == "$previous_digest" ]] || {
    echo "::error::restored web artifact differs from the pre-publication snapshot" >&2
    return 1
  }

  rm "$marker"
  rm -rf -- "$state"
  echo "web publication recovery complete"
}

application_web_main() (
  set -euo pipefail
  local mode="$1" marker="$ROOT/build/.web-publish-pending"
  if [[ "$mode" == web-recover ]]; then
    application_web_recover
    exit 0
  fi
  [[ "$mode" == web-deploy ]] || return 2

  for pending in .managed-publish-backup .application-publish-owner .application-restore-pending .api-publish-backup .uci-publish-pending .web-publish-pending; do
    [[ ! -e "$ROOT/build/$pending" && ! -L "$ROOT/build/$pending" ]] || {
      echo "::error::prior application transaction unresolved; no web changes made" >&2
      exit 1
    }
  done
  [[ ! -e /var/lib/laplace-managed/transaction.json ]] || {
    echo "::error::managed service transaction unresolved; no web changes made" >&2
    exit 1
  }

  local app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}" backup_root state swap
  local manifest="$ROOT/build/.laplace-web-artifact.json" attempted=0 rc=0
  application_revision_expected >/dev/null
  python3 "$ROOT/scripts/web-artifact.py" verify --root "$ROOT" --manifest "$manifest"

  # shellcheck source=deploy/linux/app-dir-contract.sh
  source "$ROOT/deploy/linux/app-dir-contract.sh"
  laplace_reconcile_app_dir_contract "$app_dir"
  [[ -d "$app_dir/wwwroot" && ! -L "$app_dir/wwwroot" ]] || {
    echo "::error::installed wwwroot is not a real directory" >&2
    exit 1
  }

  backup_root="$(application_web_backup_root)"
  [[ -d "$backup_root" && ! -L "$backup_root" ]] || {
    echo "::error::bootstrap-owned application backup root is missing" >&2
    exit 1
  }
  state="$(mktemp -d "$backup_root/web.XXXXXX")"
  chmod 0700 "$state"
  swap="$state/swap-wwwroot"
  mkdir -m 0755 "$swap"

  trap 'rc=$?; trap - EXIT; trap "" INT TERM HUP
    if [[ "$attempted" == 1 ]]; then
      application_web_recover || rc=1
    elif [[ -n "${state:-}" && -d "$state" ]]; then
      rm -rf -- "$state"
    fi
    exit "$rc"' EXIT
  trap 'exit 143' TERM HUP
  trap 'exit 130' INT

  python3 "$ROOT/scripts/web-artifact.py" digest --directory "$app_dir/wwwroot" > "$state/previous-web-digest"
  if [[ -f "$app_dir/.laplace-source-revision" && ! -L "$app_dir/.laplace-source-revision" ]]; then
    cp -- "$app_dir/.laplace-source-revision" "$state/previous-revision"
  elif [[ ! -e "$app_dir/.laplace-source-revision" && ! -L "$app_dir/.laplace-source-revision" ]]; then
    : > "$state/previous-revision-absent"
  else
    echo "::error::installed application revision receipt is not a regular file" >&2
    exit 1
  fi

  rsync -a --delete --no-perms --no-owner --no-group --executability     "$ROOT/web/dist/" "$swap/"
  install -m 0644 "$ROOT/build/.laplace-source-revision" "$swap/.laplace-web-source-revision"
  python3 "$ROOT/scripts/web-artifact.py" verify-installed     --root "$ROOT" --manifest "$manifest" --directory "$swap"
  stat -c '%i' "$swap" > "$state/stage-inode"

  mkdir -p "$ROOT/build"
  (set -o noclobber; printf '%s\n' "$state" > "$marker")
  attempted=1

  python3 "$ROOT/scripts/atomic-directory-exchange.py" "$app_dir/wwwroot" "$swap"
  application_revision_install
  python3 "$ROOT/scripts/web-artifact.py" verify-installed     --root "$ROOT" --manifest "$manifest" --directory "$app_dir/wwwroot"
  application_revision_verify

  rm "$marker"
  attempted=0
  rm -rf -- "$state"
  trap - EXIT INT TERM HUP
  echo "PASS: exact qualified SPA published atomically; API/UCI/MCP/Lichess bytes unchanged"
)

recover() {
  local keep_api_stopped="${1:-0}"
  if [[ -e "$ROOT/build/.web-publish-pending" ]]; then
    application_web_recover
    return $?
  fi
  if [[ -f "$ROOT/build/.api-publish-backup" ]]; then
    application_api_recover "${GITHUB_RUN_ID:-local-$$}"
    return $?
  fi
  if [[ -e "$ROOT/build/.uci-publish-pending" ]]; then
    bash "$ROOT/deploy/linux/deploy.sh" --uci-recover
    return $?
  fi
  local rc=0
  if [[ "$keep_api_stopped" == 1 ]]; then
    # A native/schema cutover cannot run the restored older API. Stop any
    # partially activated new API before the existing payload rollback owner.
    sudo -n systemctl stop laplace-api || return 1
  fi
  managed rollback || rc=$?
  if [[ "$keep_api_stopped" != 1 ]]; then
    sudo -n systemctl start laplace-api || rc=1
  fi
  return "$rc"
}

main() {
  local mode="${1:-}" keep_api_stopped=0
  if [[ "$#" -gt 1 ]]; then
    if [[ "$#" == 2 && "$mode" == deploy && "$2" == --keep-api-stopped-on-failure ]]; then
      keep_api_stopped=1
    else
      echo "unexpected application publication arguments" >&2
      return 2
    fi
  fi
  case "$mode" in
    web-deploy|web-recover)
      application_web_main "$mode"
      ;;
    api-deploy|api-recover)
      application_api_main "$mode"
      ;;
    uci-deploy|uci-recover)
      application_uci_main "$mode"
      ;;
    check)
      managed preflight
      echo "application publish preflight OK"
      ;;
    recover)
      recover
      ;;
    deploy)
      [[ ! -e "$ROOT/build/.api-publish-backup" && ! -e "$ROOT/build/.application-publish-owner" && ! -e "$ROOT/build/.uci-publish-pending" && ! -e "$ROOT/build/.web-publish-pending" ]] || {
        echo "::error::application publication recovery is unresolved; no full deployment changes made" >&2
        return 1
      }
      application_revision_expected >/dev/null
      managed preflight
      managed begin
      trap 'rc=$?; trap - EXIT; trap "" INT TERM HUP; recover '"$keep_api_stopped"' || rc=1; exit "$rc"' EXIT
      trap 'exit 143' TERM HUP
      trap 'exit 130' INT
      bash "$ROOT/scripts/pipeline.sh" publish
      application_revision_install
      managed reconcile
      managed activate
      sudo -n systemctl restart laplace-api

      # Publication owns application bytes and process activation. Corpus volume
      # and seeded product readiness belong to the distinct seed lifecycle.
      local live_body=""
      for _ in $(seq 1 60); do
        if live_body="$(curl -fsS http://127.0.0.1:5187/health 2>/dev/null)" && \
           grep -q '"status":"ok"' <<<"$live_body"; then
          application_revision_verify
          managed commit
          trap - EXIT INT TERM HUP
          echo "application publish committed"
          return 0
        fi
        sleep 1
      done
      echo "::error::laplace-api process did not become live after publish" >&2
      [[ -z "$live_body" ]] || echo "::error::last laplace-api liveness document: $live_body" >&2
      return 1
      ;;
    *)
      echo "usage: publish-applications.sh check|deploy [--keep-api-stopped-on-failure]|recover|web-deploy|web-recover|api-deploy|api-recover|uci-deploy|uci-recover" >&2
      return 2
      ;;
  esac
}

application_guard() { python3 "$ROOT/scripts/check-application-runtime.py" "$@"; }

# API-only publication reuses the same application transaction owner, payload
# sync law, and fixed systemd controls. MCP/Lichess policy and payloads are not
# involved. The invoking deployment owner holds the host-wide lease.
application_uci_main() {
  local mode="$1"
  case "$mode" in
    uci-recover)
      bash "$ROOT/deploy/linux/deploy.sh" --uci-recover
      ;;
    uci-deploy)
      application_revision_expected >/dev/null
      LAPLACE_UCI_REVISION_RECEIPT=1 \
        LAPLACE_ENGINE_BUILD="$ROOT/build/engine" \
        bash "$ROOT/deploy/linux/deploy.sh" --uci-only
      application_revision_verify
      ;;
    *)
      echo "::error::unknown UCI publication mode: $mode" >&2
      return 2
      ;;
  esac
}

application_api_snapshot() (
  local destination="$1"
  source "$ROOT/deploy/linux/managed-publish.sh"
  snapshot_application_payload "$APP_DIR" "$destination" "${LAPLACE_API_PAYLOAD_EXCLUDES[@]}"
)
application_api_sync() (
  source "$ROOT/deploy/linux/payload-sync.sh"
  laplace_sync_api_payload "$1" "${LAPLACE_APP_DIR:-/opt/laplace/app}"
)
application_api_manifest() {
  python3 "$ROOT/scripts/verify-api-payload.py" --seal-payload "$1" --manifest "$2"
}
application_api_verify() {
  python3 "$ROOT/scripts/verify-api-payload.py" \
    --app-dir "${LAPLACE_APP_DIR:-/opt/laplace/app}" --manifest "$1" --receipt "$2"
}
application_api_publish() {
  LAPLACE_API_TRANSACTION=1 LAPLACE_API_PAYLOAD_MANIFEST="$1" \
    LAPLACE_ENGINE_BUILD="$ROOT/build/engine" \
    bash "$ROOT/deploy/linux/deploy.sh" --api-only
}
application_api_active() {
  local state rc=0
  state="$(systemctl show laplace-api.service --property=LoadState --value)"
  [[ "$state" == loaded ]] || { echo "::error::installed API unit is missing" >&2; return 1; }
  systemctl is-active --quiet laplace-api || rc=$?
  case "$rc" in
    0) printf '1\n' ;;
    3) printf '0\n' ;;
    *) echo "::error::cannot observe API activation state" >&2; return 1 ;;
  esac
}
application_api_control() { sudo -n systemctl "$1" laplace-api; }

application_api_recover() {
  [[ ! -e "$ROOT/build/.managed-publish-backup" && ! -e "$ROOT/build/.uci-publish-pending" && ! -e "$ROOT/build/.web-publish-pending" && ! -e /var/lib/laplace-managed/transaction.json ]] || {
    echo "::error::API and managed transaction state is ambiguous; no recovery changes made" >&2
    return 1
  }
  local owner="$1" marker="$ROOT/build/.application-publish-owner"
  local receipt="$ROOT/build/.api-publish-backup" backup active
  [[ -f "$marker" ]] || return 0
  [[ "$(<"$marker")" == "$owner" ]] || {
    echo "::error::API recovery belongs to another run; no files changed" >&2; return 1;
  }
  if [[ -f "$receipt" ]]; then
    backup="$(<"$receipt")"
    [[ "$backup" == /opt/laplace/app-backups/api.* && -d "$backup" && ! -L "$backup" \
       && -f "$backup/previous.json" && -f "$backup/was-active" ]] || {
      echo "::error::invalid API recovery receipt; no files changed" >&2; return 1;
    }
    active="$(<"$backup/was-active")"
    [[ "$active" == 0 || "$active" == 1 ]] || {
      echo "::error::invalid prior API state; no files changed" >&2; return 1;
    }
    application_api_control stop || return 1
    application_api_sync "$backup/app" || return 1
    if [[ "$active" == 1 ]]; then
      application_api_control start || return 1
      application_api_verify "$backup/previous.json" "$backup/restore.json" || return 1
      cp "$backup/restore.json" "$ROOT/build/.api-publish-restored.json" || return 1
    fi
    # Only this owned completed backup is removed. Failed restore retains the
    # marker, manifest, and payload so the same owner can retry recovery.
    rm "$receipt" || return 1
    rm -rf -- "$backup" || return 1
  fi
  rm "$marker"
}

application_api_main() (
  set -euo pipefail
  local mode="$1" owner="${GITHUB_RUN_ID:-local-$$}" attempted=0 rc=0 backup="" active
  if [[ "$mode" == api-recover ]]; then
    application_api_recover "$owner"
    exit 0
  fi
  [[ "$mode" == api-deploy ]] || return 2
  for pending in .managed-publish-backup .application-publish-owner .application-restore-pending .api-publish-backup .uci-publish-pending .web-publish-pending; do
    [[ ! -e "$ROOT/build/$pending" ]] || {
      echo "::error::prior application transaction unresolved; no changes made" >&2; exit 1;
    }
  done
  [[ ! -e /var/lib/laplace-managed/transaction.json ]] || {
    echo "::error::managed service transaction unresolved; no application changes made" >&2; exit 1;
  }
  application_revision_expected >/dev/null
  active="$(application_api_active)"
  [[ -d /opt/laplace/app-backups && ! -L /opt/laplace/app-backups ]] || {
    echo "::error::bootstrap-owned application backup root is missing" >&2; exit 1;
  }
  backup="$(mktemp -d /opt/laplace/app-backups/api.XXXXXX)"
  chmod 0700 "$backup"
  trap 'rc=$?; trap - EXIT
    if [[ "$attempted" == 1 ]]; then
      application_api_recover "$owner" || rc=1
    elif [[ -n "$backup" && ! -e "$ROOT/build/.api-publish-backup" ]]; then
      rm -rf -- "$backup"
    fi
    exit "$rc"' EXIT
  trap 'exit 143' TERM HUP
  trap 'exit 130' INT
  application_guard --snapshot "$backup/runtime-before.json"
  mkdir -m 0700 "$backup/app"
  application_api_snapshot "$backup/app"
  application_api_manifest "$backup/app" "$backup/previous.json"
  printf '%s\n' "$active" > "$backup/was-active"
  mkdir -p "$ROOT/build"
  (set -o noclobber; printf '%s' "$owner" > "$ROOT/build/.application-publish-owner")
  attempted=1
  (set -o noclobber; printf '%s\n' "$backup" > "$ROOT/build/.api-publish-backup")
  application_api_publish "$backup/next.json"
  application_revision_install
  application_api_control start
  application_api_verify "$backup/next.json" "$backup/verified.json"
  application_revision_verify
  application_guard --compare "$backup/runtime-before.json"
  cp "$backup/next.json" "$ROOT/build/.api-publish-payload.json"
  cp "$backup/verified.json" "$ROOT/build/.api-publish-verified.json"
  cp "$backup/runtime-before.json" "$ROOT/build/.api-publish-native.json"
  # This commits only the verified API scope. The full managed-service
  # transaction owner remains unchanged.
  rm "$ROOT/build/.api-publish-backup"
  rm "$ROOT/build/.application-publish-owner"
  attempted=0
  rm -rf -- "$backup"
  backup=""
  echo "PASS: API + SPA payload verified and committed; native/database unchanged"
)

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
