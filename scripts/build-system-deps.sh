#!/usr/bin/env bash
# Build system deps (proj/geos/gdal/postgresql/postgis/tree-sitter) into /opt/laplace.
#
# Idempotent: fingerprints /opt/laplace/external pins + ISA. If the
# fingerprint matches and install artifacts exist, this is a no-op. Rebuilds only
# when sources/pins actually change (or LAPLACE_FORCE_DEPS=1).
#
# Used by: scripts/setup-host.sh Layer 0.5, CI deps job, `just build-deps`.
#
# Env:
#   LAPLACE_EXTERNAL       default /build/external
#   LAPLACE_DEPS_BUILD     default /opt/laplace/build/deps
#   LAPLACE_DEPS_PREFIX    default /opt/laplace
#   LAPLACE_TARGET_ISA     default AVX2
#   LAPLACE_FORCE_DEPS=1   ignore stamp; rebuild
#   LAPLACE_DEPS_USER      default laplace-runner (build as this user when root)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXT="${LAPLACE_EXTERNAL:-/build/external}"
BUILD="${LAPLACE_DEPS_BUILD:-/opt/laplace/build/deps}"
PREFIX="${LAPLACE_DEPS_PREFIX:-/opt/laplace}"
ISA="${LAPLACE_TARGET_ISA:-AVX2}"
RUN_AS="${LAPLACE_DEPS_USER:-laplace-runner}"

# PIN the generator. It was unset, so cmake picked whatever the ambient
# environment yielded — Ninja under CI, Unix Makefiles from a bare shell — and
# the same tree got configured two different ways depending on who ran it.
# ExternalProject sub-builds inherit the parent generator but keep their own
# caches, so the flip surfaces as "Does not match the generator used previously".
# Ninja to match pipeline.sh's engine configure (-G Ninja) and the sub-caches
# already on disk; Unix Makefiles only where ninja is genuinely absent.
if [ -n "${LAPLACE_DEPS_GENERATOR:-}" ]; then
  DEPS_GENERATOR="$LAPLACE_DEPS_GENERATOR"
elif command -v ninja >/dev/null 2>&1; then
  DEPS_GENERATOR="Ninja"
else
  DEPS_GENERATOR="Unix Makefiles"
fi
STAMP_DIR="$BUILD"
STAMP_FILE="$STAMP_DIR/.laplace-deps.fingerprint"
FORCE="${LAPLACE_FORCE_DEPS:-0}"

DEPS=(proj geos gdal postgresql postgis tree-sitter)

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }

deps_fingerprint() {
  local d rev
  {
    echo "isa=$ISA"
    echo "prefix=$PREFIX"
    echo "external=$EXT"
    # HASH THE SUPERBUILD, don't just note its presence.
    #
    # external/CMakeLists.txt carries the postgres configure line, including
    # --with-lz4 / --with-zstd / --with-liburing. If the stamp only records
    # present/absent, changing a configure FLAG does not move the fingerprint:
    # the pins are unchanged, the stamp matches, the build skips, and a binary
    # compiled without those flags survives every CI run forever. MEASURED
    # 2026-08-10: wal_compression pinned to pglz and io_method to worker on a
    # postgres predating the flags, with a 649s checkpoint writing 17.6 GB
    # underneath a 600s ingest.
    #
    # 0ab74ee2 replaced the hash with present/absent because the file was
    # UNTRACKED after 391d9be7 (+ /external/ in .gitignore), which made the stamp
    # environment-dependent: hashed locally, "MISSING" in CI. That reasoning was
    # right and the remedy was wrong -- #971 restored the file to tracking, so it
    # is now present identically in every checkout and hashing it is
    # deterministic again. Restore the input rather than keep the workaround.
    if [ -f "$ROOT/external/CMakeLists.txt" ]; then
      # Same sha256sum-or-cksum fallback the outer digest uses. Calling
      # sha256sum unconditionally aborts under `set -euo pipefail` on hosts
      # without it, before the fallback at the end of this function can run.
      if command -v sha256sum >/dev/null 2>&1; then
        echo "cmake=$(sha256sum "$ROOT/external/CMakeLists.txt" | awk '{print $1}')"
      else
        echo "cmake=$(cksum "$ROOT/external/CMakeLists.txt" | awk '{print $1"-"$2}')"
      fi
    else
      # MISSING, matching the per-dep marker below and the cmake=MISSING the
      # comments in this file already refer to.
      echo "cmake=MISSING"
    fi
    for d in "${DEPS[@]}"; do
      if [ -d "$EXT/$d/.git" ] || [ -f "$EXT/$d/.git" ]; then
        # -c safe.directory=* : submodule checkouts under /opt or root-owned trees
        # otherwise fail rev-parse with "dubious ownership" and stamp as UNKNOWN,
        # which defeats the skip logic every run (GH #423).
        rev="$(git -c safe.directory='*' -C "$EXT/$d" rev-parse HEAD 2>/dev/null || echo UNKNOWN)"
        echo "$d=$rev"
      elif [ -d "$EXT/$d" ]; then
        echo "$d=NOGIT"
      else
        echo "$d=MISSING"
      fi
    done
  } | if command -v sha256sum >/dev/null 2>&1; then
        sha256sum | awk '{print $1}'
      else
        cksum | awk '{print $1"-"$2}'
      fi
}

