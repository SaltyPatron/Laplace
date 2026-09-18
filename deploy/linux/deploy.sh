#!/usr/bin/env bash
# Publish API + SPA + UCI and immutable MCP/Lichess runtimes to /opt/laplace/app.
#
# Options:
#   --force-npm    always run npm ci (ignore lockfile stamp)
#   --serial       publish API, UCI, MCP, Lichess serially (default: parallel)
#   --api-only     publish API + SPA in the existing API-only transaction
#   --uci-only     publish only the verified immutable UCI runtime
#   --uci-recover  restore a retained failed UCI-only publication

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
source "$REPO_ROOT/deploy/linux/app-dir-contract.sh"
source "$REPO_ROOT/deploy/linux/payload-sync.sh"
STAGE=""
FORCE_NPM=0
SERIAL=0
API_ONLY=0
UCI_ONLY=0
UCI_RECOVER=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force-npm) FORCE_NPM=1; shift ;;
    --serial)    SERIAL=1; shift ;;
    --api-only)  API_ONLY=1; shift ;;
    --uci-only)  UCI_ONLY=1; shift ;;
    --uci-recover) UCI_ONLY=1; UCI_RECOVER=1; shift ;;
    -h|--help)
      sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done


# UCI-only publication uses this existing owner without service-policy setup,
# secret reads, API replacement, or MCP/Lichess selection.
uci_no_other_transaction() {
  local pending
  for pending in .managed-publish-backup .api-publish-backup .application-publish-owner .application-restore-pending; do
    [[ ! -e "$REPO_ROOT/build/$pending" ]] || {
      echo "::error::another application transaction is unresolved" >&2; return 1;
    }
  done
  [[ ! -e /var/lib/laplace-managed/transaction.json ]] || {
    echo "::error::managed transaction is unresolved" >&2; return 1;
  }
}

uci_revision_snapshot() {
  local state="$1" source="$REPO_ROOT/build/.laplace-source-revision"
  local receipt="$APP_DIR/.laplace-source-revision"
  [[ "${LAPLACE_UCI_REVISION_RECEIPT:-0}" == 1 ]] || return 0

  [[ -f "$source" && ! -L "$source" ]] || {
    echo "::error::UCI delivery requires an exact candidate revision receipt" >&2
    return 1
  }
  local next
  next="$(<"$source")"
  [[ "$next" =~ ^[0-9a-f]{40}$ ]] || {
    echo "::error::invalid candidate revision receipt for UCI delivery" >&2
    return 1
  }

  cp -- "$source" "$state/next-revision"
  if [[ -f "$receipt" && ! -L "$receipt" ]]; then
    cp -- "$receipt" "$state/previous-revision"
  elif [[ -e "$receipt" || -L "$receipt" ]]; then
    echo "::error::installed application revision receipt is not a regular file" >&2
    return 1
  else
    : > "$state/previous-revision-absent"
  fi
  : > "$state/revision-managed"
}

uci_revision_install() {
  local state="$1" receipt="$APP_DIR/.laplace-source-revision" temporary current=""
  [[ -f "$state/revision-managed" ]] || return 0
  [[ ! -L "$receipt" && ( ! -e "$receipt" || -f "$receipt" ) ]] || {
    echo "::error::application revision receipt changed type during UCI transaction" >&2
    return 1
  }
  [[ ! -f "$receipt" ]] || current="$(<"$receipt")"
  if [[ -f "$state/previous-revision" ]]; then
    [[ "$current" == "$(<"$state/previous-revision")" ]] || {
      echo "::error::application revision changed outside the UCI transaction" >&2
      return 1
    }
  elif [[ -f "$state/previous-revision-absent" ]]; then
    [[ -z "$current" && ! -e "$receipt" ]] || {
      echo "::error::application revision appeared outside the UCI transaction" >&2
      return 1
    }
  else
    echo "::error::UCI transaction lost its revision snapshot" >&2
    return 1
  fi

  temporary="$APP_DIR/.laplace-source-revision.tmp.$"$
  install -m 0644 "$state/next-revision" "$temporary"
  mv -f "$temporary" "$receipt"
  [[ "$(<"$receipt")" == "$(<"$state/next-revision")" ]] || {
    echo "::error::UCI revision receipt did not commit" >&2
    return 1
  }
}

