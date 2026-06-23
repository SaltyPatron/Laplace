#!/usr/bin/env bash
# One-time privileged host setup for the Laplace API on hart-server.
# Run ONCE as root (or: sudo deploy/linux/bootstrap-host.sh). Idempotent.
#
# Separates privileged setup (systemd unit, nginx vhost, narrow sudoers grant)
# from the routine, unprivileged CICD deploy (deploy.sh, run as laplace-runner).
set -euo pipefail

# Resolve to this script's own directory so it works both from a repo checkout
# (deploy/linux/) and from a flat staging copy.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR=/opt/laplace/app
RUN_USER=laplace-runner
RUN_GROUP=laplace-runner
API_PORT="${API_PORT:-8080}"
# LAN subnet allowed to reach the API port — auto-derived from the default-route
# interface's on-link route below; override with LAN_CIDR=... sudo -E ...
LAN_CIDR="${LAN_CIDR:-}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "✗ must run as root (use sudo)"; exit 1
fi

echo "==> app dir: $APP_DIR (owner $RUN_USER)"
install -d -m 2775 -o "$RUN_USER" -g "$RUN_GROUP" "$APP_DIR" "$APP_DIR/logs"

echo "==> env file: $APP_DIR/laplace-api.env"
if [[ ! -f "$APP_DIR/laplace-api.env" ]]; then
  install -m 0640 -o "$RUN_USER" -g "$RUN_GROUP" "$HERE/laplace-api.env.example" "$APP_DIR/laplace-api.env"
  echo "   created from example — edit secrets in place; CICD does not overwrite it."
else
  echo "   exists — left untouched."
fi

echo "==> systemd unit: /etc/systemd/system/laplace-api.service"
install -m 0644 "$HERE/laplace-api.service" /etc/systemd/system/laplace-api.service

echo "==> nginx vhost: /etc/nginx/sites-available/laplace (port 8080)"
install -m 0644 "$HERE/nginx-laplace.conf" /etc/nginx/sites-available/laplace
ln -sfn /etc/nginx/sites-available/laplace /etc/nginx/sites-enabled/laplace

echo "==> sudoers grant for $RUN_USER (restart service + reload nginx only)"
cat > /etc/sudoers.d/laplace-runner-deploy <<EOF
# Allow the CICD runner to manage just the Laplace API service + reload nginx.
$RUN_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart laplace-api, \\
  /usr/bin/systemctl start laplace-api, /usr/bin/systemctl stop laplace-api, \\
  /usr/bin/systemctl status laplace-api, /usr/bin/systemctl is-active laplace-api, \\
  /usr/sbin/nginx -t, /usr/sbin/nginx -s reload
EOF
chmod 0440 /etc/sudoers.d/laplace-runner-deploy
visudo -cf /etc/sudoers.d/laplace-runner-deploy >/dev/null

echo "==> reload systemd + enable service (not started until first deploy)"
systemctl daemon-reload
systemctl enable laplace-api >/dev/null

echo "==> validate + reload nginx"
nginx -t
systemctl reload nginx

echo "==> firewall: allow API port $API_PORT from the LAN if ufw is active"
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  if [[ -z "$LAN_CIDR" ]]; then
    def_if=$(ip route show default | awk '{print $5; exit}')
    LAN_CIDR=$(ip -4 route show dev "$def_if" scope link proto kernel 2>/dev/null | awk '{print $1; exit}')
  fi
  if [[ -z "$LAN_CIDR" ]]; then
    echo "   ✗ could not derive LAN subnet; re-run with LAN_CIDR=x.x.x.x/yy. Skipping."
  else
    # `ufw allow` is idempotent — re-adding an identical rule is a no-op.
    ufw allow from "$LAN_CIDR" to any port "$API_PORT" proto tcp comment "laplace-api LAN" >/dev/null
    echo "   ufw rule ensured: $API_PORT/tcp from $LAN_CIDR"
  fi
else
  echo "   ufw not active — nothing to do"
fi

echo "✓ bootstrap complete. Now run deploy.sh (as $RUN_USER, via CICD) to publish + start."