installs_present() {
  [ -x "$PREFIX/pgsql-18/bin/postgres" ] || return 1
  [ -e "$PREFIX/proj/lib" ] || [ -e "$PREFIX/proj/lib64" ] || return 1
  [ -e "$PREFIX/geos/lib" ] || [ -e "$PREFIX/geos/lib64" ] || return 1
  [ -e "$PREFIX/gdal/lib" ] || [ -e "$PREFIX/gdal/lib64" ] || return 1
  # CI's "Verify installed dep artifacts" (laplace.yml deps job) also requires
  # these two — a stamp-skip here with either missing fails four steps later.
  [ -e "$PREFIX/pgsql-18/lib/postgis-3.so" ] || return 1
  [ -e "$PREFIX/tree-sitter/lib/libtree-sitter.a" ] || return 1
  return 0
}

run_as_builder() {
  local cmd="$*"
  if [ "$(id -u)" -eq 0 ] && id -u "$RUN_AS" >/dev/null 2>&1; then
    # laplace-runner is nologin. `script -c` runs via $SHELL/pw_shell — without
    # SHELL=/bin/bash that prints "This account is currently not available."
    # PTY still wanted so ExternalProject/make stream under sudo -u.
    if command -v script >/dev/null 2>&1; then
      sudo -u "$RUN_AS" -H env SHELL=/bin/bash PATH="$PATH" \
        script -qefc "$cmd" /dev/null
    else
      sudo -u "$RUN_AS" -H PATH="$PATH" stdbuf -oL -eL /bin/bash -c "$cmd"
    fi
  else
    /bin/bash -c "$cmd"
  fi
}

# ONE build identity per tree. The root path above builds as $RUN_AS; the non-root path
# builds as whoever invoked it. Both write /opt/laplace/build/deps, which is setgid 2775
# group $RUN_AS — so a developer in that group writes there successfully and leaves files
# owned by THEMSELVES. Nothing complains, because the group model makes ownership look
# irrelevant. It is not: cmake's configure_file copies permissions onto its destination,
# so the next $RUN_AS build chmod()s a file it does not own and gets EPERM, "Operation
# not permitted". That is a mine armed by one run and detonated by another, weeks later,
# on the first reconfigure (2026-07-26, six ExternalProject_Add failures against a tree
# seeded Jul-17).
#
# The group bit is not the fix and neither is chown-on-every-run — it would fight the
# group model and re-break on the next developer build. Refuse the second identity
# instead: if you are not root and not $RUN_AS, use the sanctioned root entry point.
assert_single_build_identity() {
  [ "$(id -u)" -eq 0 ] && return 0
  [ "$(id -un)" = "$RUN_AS" ] && return 0
  id -u "$RUN_AS" >/dev/null 2>&1 || return 0   # no such user: single-identity by default

  red "refusing to build deps as '$(id -un)' — this tree builds as '$RUN_AS'"
  red "  $BUILD is setgid group $RUN_AS, so your write WOULD succeed and leave files"
  red "  owned by you. The next '$RUN_AS' build then fails in configure_file with"
  red "  'Operation not permitted' and the cause is ten days upstream of the symptom."
  red "  Use the sanctioned entry point:  sudo bash scripts/setup-host.sh"
  red "  Deliberate override (you accept the mixed tree): LAPLACE_DEPS_USER=$(id -un)"
  exit 1
}