uci_revision_restore() {
  local state="$1" receipt="$APP_DIR/.laplace-source-revision"
  local current="" next previous="" temporary
  [[ -f "$state/revision-managed" ]] || return 0

  next="$(<"$state/next-revision")"
  [[ ! -L "$receipt" && ( ! -e "$receipt" || -f "$receipt" ) ]] || {
    echo "::error::application revision receipt changed type during UCI recovery" >&2
    return 1
  }
  [[ ! -f "$receipt" ]] || current="$(<"$receipt")"

  if [[ -f "$state/previous-revision" ]]; then
    previous="$(<"$state/previous-revision")"
    [[ "$current" == "$previous" || "$current" == "$next" ]] || {
      echo "::error::application revision changed outside the UCI transaction" >&2
      return 1
    }
    temporary="$APP_DIR/.laplace-source-revision.restore.$"$
    install -m 0644 "$state/previous-revision" "$temporary"
    mv -f "$temporary" "$receipt"
  elif [[ -f "$state/previous-revision-absent" ]]; then
    [[ -z "$current" || "$current" == "$next" ]] || {
      echo "::error::application revision appeared outside the UCI transaction" >&2
      return 1
    }
    rm -f "$receipt"
  else
    echo "::error::UCI recovery has no prior revision disposition" >&2
    return 1
  fi
}

uci_verify() {
  python3 "$REPO_ROOT/scripts/verify-api-payload.py" --verify-uci "$1" \
    --manifest "$2" --receipt "$3"
}

uci_recover() {
  uci_no_other_transaction || return 1
  local marker="$REPO_ROOT/build/.uci-publish-pending" state old next current="" build
  [[ -f "$marker" && ! -L "$marker" ]] || {
    [[ ! -e "$marker" && ! -L "$marker" ]] && return 0
    echo "::error::invalid UCI publication marker" >&2; return 1;
  }
  state="$(<"$marker")"
  build="$(realpath -e "$REPO_ROOT/build")" || return 1
  [[ "$state" == "$build"/.uci-publish.* && -d "$state" && ! -L "$state" &&
     "$(dirname "$state")" == "$build" && -f "$state/previous-target" &&
     ! -L "$state/previous-target" ]] || {
    echo "::error::invalid UCI recovery state; no link changed" >&2; return 1;
  }
  old="$(<"$state/previous-target")"
  next=""
  [[ ! -f "$state/next-target" ]] || next="$(<"$state/next-target")"
  for current in "$old" "$next"; do
    [[ -z "$current" || "$current" =~ ^releases/runtime\.[a-zA-Z0-9_.-]+/uci/laplace-uci$ ]] || {
      echo "::error::invalid retained UCI target; no link changed" >&2; return 1;
    }
  done
  current=""
  if [[ -L "$APP_DIR/laplace-uci" ]]; then
    current="$(readlink "$APP_DIR/laplace-uci")"
  elif [[ -e "$APP_DIR/laplace-uci" ]]; then
    echo "::error::UCI selection is no longer a managed link" >&2; return 1
  fi
  [[ "$current" == "$old" || ( -n "$next" && "$current" == "$next" ) ]] || {
    echo "::error::UCI selection changed outside this transaction" >&2; return 1;
  }
  if [[ "$current" != "$old" ]]; then
    if [[ -n "$old" ]]; then
      laplace_select_uci_runtime "$APP_DIR" "$old" || return 1
    else
      rm "$APP_DIR/laplace-uci" || return 1
    fi
  fi
  uci_revision_restore "$state" || return 1
  if [[ -n "$old" ]]; then
    uci_verify "$APP_DIR/laplace-uci" "$state/previous.json" "$state/restored.json" || return 1
    cp "$state/restored.json" "$REPO_ROOT/build/.uci-publish-restored.json" || return 1
  fi
  rm "$marker" || return 1
  rm -rf -- "$state"
}

