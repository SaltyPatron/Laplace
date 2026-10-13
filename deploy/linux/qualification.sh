#!/usr/bin/env bash
# Sourced by site.sh while it owns the installed host's resource reservation.
qualification_environment() {
  cd "$ROOT"
  export LAPLACE_BUILD_DIRECTORY="$SITE_BUILD" LAPLACE_ENGINE_BUILD="$SITE_BUILD/engine"
  source "$ROOT/scripts/lib/storage.sh"
  laplace_storage_init
  python3 "$ROOT/scripts/place-build-directory.py" "$ROOT" >/dev/null
  export LAPLACE_QUALIFICATION_RESULTS="${LAPLACE_QUALIFICATION_RESULTS:-$SITE_WORK/qualification}"
  mkdir -p "$LAPLACE_QUALIFICATION_RESULTS"
  export LAPLACE_SITE_BUILD_TESTING=ON
  export LAPLACE_REUSE_INSTALLED_NATIVE=0
  export LD_LIBRARY_PATH="$SITE_BUILD/engine/core:$SITE_BUILD/engine/dynamics:$SITE_BUILD/engine/synthesis:${LD_LIBRARY_PATH:-}"
}

qualification_suite() {
  local suite="$1" rc=0
  bash "$ROOT/scripts/test-parallel.sh" --suite "$suite" 2>&1 |
    tee "$LAPLACE_QUALIFICATION_RESULTS/$suite.log" || rc=$?
  printf '%s\t%s\n' "$suite" "$rc" >> "$LAPLACE_QUALIFICATION_RESULTS/results.tsv"
  return "$rc"
}

qualification_dependencies() {
  local source="$LAPLACE_EXTERNAL/googletest" revision=063de7e9578f82b369302001269680b4b1553359
  [[ ! -f "$source/CMakeLists.txt" ]] || return 0
  [[ ! -e "$source" ]] || fail "incomplete declared GoogleTest source: $source"
  local pending
  pending=$(mktemp -d "$LAPLACE_EXTERNAL/.googletest-XXXXXXXX")
  if git -C "$pending" init -q &&
     git -C "$pending" fetch --depth=1 https://github.com/google/googletest.git "$revision" &&
     git -C "$pending" checkout --detach FETCH_HEAD &&
     [[ "$(git -C "$pending" rev-parse HEAD)" == "$revision" ]]; then
    mv -T "$pending" "$source"
  else
    rm -rf -- "$pending"
    fail "could not prepare pinned GoogleTest v1.18.0 in $source"
  fi
}

phase_qualification() {
  qualification_environment
  : > "$LAPLACE_QUALIFICATION_RESULTS/results.tsv"
  git -C "$ROOT" rev-parse HEAD > "$LAPLACE_QUALIFICATION_RESULTS/source-revision"
  phase_status > "$LAPLACE_QUALIFICATION_RESULTS/installed-before.json"
  phase_externals
  qualification_dependencies
  if [[ "${LAPLACE_FULL_CLEAN:-0}" == 1 && -f "$SITE_BUILD/build.ninja" ]]; then
    cmake --build "$SITE_BUILD" --target clean
    dotnet_app clean "$ROOT/app/Laplace.slnx" -c Release --nologo
  fi
  if [[ "${LAPLACE_FORCE_CODEGEN:-0}" == 1 ]]; then
    python3 "$ROOT/scripts/codegen-attestation-law.py"
  fi
  phase_build
  dotnet_app build "$ROOT/app/Laplace.slnx" -c Release --nologo
  (cd "$ROOT/web"; npm ci --no-audit --no-fund --prefer-offline; npm run gen:api; npm run build)
  local suite rc=0 failed=0
  # Report every component that fails, while retaining the nonzero result.
  for suite in native-dev managed-dev uci-dev browser-dev; do
    rc=0; qualification_suite "$suite" || rc=$?
    (( rc == 0 )) || failed=1
  done
  phase_status > "$LAPLACE_QUALIFICATION_RESULTS/installed-after.json"
  return "$failed"
}

phase_competitive() {
  qualification_environment
  # Resolve complete declared model/corpus inputs before changing the installation.
  bash "$ROOT/scripts/model-synthesize-ci.sh" --preflight
  phase_qualification
  phase_install
  phase_database
  phase_extensions
  phase_migrate
  phase_app
  phase_publish
  phase_restart
  phase_smoke
  export LAPLACE_REUSE_INSTALLED_NATIVE=1 LAPLACE_DB_HEALTH_SCOPE=installed
  local suite rc=0 failed=0
  for suite in db-health native-db managed-db; do
    rc=0; qualification_suite "$suite" || rc=$?
    (( rc == 0 )) || failed=1
  done
  (( failed == 0 )) || return 1
  export LAPLACE_GGUF_OUT="${LAPLACE_GGUF_OUT:-$SITE_WORK/model-proof/substrate.gguf}"
  export LAPLACE_MODEL_BEHAVIOR_REPORT="$LAPLACE_QUALIFICATION_RESULTS/model-behavior.json"
  mkdir -p "$(dirname "$LAPLACE_GGUF_OUT")"
  bash "$ROOT/scripts/model-synthesize-ci.sh"
  phase_reconcile
  export LAPLACE_API_BASE="${LAPLACE_PUBLIC_BASE_URL:-$LAPLACE_API_URL}"
  export LAPLACE_PUBLIC_UI_BASE="${LAPLACE_PUBLIC_BASE_URL:-$LAPLACE_SITE_URL}"
  python3 "$ROOT/scripts/run-authenticated-verification.py" --base "$LAPLACE_API_BASE" -- \
    bash "$ROOT/scripts/test-parallel.sh" --profile live
  phase_status > "$LAPLACE_QUALIFICATION_RESULTS/installed-after.json"
}