purge_autoconf_build_trees() {
  # Deleting ExternalProject step stamps re-ENTERS configure/build/install. For the
  # cmake deps that is enough. For the autoconf ones it is not: PostgreSQL's
  # configure short-circuits against an existing config.status, and make sees no
  # changed prerequisites, so a "forced rebuild" relinks stale objects and installs
  # them. The result looks fresh by mtime and carries none of the new configure
  # options.
  #
  # MEASURED 2026-08-10 after a full LAPLACE_FORCE_DEPS=1 run reporting
  # "[42/42] Completed 'postgis'" and "system deps built + stamped":
  #     postgresql-build/src/include/pg_config.h        2026-08-03 22:43
  #     postgresql-build/src/backend/.../xlog.o         2026-07-26 15:34
  #     postgresql-build/src/bin/pg_config/pg_config    2026-07-26 15:34  (0 --with-lz4)
  #     postgresql-build/src/backend/postgres           2026-08-10 11:18  (relink only)
  #     /opt/laplace/pgsql-18/bin/postgres              2026-08-10 11:18
  # Six seconds from configure to install cannot compile PostgreSQL. Nothing was
  # rebuilt, and --with-lz4/--with-zstd/--with-liburing have never reached the
  # shipped binary despite being in external/CMakeLists.txt since restoration.
  # Consequence: wal_compression enumvals {pglz,on,off}, io_method {sync,worker},
  # and a checkpointer writing 17.6 GB in 649 s under a ~600 s ingest.
  #
  # Force must mean force. Remove the autoconf build trees so configure runs from
  # nothing. The cmake deps keep their object trees — their stamp invalidation
  # already re-runs cmake, which does react to changed arguments.
  local d
  for d in postgresql-build postgis-build; do
    if [ -d "$BUILD/$d" ]; then
      yellow "  purging autoconf build tree: $BUILD/$d (config.status would short-circuit configure)"
      rm -rf "${BUILD:?}/$d"
    fi
  done
  # postgis builds in-source under $EXT; clear its configure cache the same way.
  # shellcheck disable=SC2043  # single-element by design: the in-source list grows here
  for d in postgis; do
    [ -f "$EXT/$d/config.status" ] || continue
    yellow "  clearing in-source autoconf cache: $EXT/$d"
    rm -f "$EXT/$d/config.status" "$EXT/$d/config.cache" 2>/dev/null || true
  done
}

invalidate_ep_stamps() {
  # Drop ExternalProject step stamps so a pin change re-enters configure/build/install.
  # Keep object trees where possible — cmake/make still incremental inside BINARY_DIR.
  find "$BUILD" -type d -name '*-stamp' 2>/dev/null | while read -r d; do
    rm -rf "$d"
  done
}