publish_uci_only() (
  set -euo pipefail
  uci_no_other_transaction
  local marker="$REPO_ROOT/build/.uci-publish-pending" state old="" reference=""
  local release="" next="" committed=0 marked=0 rc=0 previous_lease
  [[ ! -e "$marker" && ! -L "$marker" ]] || {
    echo "::error::UCI recovery is pending; run deploy.sh --uci-recover" >&2; exit 1;
  }
  if [[ -L "$APP_DIR/laplace-uci" ]]; then
    old="$(readlink "$APP_DIR/laplace-uci")"
    [[ "$old" =~ ^releases/runtime\.[a-zA-Z0-9_.-]+/uci/laplace-uci$ ]] || {
      echo "::error::existing UCI link is outside immutable runtime ownership" >&2; exit 1;
    }
    reference="$(laplace_current_runtime_dir "$APP_DIR" uci "$APP_DIR/laplace-uci")"
    [[ -f "$reference/../.runtime-lease" && ! -L "$reference/../.runtime-lease" ]] || {
      echo "::error::previous UCI runtime has no lease" >&2; exit 1;
    }
    exec {previous_lease}<"$reference/../.runtime-lease"
    flock -s "$previous_lease"
  elif [[ -e "$APP_DIR/laplace-uci" ]]; then
    echo "::error::refusing to replace a non-managed UCI executable" >&2; exit 1
  fi
  mkdir -p "$REPO_ROOT/build"
  state="$(mktemp -d "$(realpath -e "$REPO_ROOT/build")/.uci-publish.XXXXXX")"
  uci_revision_snapshot "$state"
  trap 'rc=$?; trap - EXIT
    if [[ "$marked" == 1 && "$committed" == 0 ]]; then
      uci_recover || { echo "::error::UCI recovery retained at $state" >&2; rc=1; }
    elif [[ "$marked" == 0 ]]; then
      rm -rf -- "$state"
    fi
    exit "$rc"' EXIT
  trap 'exit 143' TERM HUP
  trap 'exit 130' INT
  printf '%s\n' "$old" > "$state/previous-target"
  if [[ -n "$old" ]]; then
    python3 "$REPO_ROOT/scripts/verify-api-payload.py" --seal-uci "$reference" \
      --wrapped-uci --manifest "$state/previous.json"
  fi
  (set -o noclobber; printf '%s\n' "$state" > "$marker")
  marked=1
  mkdir "$state/stage"
  dotnet publish "$REPO_ROOT/app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj" \
    -c Release --no-self-contained --no-build -o "$state/stage"
  python3 "$REPO_ROOT/scripts/verify-api-payload.py" --seal-uci "$state/stage" \
    --native-build "${LAPLACE_ENGINE_BUILD:-$REPO_ROOT/build/engine}" --manifest "$state/next.json"
  release="$(laplace_stage_uci_runtime "$APP_DIR" "$state/stage")"
  next="releases/$(basename "$release")/uci/laplace-uci"
  printf '%s\n' "$next" > "$state/next-target"
  uci_verify "$release/uci/laplace-uci" "$state/next.json" "$state/staged.json"
  # Refuse interference before the single atomic pointer replacement.
  if [[ -n "$old" ]]; then
    [[ -L "$APP_DIR/laplace-uci" && "$(readlink "$APP_DIR/laplace-uci")" == "$old" ]]
  else
    [[ ! -e "$APP_DIR/laplace-uci" && ! -L "$APP_DIR/laplace-uci" ]]
  fi
  laplace_select_uci_runtime "$APP_DIR" "$next"
  uci_verify "$APP_DIR/laplace-uci" "$state/next.json" "$state/verified.json"
  uci_revision_install "$state"
  cp "$state/next.json" "$REPO_ROOT/build/.uci-publish-payload.json"
  cp "$state/verified.json" "$REPO_ROOT/build/.uci-publish-verified.json"
  rm "$marker"
  committed=1
  rm -rf -- "$state"
  echo "PASS: immutable UCI payload and public launcher verified; API/MCP/Lichess selections unchanged"
)

if [[ "$API_ONLY" == 1 && "$UCI_ONLY" == 1 ]]; then
  echo "::error::choose API-only or UCI-only publication" >&2
  exit 2
fi
if [[ "$UCI_ONLY" == 1 ]]; then
  if [[ "$UCI_RECOVER" == 1 ]]; then
    uci_recover
  else
    laplace_reconcile_app_dir_contract "$APP_DIR"
    publish_uci_only
  fi
  exit 0
fi
[[ ! -e "$REPO_ROOT/build/.uci-publish-pending" ]] || {
  echo "::error::UCI publication recovery is pending" >&2; exit 1;
}

if [[ "$API_ONLY" -eq 1 && "${LAPLACE_API_TRANSACTION:-}" != "1" ]]; then
  echo "::error::use scripts/publish-applications.sh api-deploy for API-only publication" >&2
  exit 2
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

laplace_reconcile_app_dir_contract "$APP_DIR"

web_source=""
web_manifest="$REPO_ROOT/build/.laplace-web-artifact.json"
require_qualified_web="${LAPLACE_REQUIRE_QUALIFIED_WEB:-0}"
reuse_installed_web="${LAPLACE_REUSE_INSTALLED_WEB:-0}"

