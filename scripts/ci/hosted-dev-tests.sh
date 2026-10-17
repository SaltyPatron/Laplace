#!/usr/bin/env bash
# The dev test profile (native ctest without the regress label, every managed test project's Tier!=db/live/perf tests,
# the UCI handshake, the browser suites) on a CI runner. Everything it needs is public and pinned here or in
# scripts/ci/tree-sitter-grammars.tsv: the dependency sources at the build host's revisions, the UCD release the build
# pins, and the oneAPI compiler/MKL/TBB versions hart-server builds with. The perf caches are build products of those
# inputs, so the managed tests run on the exact candidate ROM rather than an installed one.
#   scripts/ci/hosted-dev-tests.sh deps|native|managed|uci|browser|all
# Work tree: $LAPLACE_CI_WORK (default $RUNNER_TEMP/laplace-ci or /tmp/laplace-ci).
set -euo pipefail
# Hermetic on any runner: a self-hosted machine's own declarations (the runner .env's LAPLACE_* roots, PG* defaults,
# /etc/laplace/machine.env and /opt/laplace/secrets, read by LaplaceInstall) never reach the profile. Only this
# script's inputs survive; the machine declaration and install prefix point at paths that do not exist, which is
# what a GitHub-hosted runner presents.
for name in $(compgen -e); do
  case "$name" in
    LAPLACE_CI_WORK|LAPLACE_CI_NATIVE_SOURCE|LAPLACE_EIGEN|LAPLACE_BUILD_GROUP) ;;
    LAPLACE_*|PG*) unset "$name" ;;
  esac
done
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
WORK="${LAPLACE_CI_WORK:-${RUNNER_TEMP:-/tmp}/laplace-ci}"
EXT="$WORK/external"
UCD="$WORK/ucd"
export LAPLACE_EXTERNAL="$EXT" LAPLACE_NATIVE="$EXT/laplace-native" LAPLACE_UCD_ROOT="$UCD"
export LAPLACE_BUILD_ROOT="$WORK/managed" LAPLACE_WORK_ROOT="$WORK/work"
# The public corpora the dev tier reads, under one data root laid out as the estate is: the UCD release the build pins
# and the lichess openings revision the estate acquires (LichessOpenings/lichess-openings-<rev>.tar.gz),
# the CILI checkout and the ISO 639-3 code tables, each at the revision hart-server's /vault/Data holds.
DATA="$WORK/data"
export LAPLACE_DATA="$DATA" LAPLACE_DATA_ROOT="$DATA"
# Tests keep their scratch under the declared permanent work root (scripts/lib/storage.sh), never /tmp.
export TMPDIR="$WORK/tmp" TMP="$WORK/tmp" TEMP="$WORK/tmp"
# A hosted runner has no laplace-runner group; the work roots belong to the runner's own group (scripts/lib/storage.sh).
export LAPLACE_BUILD_GROUP="${LAPLACE_BUILD_GROUP:-$(id -gn)}"
BUILD="$ROOT/build/engine"   # the layout scripts/sync-managed-native-artifacts.sh and the managed props read
export LAPLACE_ENGINE_BUILD="$BUILD"
# An empty machine declaration: the readers (scripts/machine-env.py, LaplaceInstall) take an
# explicitly named file as the whole declaration, so this host's own declaration is never read.
export LAPLACE_MACHINE_ENV="$WORK/machine.env" LAPLACE_INSTALL_PREFIX="$WORK/no-install"
mkdir -p "$EXT" "$UCD" "$LAPLACE_WORK_ROOT" "$TMPDIR" "$DATA/UCD/Public/UCD" "$DATA/Games/Chess"
: > "$LAPLACE_MACHINE_ENV"

say() { echo "::group::$*"; }
end() { echo "::endgroup::"; }

