# shellcheck shell=bash
# Build identity helpers only. Execution is never skipped here: CMake/Ninja and
# MSBuild own incremental work. The identity is retained solely so a standalone
# install can refuse artifacts that were built from a different source state.

FP_STAMP_DIR="$ROOT/build/.stamps"

FP_NATIVE_PATHS=(
  engine
  extension
  cmake
  CMakeLists.txt
  deploy/postgresql-release.json
  scripts/postgresql-release.py
  scripts/codegen-attestation-law.py
  deploy/cmake-release.json
  scripts/provision-cmake.py
  scripts/chess-floor-artifacts.py
  app/ChessCatalogSurfaces
  app/Laplace.Chess
  app/Laplace.Core
  app/Laplace.Substrate
)

fp_compute() {
  local f h
  {
    git -C "$ROOT" ls-files -s -- "$@" 2>/dev/null || echo "no-git"
    while IFS= read -r f; do
      [[ -z "$f" ]] && continue
      if [[ -f "$ROOT/$f" ]]; then
        h=$(sha256sum <"$ROOT/$f" 2>/dev/null) || h="unreadable"
        printf 'dirty %s %s\n' "$f" "${h%% *}"
      else
        printf 'gone %s\n' "$f"
      fi
    done < <(git -C "$ROOT" diff --name-only -- "$@" 2>/dev/null)
    while IFS= read -r f; do
      [[ -z "$f" ]] && continue
      h=$(sha256sum <"$ROOT/$f" 2>/dev/null) || h="unreadable"
      printf 'new %s %s\n' "$f" "${h%% *}"
    done < <(git -C "$ROOT" ls-files --others --exclude-standard -- "$@" 2>/dev/null)
  } | LC_ALL=C sort | sha256sum | cut -d' ' -f1
}

# Bind build/install identity to the tools and headers actually consumed.
# This adds input identity; CMake/MSBuild remain the only skip authorities.
fp_postgresql_inputs() {
  python3 "$ROOT/scripts/postgresql-release.py" build-inputs \
    --prefix "${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
}

fp_chess_corpus_inputs() {
  python3 "$ROOT/scripts/chess-floor-artifacts.py" selected-export \
    --prefix "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
}

fp_chess_openings_path() {
  if [[ -n "${LAPLACE_CHESS_OPENINGS:-}" ]]; then
    printf '%s\n' "$LAPLACE_CHESS_OPENINGS"
    return
  fi
  local chess_root="${LAPLACE_DATA_ROOT:-/vault/Data}/Games/Chess" candidate
  for candidate in "$chess_root/lichess-openings" "$chess_root/openings"; do
    if [[ -d "$candidate" ]] && [[ -n "$(find -H "$candidate" -type f -name '*.tsv' -print -quit)" ]]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
  printf '%s\n' "$chess_root/lichess-openings"
}

fp_chess_openings_inputs() {
  local corpus file
  corpus=$(fp_chess_openings_path)
  printf 'chess-openings %s\n' "$corpus"
  if [[ -f "$corpus" ]]; then
    sha256sum -- "$corpus"
  elif [[ -d "$corpus" ]]; then
    while IFS= read -r -d '' file; do
      sha256sum -- "$file"
    done < <(find -H "$corpus" -type f -name '*.tsv' -print0 | LC_ALL=C sort -z)
  else
    printf 'absent\n'
  fi
}

fp_native() {
  local corpus_inputs postgresql_inputs
  corpus_inputs=$(fp_chess_corpus_inputs) || return
  postgresql_inputs=$(fp_postgresql_inputs) || return
  {
    fp_compute "${FP_NATIVE_PATHS[@]}"
    fp_chess_openings_inputs
    printf '%s\n' "$corpus_inputs"
    printf '%s\n' "$postgresql_inputs"
  } | sha256sum | cut -d' ' -f1
}

fp_runtime() {
  local corpus_inputs postgresql_inputs
  corpus_inputs=$(fp_chess_corpus_inputs) || return
  postgresql_inputs=$(fp_postgresql_inputs) || return
  {
    fp_compute "${FP_NATIVE_PATHS[@]}" app/Laplace.Migrations
    fp_chess_openings_inputs
    printf '%s\n' "$corpus_inputs"
    printf '%s\n' "$postgresql_inputs"
  } | sha256sum | cut -d' ' -f1
}

# Deliberately never authorize a skip. Native build systems already implement
# dependency-aware incremental execution and are the only skip authority.
fp_check() {
  return 1
}

fp_record() {
  mkdir -p "$FP_STAMP_DIR"
  printf '%s' "$2" >"$FP_STAMP_DIR/$1"
}