# Automatic delivery must not mutate a qualified candidate by rebuilding its SPA.
# Use the sealed exact-revision artifact when web inputs changed. If web inputs
# did not change, carry forward the installed SPA byte-for-byte.
if [[ "$require_qualified_web" == 1 || "$FORCE_NPM" -eq 0 ]]; then
  if [[ -f "$web_manifest" ]] && python3 "$REPO_ROOT/scripts/web-artifact.py" verify \
       --root "$REPO_ROOT" --manifest "$web_manifest"; then
    web_source=qualified
    echo "==> [1/4] use exact qualified front-end artifact"
  fi
fi

if [[ "$require_qualified_web" == 1 && "$web_source" != qualified ]]; then
  echo "::error::delivery requires the exact qualified web artifact for this revision" >&2
  exit 1
fi

if [[ -z "$web_source" && "$reuse_installed_web" == 1 ]]; then
  [[ -f "$APP_DIR/wwwroot/index.html" ]] || {
    echo "::error::web is unchanged but no installed SPA exists to preserve" >&2
    exit 1
  }
  web_source=installed
  echo "==> [1/4] preserve installed front-end artifact (web inputs unchanged)"
fi

if [[ -z "$web_source" ]]; then
  echo "==> [1/4] build front-end (explicit/manual fallback)"
  pushd "$REPO_ROOT/web" >/dev/null
  stamp="node_modules/.laplace-npm-ci.stamp"
  need_ci=1
  if [[ "$FORCE_NPM" -eq 0 && -d node_modules && -f package-lock.json && -f "$stamp" ]]; then
    lock_hash=$(sha256sum package-lock.json | awk '{print $1}')
    prev=$(cat "$stamp" 2>/dev/null || true)
    if [[ "$prev" == "$lock_hash" ]]; then
      echo "    npm ci skipped (package-lock unchanged)"
      need_ci=0
    fi
  fi
  if [[ "$need_ci" -eq 1 ]]; then
    npm ci --no-audit --no-fund --prefer-offline
    mkdir -p node_modules
    sha256sum package-lock.json | awk '{print $1}' > "$stamp"
  fi
  test -f openapi/openapi.json || { echo "::error::web/openapi/openapi.json missing — run pipeline.sh build first"; exit 1; }
  echo "    generating src/api/types.gen.ts from openapi/openapi.json"
  npm run gen:api
  npm run build
  popd >/dev/null
  python3 "$REPO_ROOT/scripts/web-artifact.py" seal \
    --root "$REPO_ROOT" --manifest "$web_manifest"
  web_source=built
fi

publish_api() {
  echo "==> publish API -> staging ($STAGE)"
  dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj" \
    -c Release --no-build --no-self-contained -o "$STAGE"
}

if [[ "$API_ONLY" -eq 1 ]]; then
  publish_api
  rm -rf "$STAGE/wwwroot"
  mkdir -p "$STAGE/wwwroot"
  if [[ "$web_source" == installed ]]; then
    cp -r "$APP_DIR/wwwroot/." "$STAGE/wwwroot/"
  else
    cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"
  fi
  python3 "$REPO_ROOT/scripts/verify-api-payload.py" \
    --seal-payload "$STAGE" --native-build "$LAPLACE_ENGINE_BUILD" \
    --manifest "$LAPLACE_API_PAYLOAD_MANIFEST"
  # All build/closure checks complete before the serving API is stopped.
  sudo -n systemctl stop laplace-api
  laplace_sync_api_payload "$STAGE" "$APP_DIR"
  laplace_require_app_dir_contract "$APP_DIR"
  echo "published API + SPA; transaction owner must verify or restore"
  exit 0
fi

UCI_STAGE="$(mktemp -d)"
MCP_STAGE="$(mktemp -d)"
LICHESS_STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE" "$UCI_STAGE" "$MCP_STAGE" "$LICHESS_STAGE"' EXIT

publish_uci() {
  echo "==> publish laplace-uci -> $UCI_STAGE"
  dotnet publish "$REPO_ROOT/app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj" \
    -c Release --no-build --no-self-contained -o "$UCI_STAGE"
}

publish_mcp() {
  echo "==> publish laplace-mcp -> $MCP_STAGE"
  dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj" \
    -c Release --no-build --no-self-contained -o "$MCP_STAGE"
}

publish_lichess() {
  dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj" \
    -c Release --no-build --no-self-contained -o "$LICHESS_STAGE"
}

