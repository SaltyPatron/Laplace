#!/usr/bin/env bash
# Chess Lab binaries for Linux — implementation detail.
# Invoked by: setup-host (via bootstrap-laplace-runner) and pipeline.sh publish.
# Humans: do not run this; run sudo bash scripts/setup-host.sh once, then CI.

set -euo pipefail
umask 0002

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

RUNNER_USER="${RUNNER_USER:-laplace-runner}"
RUNNER_GROUP="${RUNNER_GROUP:-laplace-runner}"
PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
EXTERNAL="${LAPLACE_EXTERNAL:-/build/external}"
QT_ROOT="${LAPLACE_QT_ROOT:-${LAPLACE_DEPS_PREFIX:-/opt/laplace}/qt}"
WORK="${LAPLACE_WORK_ROOT:-/build/laplace/work}/chess-tools"
export TMPDIR="$WORK" TMP="$WORK" TEMP="$WORK"
# Build tree beside the source on /build, not under $PREFIX on the database device.
# It defaulted to $PREFIX/build-cutechess (nvme1n1) and its CMakeCache pinned
# CMAKE_HOME_DIRECTORY to the OLD source path, so the first run after the source
# moved failed with "does not match the source used to generate cache" (2026-08-12).
CC_BUILD="${LAPLACE_CUTECHESS_BUILD:-/build/cutechess}"
CC_BIN_DIR="$PREFIX/bin"
APP_DIR="$PREFIX/app"
ENV_FILE="$APP_DIR/laplace-api.env"

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "=== $1 ==="; }

run_as_owner() {
  if [ "$(id -u)" -eq 0 ]; then
    local key
    local -a build_env=("LAPLACE_EXTERNAL=$EXTERNAL" "TMPDIR=$WORK" "TMP=$WORK" "TEMP=$WORK")
    for key in LAPLACE_STOCKFISH_SOURCE LAPLACE_STOCKFISH_COMP LAPLACE_STOCKFISH_ARCH \
      LAPLACE_STOCKFISH_JOBS LAPLACE_BUILD_JOBS LAPLACE_DEPS_PREFIX CMAKE_BUILD_PARALLEL_LEVEL MAKEFLAGS CC CXX; do
      if [[ -v "$key" ]]; then build_env+=("$key=${!key}"); fi
    done
    sudo -u "$RUNNER_USER" -H env "${build_env[@]}" "$@"
  else
    "$@"
  fi
}

resolve_stockfish() {
  if [[ -n "${LAPLACE_STOCKFISH:-}" ]]; then
    printf '%s\n' "$LAPLACE_STOCKFISH"
  else
    python3 "$SCRIPT_DIR/install-stockfish.py" --print-path
  fi
}

resolve_qt_bin() {
  local version
  version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["qt_version"])' "$REPO_ROOT/deploy/cutechess-release.json")"
  [ -x "$QT_ROOT/$version/gcc_64/bin/qmake" ] || return 1
  echo "$QT_ROOT/$version/gcc_64/bin"
}

ensure_dirs() {
  say "dirs under $PREFIX"
  if [ "$(id -u)" -eq 0 ]; then
    install -d -m 2775 -g "$RUNNER_GROUP" \
      "$PREFIX" "$EXTERNAL" "$CC_BUILD" "$CC_BIN_DIR" "$APP_DIR" "$APP_DIR/logs" "$QT_ROOT" "$WORK"
    install -d -m 2770 -g "$RUNNER_GROUP" "$PREFIX/secrets"
  else
    mkdir -p "$CC_BUILD" "$CC_BIN_DIR" "$APP_DIR/logs" "$PREFIX/secrets" "$QT_ROOT" "$WORK"
    chmod 2770 "$PREFIX/secrets" 2>/dev/null || true
  fi
}

build_cutechess() {
  say "update and build CuteChess from $EXTERNAL/cutechess"
  local src qt
  src="$(run_as_owner python3 "$SCRIPT_DIR/provision-cutechess.py" --source-dir "$EXTERNAL/cutechess")"
  qt="$(run_as_owner python3 "$SCRIPT_DIR/provision-chess-qt.py" --root "$QT_ROOT" --work "$WORK")"
  # --fresh removes only generated CMake cache metadata, preserving source and
  # build outputs while admitting a source path/Qt SDK changed since last run.
  run_as_owner cmake --fresh -S "$src" -B "$CC_BUILD" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DWITH_TESTS=OFF -DCMAKE_PREFIX_PATH="$qt" \
    -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON -DCMAKE_INSTALL_RPATH="$qt/lib"
  run_as_owner cmake --build "$CC_BUILD" --target cli
  run_as_owner python3 "$SCRIPT_DIR/provision-cutechess.py" --binary "$CC_BUILD/cutechess-cli"
  local staged
  staged="$(mktemp "$CC_BIN_DIR/.cutechess-cli.XXXXXX")"
  if install -m 0755 "$CC_BUILD/cutechess-cli" "$staged"; then
    mv -Tf "$staged" "$CC_BIN_DIR/cutechess-cli"
  else
    rm -f "$staged"
    return 1
  fi
}

