#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

managed() { bash "$ROOT/deploy/linux/managed-publish.sh" "$@"; }

recover() {
  if [[ -f "$ROOT/build/.api-publish-backup" ]]; then
    application_api_recover "${GITHUB_RUN_ID:-local-$$}"
    return $?
  fi
  local rc=0
  managed rollback || rc=$?
  sudo -n systemctl start laplace-api || rc=1
  return "$rc"
}

main() {
  local mode="${1:-}"
  case "$mode" in
    api-deploy|api-recover)
      application_api_main "$mode"
      ;;
    check)
      managed preflight
      echo "application publish preflight OK"
      ;;
    recover)
      recover
      ;;
    deploy)
      [[ ! -e "$ROOT/build/.api-publish-backup" && ! -e "$ROOT/build/.application-publish-owner" ]] || {
        echo "::error::API publication recovery is unresolved; no full deployment changes made" >&2
        return 1
      }
      managed preflight
      trap 'rc=$?; trap - EXIT; recover || rc=1; exit "$rc"' EXIT INT TERM HUP
      bash "$ROOT/scripts/pipeline.sh" publish
      sudo -n systemctl restart laplace-api
      managed activate
      for _ in $(seq 1 60); do
        if curl -fsS http://127.0.0.1:5187/health/ready | grep -q '"ready":true'; then
          managed commit
          trap - EXIT INT TERM HUP
          echo "application publish committed"
          return 0
        fi
        sleep 1
      done
      echo "::error::laplace-api did not become ready after publish" >&2
      return 1
      ;;
    *)
      echo "usage: publish-applications.sh check|deploy|recover|api-deploy|api-recover" >&2
      return 2
      ;;
  esac
}

application_guard() { python3 "$ROOT/scripts/check-application-runtime.py" "$@"; }

# API-only publication reuses the same application transaction owner, payload
# sync law, and fixed systemd controls. MCP/Lichess policy and payloads are not
# involved. The invoking deployment owner holds the host-wide lease.
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
  [[ ! -e "$ROOT/build/.managed-publish-backup" && ! -e /var/lib/laplace-managed/transaction.json ]] || {
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
  for pending in .managed-publish-backup .application-publish-owner .application-restore-pending .api-publish-backup; do
    [[ ! -e "$ROOT/build/$pending" ]] || {
      echo "::error::prior application transaction unresolved; no changes made" >&2; exit 1;
    }
  done
  [[ ! -e /var/lib/laplace-managed/transaction.json ]] || {
    echo "::error::managed service transaction unresolved; no application changes made" >&2; exit 1;
  }
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
  application_api_control start
  application_api_verify "$backup/next.json" "$backup/verified.json"
  application_guard --compare "$backup/runtime-before.json"
  cp "$backup/next.json" "$ROOT/build/.api-publish-payload.json"
  cp "$backup/verified.json" "$ROOT/build/.api-publish-verified.json"
  cp "$backup/runtime-before.json" "$ROOT/build/.api-publish-native.json"
  # This commits only the verified API scope. The normal full application
  # fingerprint and managed-service transaction stamps remain untouched.
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