# /opt/laplace/build/deps is shared across checkouts (setup-host under ~/Projects,
# CI under actions-runner/_work/...). CMake refuses -S when CMAKE_HOME_DIRECTORY
# in the cache differs — scrub top-level cache only; EP binary dirs stay.
scrub_cmake_cache_if_source_moved() {
  local cache="$BUILD/CMakeCache.txt"
  local want src
  [ -f "$cache" ] || return 0
  want="$(cd "$ROOT/external" && pwd -P)"
  src="$(grep -E '^CMAKE_HOME_DIRECTORY:' "$cache" | head -n1 | cut -d= -f2- || true)"
  [ -n "$src" ] || return 0
  if [ -d "$src" ]; then
    src="$(cd "$src" && pwd -P)"
  fi
  local gen reason=""
  gen="$(grep -E '^CMAKE_GENERATOR:' "$cache" | head -n1 | cut -d= -f2- || true)"

  [ "$src" != "$want" ] && reason="source moved: cache=$src want=$want"
  if [ -n "$gen" ] && [ "$gen" != "$DEPS_GENERATOR" ]; then
    [ -n "$reason" ] && reason="$reason; "
    reason="${reason}generator changed: cache=$gen want=$DEPS_GENERATOR"
  fi
  [ -n "$reason" ] || return 0

  # Scrub the SUB-BUILDS too, not just the top level. ExternalProject_Add binary
  # dirs (proj-build, geos-build, gdal-build) each carry their own CMakeCache and
  # inherit the parent's generator at build time. Dropping only $BUILD/CMakeCache
  # leaves them pinned to the OLD generator, and the next build dies with
  #   "generator : Unix Makefiles / Does not match the generator used previously: Ninja"
  # which reads like a cmake bug and is really a half-finished scrub. Observed
  # 2026-07-26: a developer configure from a personal checkout rewrote the parent
  # cache (different source path AND, with no -G pinned, a different generator);
  # the next CI run scrubbed the parent, kept Ninja sub-caches, and geos/proj
  # failed while tree-sitter and postgresql — which use no nested cmake — sailed
  # through, making it look source-specific rather than tree-wide.
  yellow "cmake $reason — scrubbing $BUILD caches (parent + ExternalProject sub-builds)"
  rm -f "$cache"
  rm -rf "$BUILD/CMakeFiles"
  local sub
  while IFS= read -r sub; do
    [ -n "$sub" ] || continue
    yellow "  scrubbing sub-build cache: ${sub%/CMakeCache.txt}"
    rm -f "$sub"
    rm -rf "${sub%/CMakeCache.txt}/CMakeFiles"
  done < <(find "$BUILD" -mindepth 2 -name CMakeCache.txt 2>/dev/null)
}

# --- main ---
if [ ! -d "$EXT" ]; then
  red "missing $EXT — run sync-external / setup-host prefix first"
  exit 1
fi
if [ ! -f "$ROOT/external/CMakeLists.txt" ]; then
  # The superbuild is HOW deps are built from source, not WHETHER they are
  # installed. 391d9be7 deleted it (224 lines) with .gitmodules when the deps
  # moved to the shared cache, and this guard has failed every build since --
  # including on hosts where all six artifacts are present and correct.
  #
  # installs_present() above already answers the real question, and
  # deps_fingerprint() already records cmake=MISSING for this case, so the
  # stamp logic needs nothing new. Building on a bare host is the bootstrap's
  # job now (scripts/bootstrap-laplace-runner.sh), not a per-build step.
  if installs_present; then
    green "✓ system deps installed under $PREFIX; no superbuild present — nothing to build"
    exit 0
  fi
  red "missing $ROOT/external/CMakeLists.txt, and deps are not installed under $PREFIX"
  red "  provision once: sudo scripts/bootstrap-laplace-runner.sh bootstrap"
  exit 1
fi
if ! grep -q 'USES_TERMINAL_BUILD' "$ROOT/external/CMakeLists.txt"; then
  red "$ROOT/external/CMakeLists.txt missing USES_TERMINAL_BUILD"
  exit 1
fi

assert_single_build_identity

if [ "$(id -u)" -eq 0 ]; then
  install -d -m 2775 -o "$RUN_AS" -g "$RUN_AS" "$(dirname "$BUILD")" "$BUILD" 2>/dev/null \
    || mkdir -p "$BUILD"
  # install -d only owns dirs it CREATES. Everything a developer left here under their
  # own uid stays theirs, and the build below drops root to $RUN_AS (see run_as). A
  # process can then write those files through the setgid group bit but does not OWN
  # them — and cmake's configure_file copies permissions onto its destination, so it
  # chmod()s a file owned by someone else and gets EPERM: "Operation not permitted".
  # The setgid group-writable tree hides this until the fingerprint changes and forces
  # the first reconfigure in weeks, which is when it detonates (2026-07-26: deps tree
  # from a Jul-17 `ahart` run, six ExternalProject_Add configure_file failures).
  # One-time REPAIR of trees already poisoned before assert_single_build_identity
  # existed. Prevention is that guard; this only clears the historical damage, because
  # without it the very next $RUN_AS build still EPERMs on the old files. Idempotent,
  # and it keeps the human out of `sudo rm -rf /opt/laplace/build`.
  if id -u "$RUN_AS" >/dev/null 2>&1 && [ -d "$BUILD" ]; then
    foreign="$(find "$BUILD" ! -user "$RUN_AS" -print -quit 2>/dev/null || true)"
    if [ -n "$foreign" ]; then
      yellow "  build tree has entries not owned by $RUN_AS — normalising (configure_file would EPERM)"
      find "$BUILD" ! -user "$RUN_AS" -exec chown -h "$RUN_AS:$RUN_AS" {} + 2>/dev/null || true
      still="$(find "$BUILD" ! -user "$RUN_AS" -print -quit 2>/dev/null || true)"
      if [ -n "$still" ]; then
        red "  could not normalise ownership under $BUILD (first offender: $still)"
        exit 1
      fi
      green "✓ $BUILD ownership normalised to $RUN_AS"
    fi
  fi
