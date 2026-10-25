#!/usr/bin/env bash
# Codespaces & Devcontainer Bootstrap (Non-systemd multi-repo & PostgreSQL 18.6 initializer)
#
# Modes:
#   ./bootstrap-codespace.sh create   (postCreateCommand: clone/link sibling repos, init PG 18.6 cluster, create roles/DBs)
#   ./bootstrap-codespace.sh start    (postStartCommand: ensure PG 18.6 is running and /run/lock/laplace exists)
set -euo pipefail

MODE="${1:-all}"
LAPLACE_SRC="${LAPLACE_SRC:-/repos/src}"
LAPLACE_BUILD="${LAPLACE_BUILD:-/repos/build}"
LAPLACE_WORK="${LAPLACE_WORK:-/repos/work}"
LAPLACE_DEPS="${LAPLACE_DEPS:-/repos/deps}"
LAPLACE_DATA="${LAPLACE_DATA:-/vault/Data}"
LAPLACE_MODELS="${LAPLACE_MODELS:-/vault/Models}"
LAPLACE_PG_DIR="${LAPLACE_PG_DIR:-/usr/local/pgsql}"
LAPLACE_PGDATA="${LAPLACE_PGDATA:-/data/pgdata}"
LAPLACE_PGWAL="${LAPLACE_PGWAL:-/data/pgwal}"
LAPLACE_PGTEMP="${LAPLACE_PGTEMP:-/data/pgtemp}"
LAPLACE_PGUSER="${LAPLACE_PGUSER:-postgres}"
LAPLACE_ROLE="${LAPLACE_ROLE:-laplace}"
LAPLACE_GROUP="${LAPLACE_GROUP:-laplace-runner}"
LAPLACE_GITHUB="${LAPLACE_GITHUB:-SaltyPatron}"
PGBIN="${LAPLACE_PG_DIR}/bin"
OPERATOR="$(id -un)"

SIBLING_REPOS=(
  "Laplace"
  "Laplace-Native"
  "Laplace-postgres"
  "Laplace-Engine"
  "Laplace-MCP"
  "Laplace-UCI"
  "Laplace-Operations"
  "Laplace-Wiki"
  "Laplace-Prototype"
  "Laplace-Conventional"
)

ensure_runtime_dirs() {
  sudo install -d -m 2775 -g "${LAPLACE_GROUP}" \
    "${LAPLACE_SRC}" "${LAPLACE_BUILD}" "${LAPLACE_WORK}" "${LAPLACE_DEPS}" \
    "${LAPLACE_DATA}" "${LAPLACE_MODELS}" /run/lock/laplace /opt/laplace
  sudo install -d -o "${LAPLACE_PGUSER}" -g "${LAPLACE_PGUSER}" -m 0700 \
    "${LAPLACE_PGDATA}" "${LAPLACE_PGWAL}" "${LAPLACE_PGTEMP}"
}

sync_sibling_repos() {
  local ws_root="/workspaces"
  sudo install -d -m 2775 -g "${LAPLACE_GROUP}" "${ws_root}" "${LAPLACE_SRC}"

  for repo in "${SIBLING_REPOS[@]}"; do
    if [[ -d "${ws_root}/${repo}/.git" && ! -e "${LAPLACE_SRC}/${repo}" ]]; then
      sudo ln -sfn "${ws_root}/${repo}" "${LAPLACE_SRC}/${repo}"
    fi
  done

  for repo in "${SIBLING_REPOS[@]}"; do
    local ws_dir="${ws_root}/${repo}"
    local src_link="${LAPLACE_SRC}/${repo}"
    if [[ ! -d "${ws_dir}/.git" && ! -d "${src_link}/.git" ]]; then
      if gh auth status >/dev/null 2>&1; then
        echo "=== Cloning sibling repository ${LAPLACE_GITHUB}/${repo} into ${ws_dir}"
        gh repo clone "${LAPLACE_GITHUB}/${repo}" "${ws_dir}" -- --depth 1 || true
      elif [[ -n "${GITHUB_TOKEN:-}" ]]; then
        echo "=== Cloning sibling repository ${LAPLACE_GITHUB}/${repo} via GITHUB_TOKEN"
        git clone -q --depth 1 "https://x-access-token:${GITHUB_TOKEN}@github.com/${LAPLACE_GITHUB}/${repo}.git" "${ws_dir}" || true
      fi
    fi
    if [[ -d "${ws_dir}/.git" ]]; then
      sudo ln -sfn "${ws_dir}" "${src_link}"
    fi
  done

  cat > "${ws_root}/laplace.code-workspace" <<'EOF'
{
  "folders": [
    { "path": "Laplace" },
    { "path": "Laplace-Native" },
    { "path": "Laplace-postgres" },
    { "path": "Laplace-Engine" },
    { "path": "Laplace-MCP" },
    { "path": "Laplace-UCI" },
    { "path": "Laplace-Operations" },
    { "path": "Laplace-Prototype" },
    { "path": "Laplace-Wiki" },
    { "path": "Laplace-Conventional" }
  ],
  "settings": {
    "files.exclude": {
      "**/.git": true
    }
  }
}
EOF
}

