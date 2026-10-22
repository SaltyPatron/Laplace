#!/usr/bin/env bash
# The dev test profile (native ctest without the regress label, every managed project's canonical dev-category tests,
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
    LAPLACE_CI_WORK|LAPLACE_CI_NATIVE_SOURCE|LAPLACE_CI_ENGINE_SOURCE|LAPLACE_CI_OPERATIONS_SOURCE|LAPLACE_CI_ICU_DIR|LAPLACE_CI_ISO639_ARCHIVE|LAPLACE_CI_UCD_FLAT_ARCHIVE|LAPLACE_EIGEN|LAPLACE_BUILD_GROUP) ;;
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
export LAPLACE_DATA="$DATA" LAPLACE_MODELS="$WORK/models"
# Tests keep their scratch under the declared permanent work root (scripts/lib/storage.sh), never /tmp.
export TMPDIR="$WORK/tmp" TMP="$WORK/tmp" TEMP="$WORK/tmp"
# A hosted runner has no laplace-runner group; the work roots belong to the runner's own group (scripts/lib/storage.sh).
export LAPLACE_BUILD_GROUP="${LAPLACE_BUILD_GROUP:-$(id -gn)}"
BUILD="$ROOT/build/engine"   # the layout scripts/sync-managed-native-artifacts.sh and the managed props read
export LAPLACE_ENGINE_BUILD="$BUILD"
export LAPLACE_BUILD="$WORK/build"
export LAPLACE_RECIPE_ENGINE_SOURCE="$LAPLACE_CI_ENGINE_SOURCE"
export LAPLACE_RECIPES="$LAPLACE_RECIPE_ENGINE_SOURCE/recipes"
export LAPLACE_COOKBOOK_PATH="$ROOT/recipes"
export LAPLACE_OPERATIONS="$LAPLACE_CI_OPERATIONS_SOURCE"
export LAPLACE_ICU_DIR="$LAPLACE_CI_ICU_DIR"
export LAPLACE_TREESITTER="$EXT/tree-sitter"
export LAPLACE_GRAMMARS="$WORK/grammars"
export LAPLACE_TIER0="$BUILD/core/perfcache/tier0.bin"
export LAPLACE_FLAGS="$BUILD/core/perfcache/tier0.flags"
export LAPLACE_HIGHWAY="$BUILD/core/perfcache/highway.bin"
export LAPLACE_NATIVE_TEST_LIBRARY="$BUILD/native/liblaplace.so"
# An empty machine declaration: the readers (scripts/machine-env.py, LaplaceInstall) take an
# explicitly named file as the whole declaration, so this host's own declaration is never read.
export LAPLACE_MACHINE_ENV="$WORK/machine.env" LAPLACE_INSTALL_PREFIX="$WORK/no-install"
mkdir -p "$EXT" "$UCD" "$LAPLACE_WORK_ROOT" "$TMPDIR" "$DATA/UCD/Public/UCD" "$DATA/Games/Chess" "$LAPLACE_MODELS"
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
    if [[ -n "${LAPLACE_CI_ISO639_ARCHIVE:-}" && -f "$LAPLACE_CI_ISO639_ARCHIVE" ]]; then
      echo "$sum  $LAPLACE_CI_ISO639_ARCHIVE" | sha256sum -c --status
      cp "$LAPLACE_CI_ISO639_ARCHIVE" "$zip"
    else
      curl -fsSL --retry 3 -o "$zip" \
        https://iso639-3.sil.org/sites/iso639-3/files/downloads/iso-639-3_Code_Tables_20260415.zip
    fi
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
  # The Native flag generator reads flat XML; the tier-0 generator reads the
  # grouped archive. Both are required inputs of the same Unicode 17 build.
  [[ "$v" == 17.0.0 ]] || { echo "Update the flat XML input pin for Unicode $v" >&2; return 1; }
  local flat="$UCD/ucdxml/ucd.all.flat.zip"
  local sum=2776dfabc354323c81e5092afb17bc717e6c9f6661d083d1564c05ae837b6d2d
  mkdir -p "$UCD/ucdxml"
  if ! echo "$sum  $flat" | sha256sum -c --status 2>/dev/null; then
    if [[ -n "${LAPLACE_CI_UCD_FLAT_ARCHIVE:-}" && -f "$LAPLACE_CI_UCD_FLAT_ARCHIVE" ]]; then
      echo "$sum  $LAPLACE_CI_UCD_FLAT_ARCHIVE" | sha256sum -c --status
      cp "$LAPLACE_CI_UCD_FLAT_ARCHIVE" "$flat"
    else
      curl -fsSL --retry 3 -o "$flat" "$base/ucdxml/ucd.all.flat.zip"
    fi
    echo "$sum  $flat" | sha256sum -c --status
  fi
  if [[ ! -s "$UCD/ucdxml/ucd.all.flat.xml" || "$flat" -nt "$UCD/ucdxml/ucd.all.flat.xml" ]]; then
    unzip -q -o "$flat" -d "$UCD/ucdxml"
  fi
}

