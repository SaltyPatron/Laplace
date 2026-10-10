#!/usr/bin/env bash
# The monorepo site's root part on a machine Laplace-Operations declares (hart-server), declared once and converged:
# run it again and it changes nothing. It needs root for /opt, systemd and nginx; everything after it (build, install,
# migrate, publish, restart) is deploy/linux/site.sh, run by the repository's runner as laplace-runner with no sudo.
#
#   sudo bash deploy/linux/site-host.sh
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
PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
RUN_USER="${LAPLACE_AGENT_USER:-laplace-runner}"
RUN_GROUP="${LAPLACE_GROUP:-laplace-runner}"
SITE_PORT="${LAPLACE_SITE_PORT:-8080}"
LAN="${LAPLACE_LAN:-192.168.1.0/24}"
LOCKS="${LAPLACE_LOCKS:-/run/lock/laplace}"
MCP_PORT="${LAPLACE_MCP_HTTP_PORT:-5188}"

[[ "$(id -u)" == 0 ]] || { echo "run with sudo: sudo bash $0"; exit 1; }
id "$RUN_USER" >/dev/null 2>&1 || { echo "no user $RUN_USER: Laplace-Operations' sudo ./setup.sh packages makes it"; exit 1; }
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

echo "=== units"
install -m 0644 "$HERE/laplace-api.service" /etc/systemd/system/laplace-api.service
install -m 0644 "$HERE/laplace-api-restart.service" /etc/systemd/system/laplace-api-restart.service
install -m 0644 "$HERE/laplace-api-restart.path" /etc/systemd/system/laplace-api-restart.path
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
DB_HOST="${LAPLACE_PGHOST:-$(sed -n 's/^LAPLACE_PGHOST=//p' /etc/laplace/machine.env 2>/dev/null | tail -1)}"; DB_HOST="${DB_HOST:-/tmp}"
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
install -m 0644 "$HERE/laplace-mcp.service" /etc/systemd/system/laplace-mcp.service
install -m 0644 "$HERE/laplace-mcp-restart.service" /etc/systemd/system/laplace-mcp-restart.service
install -m 0644 "$HERE/laplace-mcp-restart.path" /etc/systemd/system/laplace-mcp-restart.path
systemctl daemon-reload
systemctl enable -q laplace-mcp.service
systemctl enable -q --now laplace-mcp-restart.path
[[ -f "$PREFIX/app/mcp-runtime/Laplace.Endpoints.Mcp.dll" && -f "$PREFIX/app/laplace-mcp.env" ]] && systemctl restart laplace-mcp.service
say "laplace-mcp.service" "$(systemctl is-enabled laplace-mcp.service), $(systemctl is-active laplace-mcp.service || true) (starts once site.sh has published)"
say "laplace-mcp-restart.path" "$(systemctl is-active laplace-mcp-restart.path): $LOCKS/restart-mcp"
fi

echo "=== nginx"
command -v nginx >/dev/null || { echo "nginx is not installed: apt-get install nginx"; exit 1; }
sed "s|127.0.0.1:5188;|127.0.0.1:$MCP_PORT;|" "$HERE/nginx-laplace.conf" > /etc/nginx/sites-available/laplace; chmod 0644 /etc/nginx/sites-available/laplace
ln -sfn /etc/nginx/sites-available/laplace /etc/nginx/sites-enabled/laplace
rm -f /etc/nginx/sites-enabled/laplace-managed
nginx -t -q
if systemctl is-active -q nginx; then systemctl reload nginx; else systemctl start nginx; fi
say "nginx site laplace" ":$SITE_PORT -> 127.0.0.1:5187; /mcp -> 127.0.0.1:$MCP_PORT"

echo "=== firewall"
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow from "$LAN" to any port "$SITE_PORT" proto tcp comment "laplace site" >/dev/null
  say "ufw" "$SITE_PORT/tcp from $LAN"
else say "ufw" "not active: nothing to do"; fi

echo
echo "done. The runner deploys with deploy/linux/site.sh on every merge to main (.github/workflows/laplace.yml)."