fetch() {   # fetch <dir> <repository> <revision>
  local dir="$1" url="$2" rev="$3"
  if [[ -d "$dir/.git" && "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" == "$rev" ]]; then return 0; fi
  rm -rf "$dir"; mkdir -p "$dir"
  git -C "$dir" init -q
  git -C "$dir" fetch -q --depth 1 "$url" "$rev"
  git -C "$dir" checkout -q FETCH_HEAD
}

deps() {
  say "dependency sources (build host revisions)"
  fetch "$EXT/blake3"       https://github.com/BLAKE3-team/BLAKE3.git        8b829b697fa4cfe35de35e9aa8c20b56266cb091 &  # 1.8.3
  fetch "$EXT/tree-sitter"  https://github.com/tree-sitter/tree-sitter.git   dcdc8cc55e5dfedfc858080835f153999a29ec40 &
  fetch "$EXT/spectra"      https://github.com/yixuan/spectra.git            6841bcbacaa0f0a8446210314e682057a084be4e &  # v1.2.0
  fetch "$EXT/fathom"       https://github.com/jdart1/Fathom.git             c9c6fef0dddc05d2e242c183acf5833149ab676d &
  if [[ -n "${LAPLACE_CI_NATIVE_SOURCE:-}" ]]; then ln -sfnT "$LAPLACE_CI_NATIVE_SOURCE" "$EXT/laplace-native"
  else fetch "$EXT/laplace-native" https://github.com/SaltyPatron/Laplace-Native.git "$(git ls-remote https://github.com/SaltyPatron/Laplace-Native.git refs/heads/main | cut -f1)" & fi
  ln -sfnT "${LAPLACE_EIGEN:-/usr/include/eigen3}" "$EXT/eigen"
  local name rev url
  mkdir -p "$EXT/tree-sitter-grammars"
  while IFS=$'\t' read -r name rev url; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    fetch "$EXT/tree-sitter-grammars/$name" "$url" "$rev" &
  done < "$ROOT/scripts/ci/tree-sitter-grammars.tsv"
  wait
  end
  say "UCD $(ucd_version)"
  ucd_fetch
  ln -sfnT "$UCD" "$DATA/UCD/Public/UCD/latest"
  end
  say "lichess openings (the estate's acquired revision)"
  fetch "$DATA/Games/Chess/lichess-openings" https://github.com/lichess-org/chess-openings.git 4b8622759e7ae6f93f011cc6c83a3823401ab45e
  end
  say "CILI (the estate's globalwordnet/cili revision)"
  fetch "$DATA/CILI" https://github.com/globalwordnet/cili.git a895d7ecb18019dda3443f98901e59d81ce8722b
  end
  say "ISO 639-3 code tables (the estate's 2026-04-15 release)"
  iso639_fetch
  end
}

iso639_fetch() {
  local zip="$WORK/iso-639-3_Code_Tables_20260415.zip"
  local sum=106a3f3b073e94bcc767f663e05f386358b1285ba2b063f24a2eeb6bafcc982a
  if ! echo "$sum  $zip" | sha256sum -c --status 2>/dev/null; then
    curl -fsSL --retry 3 -o "$zip" \
      https://iso639-3.sil.org/sites/iso639-3/files/downloads/iso-639-3_Code_Tables_20260415.zip
    echo "$sum  $zip" | sha256sum -c --status
  fi
  rm -rf "$DATA/ISO639"; mkdir -p "$DATA/ISO639"
  unzip -q -j -o "$zip" '*.tab' -d "$DATA/ISO639"
  test -s "$DATA/ISO639/iso-639-3.tab"
}

ucd_version() { sed -n 's/^set(LAPLACE_UNICODE_VERSION "\([^"]*\)".*/\1/p' "$ROOT/engine/CMakeLists.txt" | head -1; }

