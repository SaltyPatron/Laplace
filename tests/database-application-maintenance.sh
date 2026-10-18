#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/laplace-app-maintenance.XXXXXX")
mkdir -p "$work/scripts/lib" "$work/runtime/migrations" "$work/bin"
mkdir -p "$work/deploy/linux"
cp "$root/scripts/db-migrations.sh" "$work/scripts/db-migrations.sh"
cp "$root/deploy/linux/platform-owner-policy.py" "$work/deploy/linux/"
: > "$work/scripts/lib/database.sh"
: > "$work/runtime/migrations/Laplace.Migrations.dll"
printf 'set -euo pipefail\n' > "$work/dispatch.sh"
sed -n '/^          case "${LAPLACE_DB_SCOPE/,/^          case "$LAPLACE_DB_OPERATION"/p' "$root/.github/workflows/db-ops.yml" |
  sed '$d; s/^          //' >> "$work/dispatch.sh"
[[ -s "$work/dispatch.sh" ]]
export TEST_EVENTS="$work/events" LAPLACE_APP_DIR="$work/runtime"
unset LAPLACE_PLATFORM_OWNER_POLICY_FILE
export LAPLACE_WORK_ROOT="$work/no-owner-policy"
cat > "$work/bin/dotnet" <<'STUB'
#!/usr/bin/env bash
shift
if [[ "$1" == --help ]]; then
  if [[ "${TEST_OLD_RUNTIME:-}" == 1 ]]; then printf '%s\n' up status reset nuke; exit 0; fi
  if [[ "${GITHUB_ACTIONS:-}" == true && -z "${NO_COLOR:-}" ]]; then
    printf '    \033[38;5;7m%s\033[0m\n' app-up app-status app-check app-reset app-nuke substrate-up substrate-status
  else
    printf '%s\n' app-up app-status app-check app-reset app-nuke substrate-up substrate-status
  fi
  exit 0
fi
printf '%s\n' "$*" >> "$TEST_EVENTS"
[[ "$1" != "${TEST_FAIL_VERB:-}" ]] || exit 23
STUB
chmod +x "$work/bin/dotnet"
export PATH="$work/bin:$PATH"
cd "$work"
run() {
  : > "$TEST_EVENTS"
  LAPLACE_DB_SCOPE=application LAPLACE_DB_OPERATION="$1" LAPLACE_APP_DB='Host=fixture;Database=accounts' bash dispatch.sh
}
for operation in migrate repair; do
  run "$operation"
  [[ "$(cat "$TEST_EVENTS")" == $'app-up\napp-check' ]]
done
run status
[[ "$(cat "$TEST_EVENTS")" == $'app-status\napp-check' ]]
run remigrate
[[ "$(cat "$TEST_EVENTS")" == $'app-reset --yes\napp-up\napp-check' ]]
run recreate
[[ "$(cat "$TEST_EVENTS")" == $'app-nuke --yes\napp-check' ]]
echo 'PASS application selection routes all supported operations through the actual migration wrapper'
export GITHUB_ACTIONS=true
unset NO_COLOR
run recreate
[[ "$(cat "$TEST_EVENTS")" == $'app-nuke --yes\napp-check' ]]
echo 'PASS GitHub Actions color formatting does not hide supported migration commands'
for operation in reindex invalid; do
  if run "$operation"; then echo "FAIL accepted $operation"; exit 1; fi
  [[ ! -s "$TEST_EVENTS" ]]
done
: > "$TEST_EVENTS"
if env -u LAPLACE_APP_DB LAPLACE_DB_SCOPE=application LAPLACE_DB_OPERATION=repair bash dispatch.sh; then
  echo 'FAIL accepted missing application target'; exit 1
fi
[[ ! -s "$TEST_EVENTS" ]]
echo 'PASS unsupported operations and missing target stop before migrations'
export TEST_FAIL_VERB=app-up
if run repair; then echo 'FAIL ignored migration failure'; exit 1; else rc=$?; fi
[[ "$rc" == 23 && "$(cat "$TEST_EVENTS")" == app-up ]]
echo 'PASS migration failure prevents verification and preserves its exit code'
unset TEST_FAIL_VERB
export TEST_OLD_RUNTIME=1
if run recreate; then echo 'FAIL accepted a legacy runtime'; exit 1; fi
[[ ! -s "$TEST_EVENTS" ]]
echo 'PASS legacy runtime cannot reinterpret application reset as a default migration'
echo "Fixture: $work"