else
  mkdir -p "$BUILD"
fi

fp="$(deps_fingerprint)"
echo "deps fingerprint: $fp"

write_stamp() {
  printf '%s\n' "$fp" >"$STAMP_FILE"
  if [ "$(id -u)" -eq 0 ] && id -u "$RUN_AS" >/dev/null 2>&1; then
    chown "$RUN_AS:$RUN_AS" "$STAMP_FILE" 2>/dev/null || true
  fi
}

if [ "$FORCE" != "1" ] && installs_present; then
  if [ -f "$STAMP_FILE" ] && [ "$(cat "$STAMP_FILE")" = "$fp" ]; then
    green "✓ system deps current (fingerprint match) — skip build"
    echo "  stamp: $STAMP_FILE"
    echo "  force: LAPLACE_FORCE_DEPS=1 $0"
    exit 0
  fi
  if [ ! -f "$STAMP_FILE" ]; then
    # First run after stamp introduction: trust existing /opt/laplace installs.
    yellow "no stamp yet; installs present — adopting fingerprint (skip build)"
    yellow "  if installs are stale vs pins: LAPLACE_FORCE_DEPS=1 $0"
    write_stamp
    green "✓ stamped $STAMP_FILE"
    exit 0
  fi
fi

if [ "$FORCE" = "1" ]; then
  yellow "LAPLACE_FORCE_DEPS=1 — rebuilding"
  invalidate_ep_stamps
  purge_autoconf_build_trees
elif [ -f "$STAMP_FILE" ] && [ "$(cat "$STAMP_FILE" 2>/dev/null)" != "$fp" ]; then
  # Same purge as the FORCE path, and for the same reason. The fingerprint hashes
  # external/CMakeLists.txt, so a changed CONFIGURE_COMMAND lands here — and
  # invalidating stamps alone would re-enter configure, hit an existing
  # config.status, relink stale objects and install them. A detected change that
  # produces the old binary is worse than no detection: it reports success.
  #
  # With this, no operator has to know to set LAPLACE_FORCE_DEPS=1. Editing the
  # configure line is the signal; the rebuild follows from it.
  yellow "deps sources/pins/configure changed — invalidating stamps and autoconf trees"
  invalidate_ep_stamps
  purge_autoconf_build_trees
elif ! installs_present; then
  yellow "install artifacts missing under $PREFIX — building"
fi

scrub_cmake_cache_if_source_moved

echo "==== cmake configure $BUILD (LAPLACE_EXTERNAL=$EXT) ===="
# Fail loud: do not swallow cmake configure/build errors (set -e already; keep explicit).
if ! run_as_builder "cmake -B '$BUILD' -S '$ROOT/external' -G '$DEPS_GENERATOR' -ULAPLACE_EXTERNAL -DLAPLACE_EXTERNAL='$EXT' -DLAPLACE_DEPS_PREFIX='$PREFIX'"; then
  red "cmake configure failed for $BUILD (source=$ROOT/external)"
  exit 1
fi

echo "==== cmake --build $BUILD -j ===="
if ! run_as_builder "cmake --build '$BUILD' -j"; then
  red "cmake --build failed for $BUILD"
  exit 1
fi

if ! installs_present; then
  red "build finished but install artifacts still missing under $PREFIX"
  exit 1
fi

write_stamp
green "✓ system deps built + stamped ($STAMP_FILE)"

