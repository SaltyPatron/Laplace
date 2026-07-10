#!/usr/bin/env bash
# Chess Lab host deps for Linux (hart-server / laplace-runner).
#
# Idempotent. Safe to re-run.
#
#   Root (one-shot package install + build):
#     sudo bash scripts/bootstrap-chess-lab.sh
#       - apt: stockfish + Qt6 build/runtime deps
#       - build cutechess-cli into /opt/laplace/bin
#       - write binary paths into /opt/laplace/app/laplace-api.env
#
#   laplace-runner (CI / pipeline.sh chess-lab):
#     bash scripts/bootstrap-chess-lab.sh
#       - skips apt
#       - rebuilds cutechess-cli if needed
#       - refreshes binary-path block in laplace-api.env
#
# Lichess tokens are NOT handled here. Operator publishes them with:
#   bash scripts/publish-host-secrets.sh
# → /opt/laplace/secrets/lichess.env (systemd EnvironmentFile)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

RUNNER_USER="${RUNNER_USER:-laplace-runner}"
RUNNER_GROUP="${RUNNER_GROUP:-laplace-runner}"
PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
EXTERNAL="${LAPLACE_EXTERNAL:-$PREFIX/external}"
CC_BUILD="${LAPLACE_CUTECHESS_BUILD:-$PREFIX/build-cutechess}"
CC_BIN_DIR="$PREFIX/bin"
APP_DIR="$PREFIX/app"
ENV_FILE="$APP_DIR/laplace-api.env"

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "=== $1 ==="; }

run_as_owner() {
  if [ "$(id -u)" -eq 0 ]; then
    sudo -u "$RUNNER_USER" -H "$@"
  else
    "$@"
  fi
}

install_apt_deps() {
  say "apt: stockfish + Qt6 (cutechess build/runtime)"
  if [ "$(id -u)" -ne 0 ]; then
    yellow "not root — skipping apt (one-shot: sudo bash scripts/bootstrap-chess-lab.sh)"
    return 0
  fi
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    stockfish \
    qt6-base-dev \
    qt6-base-dev-tools \
    libqt6svg6-dev \
    libqt6core5compat6-dev \
    libqt6svg6 \
    libqt6core5compat6 \
    >/dev/null
  green "✓ stockfish + Qt6 packages present"
}

resolve_stockfish() {
  local p
  for p in /usr/games/stockfish /usr/bin/stockfish "$(command -v stockfish 2>/dev/null || true)"; do
    if [ -n "$p" ] && [ -x "$p" ]; then
      echo "$p"
      return 0
    fi
  done
  return 1
}

resolve_qt_bin() {
  local p
  for p in /usr/lib/qt6/bin /usr/lib/x86_64-linux-gnu/qt6/bin; do
    if [ -d "$p" ]; then
      echo "$p"
      return 0
    fi
  done
  for p in /usr/lib/x86_64-linux-gnu /usr/lib; do
    if [ -e "$p/libQt6Core.so.6" ] || ls "$p"/libQt6Core.so* >/dev/null 2>&1; then
      echo "$p"
      return 0
    fi
  done
  return 1
}

resolve_cutechess_src() {
  local p
  for p in "$EXTERNAL/cutechess" "$REPO_ROOT/external/cutechess"; do
    if [ -f "$p/CMakeLists.txt" ]; then
      echo "$p"
      return 0
    fi
  done
  return 1
}

ensure_dirs() {
  say "dirs under $PREFIX"
  if [ "$(id -u)" -eq 0 ]; then
    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" \
      "$PREFIX" "$EXTERNAL" "$CC_BUILD" "$CC_BIN_DIR" "$APP_DIR" "$APP_DIR/logs"
    install -d -m 2770 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$PREFIX/secrets"
  else
    mkdir -p "$CC_BUILD" "$CC_BIN_DIR" "$APP_DIR/logs" "$PREFIX/secrets"
    chmod 2770 "$PREFIX/secrets" 2>/dev/null || true
  fi
  green "✓ $CC_BUILD + $CC_BIN_DIR + $PREFIX/secrets ready"
}