init_pg_cluster() {
  if [[ ! -f "${LAPLACE_PGDATA}/PG_VERSION" ]]; then
    echo "=== Initializing PostgreSQL 18.6 cluster in ${LAPLACE_PGDATA}"
    sudo -u "${LAPLACE_PGUSER}" "${PGBIN}/initdb" -D "${LAPLACE_PGDATA}" --locale=C.UTF-8 --encoding=UTF8 --data-checksums >/dev/null
    cat <<EOF | sudo -u "${LAPLACE_PGUSER}" tee "${LAPLACE_PGDATA}/pg_ident.conf" >/dev/null
laplace   ${LAPLACE_PGUSER}    ${LAPLACE_ROLE}
laplace   ${OPERATOR}         ${LAPLACE_ROLE}
laplace   root                ${LAPLACE_ROLE}
EOF
    cat <<EOF | sudo -u "${LAPLACE_PGUSER}" tee "${LAPLACE_PGDATA}/pg_hba.conf" >/dev/null
local   all   ${LAPLACE_PGUSER}                 peer
local   all   ${LAPLACE_ROLE}                   peer map=laplace
local   all   all                               trust
host    all   all               127.0.0.1/32    trust
host    all   all               ::1/128         trust
EOF
    cat <<EOF | sudo -u "${LAPLACE_PGUSER}" tee -a "${LAPLACE_PGDATA}/postgresql.conf" >/dev/null
unix_socket_directories = '/tmp'
listen_addresses = 'localhost'
port = 5432
shared_buffers = 1GB
work_mem = 64MB
maintenance_work_mem = 512MB
max_worker_processes = 16
max_parallel_workers = 8
shared_preload_libraries = 'pg_stat_statements,auto_explain'
EOF
  fi
}

start_pg_cluster() {
  if [[ ! -x "${PGBIN}/pg_ctl" ]]; then
    echo "PostgreSQL binary not yet built at ${PGBIN}/pg_ctl; skipping start."
    return 0
  fi
  init_pg_cluster
  if ! sudo -u "${LAPLACE_PGUSER}" "${PGBIN}/pg_isready" -q -h /tmp; then
    echo "=== Starting PostgreSQL 18.6 cluster (${LAPLACE_PGDATA})"
    sudo -u "${LAPLACE_PGUSER}" "${PGBIN}/pg_ctl" -D "${LAPLACE_PGDATA}" -l "${LAPLACE_WORK}/postgresql.log" start
  fi
  sudo -u "${LAPLACE_PGUSER}" "${PGBIN}/psql" -X -h /tmp -d postgres -v ON_ERROR_STOP=1 <<SQL >/dev/null
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${LAPLACE_ROLE}') THEN
    CREATE ROLE "${LAPLACE_ROLE}" SUPERUSER LOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${OPERATOR}') THEN
    CREATE ROLE "${OPERATOR}" SUPERUSER LOGIN;
  END IF;
END
\$\$;
SQL
  for db in laplace laplace-mono laplace-app; do
    exists=$(sudo -u "${LAPLACE_PGUSER}" "${PGBIN}/psql" -X -h /tmp -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '${db}'")
    if [[ "${exists}" != "1" ]]; then
      sudo -u "${LAPLACE_PGUSER}" "${PGBIN}/createdb" -h /tmp -O "${LAPLACE_ROLE}" "${db}"
    fi
  done
  echo "=== PostgreSQL 18.6 ready on /tmp:5432 (databases: laplace, laplace-mono, laplace-app)"
}

case "${MODE}" in
  create)
    ensure_runtime_dirs
    sync_sibling_repos
    start_pg_cluster
    ;;
  start)
    ensure_runtime_dirs
    start_pg_cluster
    ;;
  all|*)
    ensure_runtime_dirs
    sync_sibling_repos
    start_pg_cluster
    ;;
esac
