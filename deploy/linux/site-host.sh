#!/usr/bin/env bash
# The monorepo site's root part on a machine Laplace-Operations declares (hart-server), declared once and converged:
# run it again and it changes nothing. It needs root for /opt, systemd and nginx; everything after it (build, install,
# migrate, publish, restart) is deploy/linux/site.sh, run by the repository's runner as laplace-runner with no sudo.
#
#   sudo bash deploy/linux/site-host.sh
#   sudo bash deploy/linux/site-host.sh --routing-only
#
# What it makes:
#   /opt/laplace/{app,app/logs,app/mcp-runtime,lib,share,tmp}   laplace-runner:laplace-runner 2775 (the shared group)
#   /opt/laplace/secrets, secrets/data-protection               laplace-runner:laplace-runner 2770
#   laplace-api.service                                         the API on 127.0.0.1:5187 as laplace-runner
#   laplace-api-restart.path + .service                         restarts laplace-api when /run/lock/laplace/restart-api changes
#   laplace-mcp.service                                         the MCP endpoint on 127.0.0.1:$LAPLACE_MCP_HTTP_PORT as laplace-runner
#   laplace-mcp-restart.path + .service                         restarts laplace-mcp when /run/lock/laplace/restart-mcp changes
#   /opt/laplace/secrets/mcp.env                                LAPLACE_MCP_TOKEN, made once (64 hex), laplace-runner 0640; never replaced
#   /etc/nginx/sites-available/laplace, enabled                 :8080 -> 127.0.0.1:5187, and /mcp -> the MCP port
#   ufw 8080/tcp from the LAN                                   when ufw is active
# What it does not: no sudo rule for any runner (Laplace-Operations' setup.sh rights removes those), no runner
# registration (Laplace-Operations' agents.sh registers every repository's runner), no database (site.sh makes it).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
host_args=("$@")
as_is=false; routing_only=false; browser_setup=false; identity_enrollment=''
chess_setup=false; chess_configuration=''; chess_enrollment=''
runtime_only=false; runtime_manifest=''; check=false
while (( $# )); do
  case "$1" in
    --as-is) as_is=true ;;
    --routing-only) routing_only=true ;;
    --browser-setup) browser_setup=true ;;
    --chess-setup) chess_setup=true ;;
    --runtime-only) runtime_only=true ;;
    --check) check=true ;;
    --runtime-manifest)
      (( $# >= 2 )) || { echo '--runtime-manifest requires a path' >&2; exit 2; }
      runtime_manifest=$2; shift ;;
    --chess-configuration|--chess-enrollment)
      (( $# >= 2 )) || { echo "$1 requires a path" >&2; exit 2; }
      if [[ "$1" == --chess-configuration ]]; then chess_configuration=$2; else chess_enrollment=$2; fi
      shift ;;
    --identity-enrollment)
      (( $# >= 2 )) || { echo '--identity-enrollment requires a path' >&2; exit 2; }
      identity_enrollment=$2; shift ;;
    *) echo "Unknown setup argument: $1" >&2; exit 2 ;;
  esac
  shift
done
if ! "$check"; then
  [[ "$(id -u)" == 0 ]] || { echo "run with sudo: sudo bash $0"; exit 1; }
fi
# The declaration is public; credentials alongside it retain their own modes.
machine_config_permissions() {
  local directory=${1:-/etc/laplace}
  install -d -m 755 "$directory"
  if [[ -f "$directory/machine.env" ]]; then
    chmod 644 "$directory/machine.env"
  fi
}
# the machine's own declaration (Laplace-Operations: /etc/laplace/machine.env), as site.sh reads it: what this host
# names differently from the defaults below is said there once, never on a command line
machine_values=$(python3 "$HERE/../../scripts/machine-env.py")
eval "$machine_values"
unset machine_values
PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
RUN_USER="${LAPLACE_AGENT_USER:-laplace-runner}"
RUN_GROUP="${LAPLACE_GROUP:-laplace-runner}"
SITE_PORT="${LAPLACE_SITE_PORT:-8080}"
API_UPSTREAM="${LAPLACE_API_URL:-http://127.0.0.1:5187}"
[[ "$SITE_PORT" =~ ^[0-9]{1,5}$ ]] && (( 10#$SITE_PORT > 0 && 10#$SITE_PORT <= 65535 )) || { echo 'Invalid LAPLACE_SITE_PORT' >&2; exit 1; }
[[ "$API_UPSTREAM" =~ ^https?://[A-Za-z0-9.:-]+$ ]] || { echo 'Invalid LAPLACE_API_URL origin' >&2; exit 1; }
LAN="${LAPLACE_LAN:-192.168.1.0/24}"
LOCKS="${LAPLACE_LOCKS:-/run/lock/laplace}"
MCP_PORT="${LAPLACE_MCP_HTTP_PORT:-5190}"   # the same default as site.sh; a machine that declares another says so in /etc/laplace/machine.env
MCP_UPSTREAM="${LAPLACE_MCP_UPSTREAM:-http://127.0.0.1:$MCP_PORT}"
[[ "$MCP_UPSTREAM" =~ ^https?://[A-Za-z0-9.:-]+$ ]] || { echo 'Invalid LAPLACE_MCP_UPSTREAM origin' >&2; exit 1; }

# Never an old script. This runs from a person's checkout, which nothing else keeps current: before anything is
# changed, the checkout is brought to its upstream by the account that owns it (never as root), and what arrived is
# what runs. A checkout that cannot be brought there (changed files, commits of its own, no upstream, no network) is
# refused and said, with what to do; --as-is runs what is here, for a host that is deliberately held back.
current() {
  local repo owner branch before after
  repo="$(git -C "$HERE" rev-parse --show-toplevel 2>/dev/null)" || { echo "  $HERE is not in a git checkout: nothing says what revision this is. --as-is runs it anyway."; return 1; }
  owner="$(stat -c %U "$repo")"
  g() { sudo -u "$owner" git -C "$repo" "$@"; }
  branch="$(g symbolic-ref -q --short HEAD)" || { echo "  $repo is on no branch (a detached revision): check out main there, or --as-is."; return 1; }
  g rev-parse -q --verify "@{upstream}" >/dev/null 2>&1 || { echo "  $repo: $branch follows no upstream: check out main there, or --as-is."; return 1; }
  [[ -z "$(g status --porcelain --untracked-files=no)" ]] || { echo "  $repo has changed files:"; g status --short --untracked-files=no | head -10; echo "  commit or drop them, or --as-is."; return 1; }
  before="$(g rev-parse HEAD)"
  g fetch -q || { echo "  $repo: its upstream could not be fetched: whether this script is current is not known. --as-is runs it anyway."; return 1; }
  g merge -q --ff-only "@{upstream}" || { echo "  $repo: $branch has commits its upstream does not: it cannot be brought forward. --as-is runs it anyway."; return 1; }
  after="$(g rev-parse HEAD)"
  if [[ "$before" != "$after" ]]; then
    echo "=== $repo brought from ${before:0:12} to ${after:0:12} ($branch): running what arrived"
    exec env LAPLACE_SITE_HOST_CURRENT="$after" bash "$HERE/$(basename "${BASH_SOURCE[0]}")" "${host_args[@]}"
  fi
  echo "=== $repo is at its upstream: ${after:0:12} ($branch)"
}
if "$check"; then :
elif "$as_is"; then echo "=== run as it is here, not checked against its upstream (--as-is): $(git -C "$HERE" rev-parse --short=12 HEAD 2>/dev/null || echo 'no revision')"
elif [[ -n "${LAPLACE_SITE_HOST_CURRENT:-}" ]]; then echo "=== at ${LAPLACE_SITE_HOST_CURRENT:0:12}"
else current || exit 1; fi
private_browser_route() {
  python3 "$HERE/configure-nginx.py" --configuration "${LAPLACE_PRIVATE_ORIGINS:-$HERE/../private-origins.json}" --apply
}
browser_identity() {
  local directory=${LAPLACE_IDENTITY_DIRECTORY:-/etc/laplace-mono/identity}
  local args=(install --directory "$directory")
  if [[ -n "$identity_enrollment" ]]; then args+=(--enrollment "$identity_enrollment")
  elif [[ ! -f "$directory/enrollment.json" ]]; then
    if "$browser_setup"; then echo 'Browser setup requires --identity-enrollment on first installation' >&2; return 1; fi
    return 0
  fi
  python3 "$HERE/identity-runtime.py" "${args[@]}"
}
chess_runtime() {
  local declaration=${chess_configuration:-${LAPLACE_CHESS_CONFIGURATION:-/etc/laplace-mono/chess/agent/installation.json}}
  if [[ ! -f "$declaration" ]]; then
    if "$chess_setup"; then echo "Missing chess installation declaration: $declaration" >&2; return 1; fi
    return 0
  fi
  local args=(install --configuration "$declaration")
  if [[ -n "$chess_enrollment" ]]; then args+=(--enrollment "$chess_enrollment"); fi
  python3 "$HERE/chess-host.py" "${args[@]}"
}
application_runtime() {
  local args=()
  if [[ -n "$runtime_manifest" ]]; then args+=(--manifest "$runtime_manifest"); fi
  local selected
  selected=$(python3 "$HERE/runtime/install.py" --select "${args[@]}")
  if [[ -z "$selected" ]]; then
    if "$runtime_only"; then echo 'No monorepo runtime installation declared for this host' >&2; return 1; fi
    return 0
  fi
  if "$check"; then args=(--check); else args=(); fi
  python3 "$HERE/runtime/install.py" --manifest "$selected" "${args[@]}"
}
runtime_storage() {
  local args=() selected
  if [[ -n "$runtime_manifest" ]]; then args+=(--manifest "$runtime_manifest"); fi
  selected=$(python3 "$HERE/runtime/install.py" --select "${args[@]}")
  if [[ -n "$selected" ]]; then
    python3 "$HERE/runtime/install.py" --manifest "$selected" --storage-check
  fi
}
runtime_storage
if ! "$check"; then machine_config_permissions; fi
if "$check"; then
  python3 "$HERE/service-units.py" --verify
  application_runtime
  exit
fi
if "$runtime_only"; then application_runtime; exit; fi
if "$chess_setup"; then chess_runtime; exit; fi
if "$browser_setup"; then private_browser_route; browser_identity; chess_runtime; exit; fi
if "$routing_only"; then private_browser_route; exit; fi
id "$RUN_USER" >/dev/null 2>&1 || { echo "no user $RUN_USER: Laplace-Operations' sudo ./setup.sh packages makes it"; exit 1; }
# Resolve every service path and identity before changing the installation.
python3 "$HERE/service-units.py" --verify >/dev/null
say() { printf '  %-44s %s\n' "$1" "$2"; }

echo "=== directories"
install -d -o "$RUN_USER" -g "$RUN_GROUP" -m 2775 "$PREFIX" "$PREFIX/app" "$PREFIX/app/logs" "$PREFIX/app/mcp-runtime" \
  "$PREFIX/lib" "$PREFIX/share" "$PREFIX/tmp"
install -d -o "$RUN_USER" -g "$RUN_GROUP" -m 2770 "$PREFIX/secrets" "$PREFIX/secrets/data-protection"
for d in "$PREFIX" "$PREFIX/app" "$PREFIX/app/logs" "$PREFIX/app/mcp-runtime" "$PREFIX/lib" "$PREFIX/share" "$PREFIX/tmp"; do
  chgrp "$RUN_GROUP" "$d"; chmod 2775 "$d"; done
say "$PREFIX" "$RUN_USER:$RUN_GROUP 2775; secrets 2770"
# the restart trigger's directory is Operations' (setup.sh shared: tmpfiles at boot); made here too if it is not yet
[[ -d "$LOCKS" ]] || { install -d -g "$RUN_GROUP" -m 2775 "$LOCKS"; say "$LOCKS" "made (Operations setup.sh declares it at boot)"; }
if ! [[ /proc/self/fd/8 -ef "$LOCKS/host-resource.lock" ]]; then exec 8>>"$LOCKS/host-resource.lock"; fi
flock -xn 8 || { echo 'Another deployment or ingest owns the host lock' >&2; exit 1; }

echo "=== units"
# Retain the service configuration and activation state until the entire setup
# succeeds. A later runtime or routing failure must not leave new broken units.
unit_snapshot=$(mktemp -d)
unit_names=(laplace-api.service laplace-api-restart.path laplace-api-restart.service
            laplace-mcp.service laplace-mcp-restart.path laplace-mcp-restart.service)
for unit in "${unit_names[@]}"; do
  [[ ! -e "/etc/systemd/system/$unit" ]] || cp -p "/etc/systemd/system/$unit" "$unit_snapshot/$unit"
  if systemctl is-active --quiet "$unit"; then touch "$unit_snapshot/$unit.active"; fi
  if systemctl is-enabled --quiet "$unit" 2>/dev/null; then touch "$unit_snapshot/$unit.enabled"; fi
done
finish_units() {
  local result=$? unit
  trap - EXIT
  if (( result != 0 )); then
    echo 'Setup failed; restoring previous service units and activation state.' >&2
    set +e
    for unit in "${unit_names[@]}"; do
      if [[ -f "$unit_snapshot/$unit" ]]; then cp -p "$unit_snapshot/$unit" "/etc/systemd/system/$unit"
      else rm -f "/etc/systemd/system/$unit"; fi
    done
    systemctl daemon-reload
    for unit in laplace-api.service laplace-mcp.service laplace-api-restart.path laplace-mcp-restart.path; do
      if [[ -f "$unit_snapshot/$unit.enabled" ]]; then systemctl enable --quiet "$unit"
      else systemctl disable --quiet "$unit"; fi
      if [[ -f "$unit_snapshot/$unit.active" ]]; then systemctl restart "$unit"
      else systemctl stop "$unit"; fi
    done
  fi
  rm -rf -- "$unit_snapshot"
  exit "$result"
}
trap finish_units EXIT
python3 "$HERE/service-units.py" --verify --output /etc/systemd/system
systemctl daemon-reload
systemctl enable -q laplace-api.service
systemctl enable -q --now laplace-api-restart.path
[[ -f "$PREFIX/app/Laplace.Endpoints.OpenAICompat.dll" ]] && systemctl restart laplace-api.service
say "laplace-api.service" "$(systemctl is-enabled laplace-api.service), $(systemctl is-active laplace-api.service || true) (starts once site.sh has published)"
say "laplace-api-restart.path" "$(systemctl is-active laplace-api-restart.path): $LOCKS/restart-api"

echo "=== the MCP endpoint"
# The MCP server takes only a database route the host itself authenticates: the cluster's Unix-domain socket
# (ManagedServiceDatabase). A host whose database is another machine's (a hosting host: LAPLACE_PGHOST in
# /etc/laplace/machine.env names it) cannot run it, and is told so here in place of a unit that would never start.
DB_HOST="${LAPLACE_PGHOST:-/tmp}"
if [[ "$DB_HOST" != /* ]]; then
  say "laplace-mcp.service" "not declared: this host's database is $DB_HOST, not a local socket (the MCP endpoint runs where the database is)"
else
# Its token: every caller presents it, the operator keeps a copy in the secrets store. Made once; a host that has one
# keeps it (a new token would lock out every registered client).
if [[ ! -s "$PREFIX/secrets/mcp.env" ]]; then
  command -v openssl >/dev/null || { echo "openssl is not installed: apt-get install openssl"; exit 1; }
  ( umask 0027; printf 'LAPLACE_MCP_TOKEN=%s\n' "$(openssl rand -hex 32)" > "$PREFIX/secrets/mcp.env" )
  say "$PREFIX/secrets/mcp.env" "made: LAPLACE_MCP_TOKEN (64 hex)"
else say "$PREFIX/secrets/mcp.env" "kept"; fi
chown "$RUN_USER:$RUN_GROUP" "$PREFIX/secrets/mcp.env"; chmod 0640 "$PREFIX/secrets/mcp.env"
systemctl daemon-reload
systemctl enable -q laplace-mcp.service
systemctl enable -q --now laplace-mcp-restart.path
[[ -f "$PREFIX/app/mcp-runtime/Laplace.Endpoints.Mcp.dll" && -f "$PREFIX/app/laplace-mcp.env" ]] && systemctl restart laplace-mcp.service
say "laplace-mcp.service" "$(systemctl is-enabled laplace-mcp.service), $(systemctl is-active laplace-mcp.service || true) (starts once site.sh has published)"
say "laplace-mcp-restart.path" "$(systemctl is-active laplace-mcp-restart.path): $LOCKS/restart-mcp"
fi

echo "=== nginx"
command -v nginx >/dev/null || { echo "nginx is not installed: apt-get install nginx"; exit 1; }
sed -e "s|http://127.0.0.1:5190;|$MCP_UPSTREAM;|" \
    -e "s|http://127.0.0.1:5187;|$API_UPSTREAM;|" \
    -e "s|listen 8080;|listen $SITE_PORT;|" \
    -e "s|listen \[::\]:8080;|listen [::]:$SITE_PORT;|" \
    "$HERE/nginx-laplace.conf" > /etc/nginx/sites-available/laplace; chmod 0644 /etc/nginx/sites-available/laplace
ln -sfn /etc/nginx/sites-available/laplace /etc/nginx/sites-enabled/laplace
rm -f /etc/nginx/sites-enabled/laplace-managed
nginx -t -q
if systemctl is-active -q nginx; then systemctl reload nginx; else systemctl start nginx; fi
say "nginx site laplace" ":$SITE_PORT -> $API_UPSTREAM; /mcp -> $MCP_UPSTREAM"
private_browser_route

echo "=== firewall"
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow from "$LAN" to any port "$SITE_PORT" proto tcp comment "laplace site" >/dev/null
  say "ufw" "$SITE_PORT/tcp from $LAN"
else say "ufw" "not active: nothing to do"; fi

# Identity refresh owns the same deployment lock. Release the setup lock before
# running its activation; the root-owned enrollment survives source updates.
flock -u 8
application_runtime
browser_identity
chess_runtime

echo
echo "done. The runner deploys with deploy/linux/site.sh on every merge to main (.github/workflows/laplace.yml)."