ucd_fetch() {
  local v base; v="$(ucd_version)"; base="https://www.unicode.org/Public/$v"
  if ! grep -Fqs "for Version $v of the Unicode Standard." "$UCD/ReadMe.txt"; then
    rm -rf "$UCD"; mkdir -p "$UCD/ucd" "$UCD/ucdxml" "$UCD/uca"
    curl -fsSL --retry 3 -o "$UCD/ReadMe.txt" "$base/ReadMe.txt"
    curl -fsSL --retry 3 -o "$WORK/UCD.zip" "$base/ucd/UCD.zip"
    unzip -q -o "$WORK/UCD.zip" -d "$UCD/ucd"
    curl -fsSL --retry 3 -o "$UCD/ucdxml/ucd.all.grouped.zip" "$base/ucdxml/ucd.all.grouped.zip"
    curl -fsSL --retry 3 -o "$UCD/uca/allkeys.txt" "$base/uca/allkeys.txt"
  fi
  grep -Fq "for Version $v of the Unicode Standard." "$UCD/ReadMe.txt"
}

oneapi() {
  [[ -n "${SETVARS_COMPLETED:-}" ]] && return 0
  set +u; . /opt/intel/oneapi/setvars.sh >/dev/null 2>&1 || true; set -u
}

native() {
  oneapi
  say "native: configure and build (oneAPI, MKL required, tests on)"
  cmake -S "$ROOT/engine" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$ROOT/cmake/toolchains/intel-oneapi.cmake" -DLAPLACE_REQUIRE_MKL=ON -DBUILD_TESTING=ON \
    -DLAPLACE_EXTERNAL="$EXT" -DLAPLACE_NATIVE="$LAPLACE_NATIVE" -DLAPLACE_UCD_PATH="$UCD" \
    -DLAPLACE_CHESS_OPENINGS="$DATA/Games/Chess/lichess-openings"
  cmake --build "$BUILD" -j "$(nproc)"
  find "$BUILD" -name 'laplace_t0_perfcache.bin' -print -quit | grep -q . || { echo "::error::tier-0 perf-cache not produced" >&2; exit 1; }
  end
  say "native: ctest (dev profile: everything but the regress label)"
  ctest --test-dir "$BUILD" --no-tests=error --output-on-failure -j "$(nproc)" -LE regress
  end
}

managed() {
  oneapi
  say "managed: build"
  dotnet build app/Laplace.slnx -c Release --nologo -v minimal
  end
  say "managed: dev tier (Tier!=db&Tier!=live&Tier!=perf) on the candidate native build"
  export LAPLACE_PERFCACHE_BIN; LAPLACE_PERFCACHE_BIN="$(find "$BUILD" -name 'laplace_t0_perfcache.bin' -print -quit)"
  bash scripts/sync-managed-native-artifacts.sh
  rm -rf "$WORK/managed-results"
  dotnet test app/Laplace.slnx -c Release --no-build --nologo -v minimal \
    --filter 'Tier!=db&Tier!=live&Tier!=perf' --logger 'trx' --results-directory "$WORK/managed-results"
  python3 "$ROOT/scripts/ci/trx-summary.py" "$WORK/managed-results"
  end
}

uci() {
  oneapi
  say "UCI handshake"
  local runtime; runtime="$(mktemp -d)"
  dotnet publish app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj -c Release --no-build --no-self-contained -o "$runtime" --nologo
  local out; out="$(printf 'uci\nisready\nquit\n' | timeout 30 "$runtime/laplace-uci")"
  grep -q '^uciok$' <<<"$out" && grep -q '^readyok$' <<<"$out"
  end
}

browser() {
  say "browser suites (typecheck, read-resource, workspace-ui, data-ui, chess-ui)"
  bash scripts/test-suites/browser-dev.sh
  end
}

case "${1:-all}" in
  deps) deps ;; native) native ;; managed) managed ;; uci) uci ;; browser) browser ;;
  all) deps; native; managed; uci; browser ;;
  *) echo "usage: $0 deps|native|managed|uci|browser|all" >&2; exit 2 ;;
esac