build_zstd() {
  say "update and build native Zstandard from its official source"
  local library
  library="$(run_as_owner python3 "$SCRIPT_DIR/install-zstd.py" \
    --source-dir "${LAPLACE_ZSTD_SOURCE:-$EXTERNAL/zstd}" \
    --build-dir "${LAPLACE_ZSTD_BUILD:-${LAPLACE_BUILD_ROOT:-/build/laplace/build}/zstd}")"
  if [[ -z "${LAPLACE_ZSTD_LIBRARY:-}" ]]; then export LAPLACE_ZSTD_LIBRARY="$library"; fi
}

write_api_env() {
  say "api env chess-lab paths → $ENV_FILE"
  if [ ! -d "$APP_DIR" ]; then
    yellow "$APP_DIR missing — setup-host creates it"
    return 0
  fi

  local sf qt cc
  sf="$(resolve_stockfish || true)"
  qt="$(resolve_qt_bin || true)"
  cc="${LAPLACE_CUTECHESS:-$CC_BIN_DIR/cutechess-cli}"
  [ -x "$cc" ] || cc=""

  if [ ! -f "$ENV_FILE" ]; then
    if [ -f "$REPO_ROOT/deploy/linux/laplace-api.env.example" ]; then
      if [ "$(id -u)" -eq 0 ]; then
        install -m 0640 -o "$RUNNER_USER" -g "$RUNNER_GROUP" \
          "$REPO_ROOT/deploy/linux/laplace-api.env.example" "$ENV_FILE"
      else
        cp "$REPO_ROOT/deploy/linux/laplace-api.env.example" "$ENV_FILE"
        chmod 0640 "$ENV_FILE"
      fi
    else
      return 1
    fi
  fi

  local marker_begin="# >>> laplace-runner managed: chess-lab env >>>"
  local marker_end="# <<< laplace-runner managed: chess-lab env <<<"
  sed -i -e "/$marker_begin/,/$marker_end/d" "$ENV_FILE"
  {
    echo "$marker_begin"
    [ -n "$cc" ] && echo "LAPLACE_CUTECHESS=$cc"
    [ -n "$sf" ] && echo "LAPLACE_STOCKFISH=$sf"
    [ -n "$qt" ] && echo "LAPLACE_QT_BIN=$qt"
    echo "LAPLACE_EXTERNAL=$EXTERNAL"
    [ -z "${LAPLACE_STOCKFISH_SOURCE:-}" ] || echo "LAPLACE_STOCKFISH_SOURCE=$LAPLACE_STOCKFISH_SOURCE"
    [ -z "${LAPLACE_ZSTD_LIBRARY:-}" ] || echo "LAPLACE_ZSTD_LIBRARY=$LAPLACE_ZSTD_LIBRARY"
    [ -z "${LAPLACE_ZSTD_SOURCE:-}" ] || echo "LAPLACE_ZSTD_SOURCE=$LAPLACE_ZSTD_SOURCE"
    [ -z "${LAPLACE_ZSTD_BUILD:-}" ] || echo "LAPLACE_ZSTD_BUILD=$LAPLACE_ZSTD_BUILD"
    [ -z "${LAPLACE_ZSTD_WINDOW_LOG_MAX:-}" ] || echo "LAPLACE_ZSTD_WINDOW_LOG_MAX=$LAPLACE_ZSTD_WINDOW_LOG_MAX"
    echo "LAPLACE_CUTECHESS_BUILD=$CC_BUILD"
    echo "LAPLACE_CHESS_LAB_DIR=$PREFIX/chess-lab-work"
    echo "$marker_end"
  } >> "$ENV_FILE"

  if [ "$(id -u)" -eq 0 ]; then
    chgrp "$RUNNER_GROUP" "$ENV_FILE"
    chmod 0640 "$ENV_FILE"
    install -d -m 2775 -g "$RUNNER_GROUP" "$PREFIX/chess-lab-work"
  else
    mkdir -p "$PREFIX/chess-lab-work"
  fi

  echo "  CUTECHESS=${cc:-MISSING}  STOCKFISH=${sf:-MISSING}  QT=${qt:-MISSING}"
  if [ -f "$PREFIX/secrets/lichess.env" ]; then
    green "✓ secrets drop present"
  else
    yellow "✓ no lichess.env yet — seeded by setup-host from ~/.config/shell/secrets.env"
  fi
}

verify() {
  say "verify"
  local fail=0 sf qt zstd_version
  sf="$(resolve_stockfish || true)"
  qt="$(resolve_qt_bin || true)"
  python3 "$SCRIPT_DIR/provision-cutechess.py" --binary "${LAPLACE_CUTECHESS:-$CC_BIN_DIR/cutechess-cli}" || { red "✗ cutechess-cli / Qt runtime"; fail=1; }
  python3 "$SCRIPT_DIR/install-stockfish.py" --check-binary "$sf" || { red "✗ stockfish"; fail=1; }
  zstd_version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$REPO_ROOT/deploy/zstd-release.json")"
  python3 "$SCRIPT_DIR/check-zstd-runtime.py" --require-version "$zstd_version" || { red "✗ native Zstandard PGN decoder"; fail=1; }
  [ -n "$qt" ] || { red "✗ Qt6"; fail=1; }
  [ "$fail" -eq 0 ] || return 1
  green "===== CHESS LAB OK ====="
}

main() {
  ensure_dirs
  build_cutechess
  build_zstd
  run_as_owner python3 "$SCRIPT_DIR/install-stockfish.py"
  verify
  write_api_env
}

main "$@"