oneapi() {
  [[ -n "${SETVARS_COMPLETED:-}" ]] && return 0
  set +u; . /opt/intel/oneapi/setvars.sh >/dev/null 2>&1 || true; set -u
}

native() {
  oneapi
  say "compiled recipe grammars"
  bash "$LAPLACE_RECIPE_ENGINE_SOURCE/tools/build_grammars.sh" "$EXT/tree-sitter-grammars" "$LAPLACE_GRAMMARS"
  bash "$LAPLACE_RECIPE_ENGINE_SOURCE/tools/build_grammars.sh" "$LAPLACE_RECIPE_ENGINE_SOURCE/recipes/formats/grammars" "$LAPLACE_GRAMMARS"
  end
  say "native: configure and build (oneAPI, MKL required, tests on)"
  cmake -U 'ICU_*' -S "$ROOT/engine" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$ROOT/cmake/toolchains/intel-oneapi.cmake" -DLAPLACE_REQUIRE_MKL=ON -DBUILD_TESTING=ON \
    -DLAPLACE_EXTERNAL="$EXT" -DLAPLACE_NATIVE="$LAPLACE_NATIVE" -DLAPLACE_UCD_PATH="$UCD" \
    -DLAPLACE_OPERATIONS="$LAPLACE_OPERATIONS" -DLAPLACE_RECIPE_ENGINE_SOURCE="$LAPLACE_RECIPE_ENGINE_SOURCE" \
    -DLAPLACE_TREESITTER="$LAPLACE_TREESITTER" -DLAPLACE_TIER0="$LAPLACE_TIER0" -DICU_ROOT="$LAPLACE_ICU_DIR" \
    -DLAPLACE_CHESS_OPENINGS="$DATA/Games/Chess/lichess-openings"
  cmake --build "$BUILD" -j "$(nproc)"
  cmake --build "$BUILD" --target laplace laplace_highway_perfcache -j "$(nproc)"
  python3 scripts/ingest_catalog.py --check-engine "$BUILD/core/recipe_engine/laplace"
  test -s "$LAPLACE_TIER0" || { echo "::error::tier-0 perf-cache not produced" >&2; exit 1; }
  "$BUILD/core/recipe_engine/laplace" flags -u "$UCD" -o "$LAPLACE_FLAGS"
  end
  say "native: ctest (dev profile: everything but the regress label)"
  ctest --test-dir "$BUILD" --no-tests=error --output-on-failure -j "$(nproc)" -LE regress
  end
}

managed() {
  oneapi
  # The managed catalog's workspace root does not relocate Native dependencies.
  export LAPLACE_SRC="$(dirname "$ROOT")"
  say "managed: build"
  dotnet build app/Laplace.slnx -c Release --nologo -v minimal
  end
  say "managed: canonical dev tier on the candidate native build"
  export LAPLACE_PERFCACHE_BIN="$LAPLACE_TIER0"
  export LAPLACE_CANDIDATE_ENGINE_BUILD="$BUILD"
  test -s "$LAPLACE_HIGHWAY" && test -s "$LAPLACE_NATIVE_TEST_LIBRARY"
  rm -rf "$WORK/managed-results"
  bash scripts/test-suites/managed-dev.sh --logger trx --results-directory "$WORK/managed-results"
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