if [[ "$SERIAL" -eq 1 ]]; then
  echo "==> [2/4] publish API + UCI + MCP + Lichess (serial)"
  publish_api
  publish_uci
  publish_mcp
  publish_lichess
else
  echo "==> [2/4] publish API || UCI || MCP || Lichess (parallel)"
  api_log="$(mktemp)"; uci_log="$(mktemp)"; mcp_log="$(mktemp)"; lichess_log="$(mktemp)"
  set +e
  publish_api >"$api_log" 2>&1 &
  api_pid=$!
  publish_uci >"$uci_log" 2>&1 &
  uci_pid=$!
  publish_mcp >"$mcp_log" 2>&1 &
  mcp_pid=$!
  publish_lichess >"$lichess_log" 2>&1 &
  lichess_pid=$!
  wait "$api_pid"; api_rc=$?
  wait "$uci_pid"; uci_rc=$?
  wait "$mcp_pid"; mcp_rc=$?
  wait "$lichess_pid"; lichess_rc=$?
  set -e
  cat "$api_log" "$uci_log" "$mcp_log" "$lichess_log"
  rm -f "$api_log" "$uci_log" "$mcp_log" "$lichess_log"
  if [[ "$api_rc" -ne 0 || "$uci_rc" -ne 0 || "$mcp_rc" -ne 0 || "$lichess_rc" -ne 0 ]]; then
    echo "::error::publish failed (api=$api_rc uci=$uci_rc mcp=$mcp_rc lichess=$lichess_rc)"
    exit 1
  fi
fi

echo "==> [3/4] overlay SPA; prepare isolated UCI/MCP/Lichess runtimes"
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
if [[ "$web_source" == installed ]]; then
  cp -r "$APP_DIR/wwwroot/." "$STAGE/wwwroot/"
else
  cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"
fi
test -x "$UCI_STAGE/laplace-uci"
test -f "$MCP_STAGE/Laplace.Endpoints.Mcp"
test -f "$LICHESS_STAGE/Laplace.Endpoints.Lichess"
chmod 0755 "$MCP_STAGE/Laplace.Endpoints.Mcp" "$LICHESS_STAGE/Laplace.Endpoints.Lichess"
release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"
release_name="$(basename "$release")"
ln -s "releases/$release_name/uci/laplace-uci" "$STAGE/laplace-uci"
ln -s "releases/$release_name/mcp/Laplace.Endpoints.Mcp" "$STAGE/laplace-mcp"
ln -s "releases/$release_name/lichess/Laplace.Endpoints.Lichess" "$STAGE/laplace-lichess"
python3 "$REPO_ROOT/scripts/managed-policy.py" --root "$REPO_ROOT" --stage "$STAGE/managed-services"

python3 "$REPO_ROOT/scripts/check-uci-runtime.py" "$release/uci/laplace-uci"

echo "==> [4/4] sync isolated MCP runtime + app into $APP_DIR"
if [[ "${LAPLACE_MANAGED_TRANSACTION:-}" == "1" ]]; then
  sudo -n systemctl stop laplace-api
fi
laplace_sync_payload "$STAGE" "$APP_DIR" \
  --exclude 'laplace-api.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'chess-lab-work/' \
  --exclude 'mcp-runtime/' --exclude 'mcp/' --exclude 'releases/'
laplace_require_app_dir_contract "$APP_DIR"
test -x "$APP_DIR/laplace-uci" || { echo "::error::laplace-uci missing from $APP_DIR after sync"; exit 1; }
test -x "$APP_DIR/laplace-mcp" || { echo "::error::laplace-mcp missing from $APP_DIR after sync"; exit 1; }
test -f "$release/mcp/Laplace.Endpoints.Mcp.dll"
test -x "$APP_DIR/laplace-lichess"

mcp_target="$(readlink -f "$APP_DIR/laplace-mcp" 2>/dev/null || true)"
expected_mcp="$(readlink -f "$release/mcp/Laplace.Endpoints.Mcp" 2>/dev/null || true)"
test -n "$mcp_target" && test -x "$mcp_target" || {
  echo "::error::deployed laplace-mcp does not resolve to an executable target"; exit 1;
}
[[ -n "$expected_mcp" && "$mcp_target" == "$expected_mcp" ]] || {
  echo "::error::deployed laplace-mcp target is stale: got '$mcp_target', expected '$expected_mcp'"; exit 1;
}
python3 "$REPO_ROOT/scripts/probe-mcp-stdio.py" "$mcp_target"
echo "✓ published API + SPA + UCI + versioned MCP/Lichess to $APP_DIR"