build_cutechess() {
  say "build cutechess-cli → $CC_BIN_DIR/cutechess-cli"
  local src
  if ! src="$(resolve_cutechess_src)"; then
    red "cutechess source missing — expected $EXTERNAL/cutechess or $REPO_ROOT/external/cutechess"
    red "  git submodule update --init external/cutechess"
    return 1
  fi
  if ! resolve_qt_bin >/dev/null; then
    red "Qt6 not found — install packages first (sudo bash scripts/bootstrap-chess-lab.sh)"
    return 1
  fi

  run_as_owner cmake -S "$src" -B "$CC_BUILD" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DWITH_TESTS=OFF
  run_as_owner cmake --build "$CC_BUILD" --target cli

  local built
  built="$(find "$CC_BUILD" -type f -name cutechess-cli -perm -111 2>/dev/null | head -1 || true)"
  if [ -z "$built" ]; then
    red "cutechess-cli binary not produced under $CC_BUILD"
    return 1
  fi
  if [ "$(id -u)" -eq 0 ]; then
    install -m 0755 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$built" "$CC_BIN_DIR/cutechess-cli"
  else
    install -m 0755 "$built" "$CC_BIN_DIR/cutechess-cli"
  fi
  green "✓ $CC_BIN_DIR/cutechess-cli"
}

write_api_env() {
  say "api env chess-lab paths → $ENV_FILE"
  if [ ! -d "$APP_DIR" ]; then
    yellow "$APP_DIR missing — create via deploy/linux/bootstrap-host.sh first"
    return 0
  fi

  local sf qt cc
  sf="$(resolve_stockfish || true)"
  qt="$(resolve_qt_bin || true)"
  cc="$CC_BIN_DIR/cutechess-cli"
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
      yellow "created $ENV_FILE from example"
    else
      red "no $ENV_FILE and no example — skipping env write"
      return 1
    fi
  fi

  local marker_begin="# >>> laplace-runner managed: chess-lab env >>>"
  local marker_end="# <<< laplace-runner managed: chess-lab env <<<"
  # Strip any LICHESS_* that older revisions stuffed into this managed block —
  # tokens live in /opt/laplace/secrets/lichess.env only.
  sed -i -e "/$marker_begin/,/$marker_end/d" "$ENV_FILE"
  {
    echo "$marker_begin"
    [ -n "$cc" ] && echo "LAPLACE_CUTECHESS=$cc"
    [ -n "$sf" ] && echo "LAPLACE_STOCKFISH=$sf"
    [ -n "$qt" ] && echo "LAPLACE_QT_BIN=$qt"
    echo "LAPLACE_CUTECHESS_BUILD=$CC_BUILD"
    echo "LAPLACE_CHESS_LAB_DIR=$PREFIX/chess-lab-work"
    echo "$marker_end"
  } >> "$ENV_FILE"

  if [ "$(id -u)" -eq 0 ]; then
    chown "$RUNNER_USER:$RUNNER_GROUP" "$ENV_FILE"
    chmod 0640 "$ENV_FILE"
    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$PREFIX/chess-lab-work"
  else
    mkdir -p "$PREFIX/chess-lab-work"
  fi

  echo "  CUTECHESS=${cc:-MISSING}"
  echo "  STOCKFISH=${sf:-MISSING}"
  echo "  QT_BIN=${qt:-MISSING}"
  if [ -f "$PREFIX/secrets/lichess.env" ]; then
    green "✓ secrets drop present: $PREFIX/secrets/lichess.env"
  else
    yellow "✓ no $PREFIX/secrets/lichess.env yet — run: bash scripts/publish-host-secrets.sh"
  fi
  green "✓ chess-lab path block written"
}

verify() {
  say "verify"
  local fail=0
  local sf qt
  sf="$(resolve_stockfish || true)"
  qt="$(resolve_qt_bin || true)"
  [ -x "$CC_BIN_DIR/cutechess-cli" ] || { red "✗ cutechess-cli missing"; fail=1; }
  [ -n "$sf" ] || { red "✗ stockfish missing"; fail=1; }
  [ -n "$qt" ] || { red "✗ Qt6 missing"; fail=1; }
  if [ "$fail" -eq 0 ]; then
    green "===== CHESS LAB BOOTSTRAP OK ====="
    echo "  cutechess: $CC_BIN_DIR/cutechess-cli"
    echo "  stockfish: $sf"
    echo "  qt:        $qt"
    echo "  api env:   $ENV_FILE"
    echo "  secrets:   $PREFIX/secrets/lichess.env"
  else
    red "===== CHESS LAB BOOTSTRAP INCOMPLETE ====="
    return 1
  fi
}

main() {
  install_apt_deps
  ensure_dirs
  build_cutechess
  write_api_env
  verify
}

main "$@"
