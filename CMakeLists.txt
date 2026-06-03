cmake_minimum_required(VERSION 3.20)
project(laplace
    VERSION 0.1.0
    DESCRIPTION "Laplace — content-addressable geometric-attestation substrate"
    LANGUAGES C CXX
)

# Top-level orchestrator per ADR 0032 (unified CMake build pipeline,
# Path B — PGXS retired). Drives engine + extension builds from one
# CMake tree, reads dep sources from external/ submodules per ADR 0033.

# Top-level CTest enables `ctest` from build/ to discover all engine
# tests (per-module GoogleTest registrations via gtest_discover_tests).
enable_testing()
include(CTest)

# === PG install prefix ===
#
# Defaults to stock PG 18 at /usr/lib/postgresql/18. Override to the
# custom build when Epic B's scripts/build-pg.sh produces /opt/laplace/
# pgsql-18: cmake -B build -DLAPLACE_PG_PREFIX=/opt/laplace/pgsql-18
set(LAPLACE_PG_PREFIX "/usr/lib/postgresql/18"
    CACHE PATH "Prefix where pg_config lives (stock /usr/lib/postgresql/18 or custom /opt/laplace/pgsql-18)")

find_program(LAPLACE_PG_CONFIG pg_config
    PATHS "${LAPLACE_PG_PREFIX}/bin"
    NO_DEFAULT_PATH)
if(NOT LAPLACE_PG_CONFIG)
    message(FATAL_ERROR
        "pg_config not found at ${LAPLACE_PG_PREFIX}/bin. "
        "Set -DLAPLACE_PG_PREFIX=<path to the PG install prefix>.")
endif()

execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --includedir-server
                OUTPUT_VARIABLE LAPLACE_PG_INCLUDEDIR_SERVER
                OUTPUT_STRIP_TRAILING_WHITESPACE)
execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --pkglibdir
                OUTPUT_VARIABLE LAPLACE_PG_PKGLIBDIR
                OUTPUT_STRIP_TRAILING_WHITESPACE)
execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --sharedir
                OUTPUT_VARIABLE LAPLACE_PG_SHAREDIR
                OUTPUT_STRIP_TRAILING_WHITESPACE)
execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --version
                OUTPUT_VARIABLE LAPLACE_PG_VERSION
                OUTPUT_STRIP_TRAILING_WHITESPACE)

message(STATUS "Laplace PG: ${LAPLACE_PG_VERSION}")
message(STATUS "  pg_config:        ${LAPLACE_PG_CONFIG}")
message(STATUS "  server includes:  ${LAPLACE_PG_INCLUDEDIR_SERVER}")
message(STATUS "  pkglibdir:        ${LAPLACE_PG_PKGLIBDIR}")
message(STATUS "  sharedir:         ${LAPLACE_PG_SHAREDIR}")

# === Extension install destination ===
#
# By default (LAPLACE_INSTALL_STAGED=OFF), PG extensions install to the
# absolute pkglibdir/sharedir that pg_config reports — i.e. the system PG
# tree (/usr/lib/postgresql/$PG_MAJOR/lib and /usr/share/postgresql/$PG_MAJOR
# for a stock Debian/Ubuntu PG). That tree is root-owned, so installing
# requires sudo. This is the right default for CI and for installing into
# a custom /opt/laplace/pgsql-$PG_MAJOR built by `just build-deps`.
#
# When LAPLACE_INSTALL_STAGED=ON, extensions install to the PARALLEL
# layout under CMAKE_INSTALL_PREFIX — e.g. /opt/laplace/lib/postgresql/$PG_MAJOR
# and /opt/laplace/share/postgresql/$PG_MAJOR/extension. /opt/laplace is
# laplace-runner-owned (per bootstrap_build_environment), so the install is
# sudo-free. The running PG is told to look there via the
# extension_control_path + dynamic_library_path GUCs set by
# bootstrap_pg_extension_paths (one-time, in sudo just bootstrap).
#
# Derive PG major version from pg_config --version output, e.g.
# "PostgreSQL 18.3" → "18".
string(REGEX MATCH "PostgreSQL +([0-9]+)" _pg_major_match "${LAPLACE_PG_VERSION}")
set(LAPLACE_PG_MAJOR "${CMAKE_MATCH_1}")
if(NOT LAPLACE_PG_MAJOR)
    message(FATAL_ERROR
        "Could not parse PG major version from '${LAPLACE_PG_VERSION}'. "
        "Expected output of `pg_config --version` to look like 'PostgreSQL 18.X'.")
endif()

option(LAPLACE_INSTALL_STAGED
    "Install PG extensions to CMAKE_INSTALL_PREFIX/{lib,share}/postgresql/PG_MAJOR (sudo-free) instead of pg_config's absolute system paths"
    OFF)

# Derived from LAPLACE_INSTALL_STAGED — NOT cached. With CACHE PATH the
# previous configure's value sticks (subsequent set() is a no-op for
# already-cached entries), so toggling STAGED in a persistent build dir
# (e.g. CI with clean: false) would still see the stale install paths.
# These are derived knobs; the user-facing toggle is LAPLACE_INSTALL_STAGED.
if(LAPLACE_INSTALL_STAGED)
    set(LAPLACE_EXT_LIBDIR
        "${CMAKE_INSTALL_PREFIX}/lib/postgresql/${LAPLACE_PG_MAJOR}")
    set(LAPLACE_EXT_SHAREDIR
        "${CMAKE_INSTALL_PREFIX}/share/postgresql/${LAPLACE_PG_MAJOR}/extension")
else()
    set(LAPLACE_EXT_LIBDIR   "${LAPLACE_PG_PKGLIBDIR}")
    set(LAPLACE_EXT_SHAREDIR "${LAPLACE_PG_SHAREDIR}/extension")
endif()

# === Install-time RPATH ===
# CMake strips build-tree RPATH at install by default, leaving installed
# .so files unable to find their non-system deps. The engine's dynamics
# library links MKL + TBB + Intel compiler runtime (libsvml, libimf,
# libintlc). laplace_substrate.so links engine libs at /opt/laplace/lib
# (transitively pulls MKL/TBB through dynamics). Without this, PG can't
# load the extension at runtime — "libmkl_intel_lp64.so.3: cannot open
# shared object file" — unless the PG service has LD_LIBRARY_PATH set
# to include these paths, which it doesn't and shouldn't need to.
#
# Paths are derived from the oneAPI env vars set by /opt/intel/oneapi/setvars.sh
# (MKLROOT, TBBROOT, CMPLR_ROOT) so this is portable across machines —
# any oneAPI install layout (different versions, alternative prefix)
# resolves correctly as long as setvars.sh was sourced before configure.
# If setvars.sh wasn't sourced, the build wouldn't have found icpx / MKL /
# TBB in the first place, so a missing env var here means the build is
# already broken at configure — we fail loud rather than embed a guess.
#
# Build-tree RPATH (CMAKE_BUILD_RPATH) is preserved separately and lets
# ctest in build/ work without install. CMAKE_BUILD_WITH_INSTALL_RPATH
# defaults FALSE, so install rewrites RPATH from build-tree values to these.
set(CMAKE_INSTALL_RPATH "${CMAKE_INSTALL_PREFIX}/lib")
# /opt/laplace/geos/lib so laplace_geom finds libgeos_c at PG load time.
# liblwgeom.a is static-linked into laplace_geom; its lwgeom_geos_* code
# paths reference the GEOS C API at runtime. We don't want to depend on
# postgis-3.so being loaded first (RTLD_NOW resolves DT_NEEDED before
# any other extension's deps are visible).
list(APPEND CMAKE_INSTALL_RPATH "/opt/laplace/geos/lib")
list(APPEND CMAKE_INSTALL_RPATH "/opt/laplace/proj/lib")
foreach(_oneapi_var_pair IN ITEMS
        "MKLROOT;/lib"
        "TBBROOT;/lib/intel64/gcc4.8"
        "CMPLR_ROOT;/lib")
    list(GET _oneapi_var_pair 0 _var)
    list(GET _oneapi_var_pair 1 _suffix)
    if(NOT DEFINED ENV{${_var}})
        message(FATAL_ERROR
            "$ENV{${_var}} is unset. Source /opt/intel/oneapi/setvars.sh "
            "before configuring — oneAPI env vars drive both the build "
            "(icpx/MKL/TBB discovery) and the install RPATH.")
    endif()
    list(APPEND CMAKE_INSTALL_RPATH "$ENV{${_var}}${_suffix}")
endforeach()
unset(_oneapi_var_pair)
unset(_var)
unset(_suffix)

message(STATUS "Laplace extension install:")
message(STATUS "  staged:           ${LAPLACE_INSTALL_STAGED}")
message(STATUS "  install prefix:   ${CMAKE_INSTALL_PREFIX}")
message(STATUS "  ext libdir:       ${LAPLACE_EXT_LIBDIR}")
message(STATUS "  ext sharedir:     ${LAPLACE_EXT_SHAREDIR}")

# === SQL preprocessor (per ADR 0034 — PostGIS-pattern .sql.in → .sql) ===
find_program(LAPLACE_SQLPP NAMES cpp gpp_
    DOC "C preprocessor used to expand extension .sql.in modules into the built .sql artifact")
if(NOT LAPLACE_SQLPP)
    message(FATAL_ERROR
        "cpp (or gpp_) not found — required for .sql.in preprocessing per ADR 0034. "
        "Install via 'apt install build-essential' (already in bootstrap_build_environment).")
endif()
message(STATUS "Laplace SQLPP: ${LAPLACE_SQLPP}")

# === Install permissions discipline (repo-wide, ownership-agnostic) ===
#
# THE INVARIANT: every Laplace install destination — directory and file —
# is group-writable + setgid'd to laplace-runner. /opt/laplace dirs are 2775
# with no sticky bit, so unlink() needs only parent-dir-write (which the
# group has) — file owner is irrelevant. With this invariant established,
# `cmake --install build` works for any laplace-runner-group user (ahart
# locally, laplace-runner in CI) without sudo, without `sudo -u laplace-runner`,
# without NOPASSWD entries, without recovery scripts.
#
# WHY THE INVARIANT IS NECESSARY:
#   1. cmake `file(INSTALL DIRECTORY ...)` always calls chmod() on the dest
#      directory, unconditionally — even when the dir's mode already matches
#      the desired mode. POSIX chmod is owner-only. Cross-user chmod = EPERM.
#      → Pre-wipe the dir so the installer creates it fresh + owns it.
#   2. To pre-wipe a dir requires unlinking its contents, which needs
#      parent-dir write. If the parent dir is g-w (mode 755), nobody in
#      the group except the owner can unlink. → All dirs must be g+w.
#   3. install(DIRECTORY ...) by default copies SOURCE permissions (the repo
#      dir's mode 755). That re-creates the g-w bad state on every install
#      unless overridden. → Every install(DIRECTORY) needs DIRECTORY_PERMISSIONS.
#   4. Implicit dirs cmake creates for install(TARGETS) / install(FILES) follow
#      CMAKE_INSTALL_DEFAULT_DIRECTORY_PERMISSIONS. → Set it globally.
#
# These three perm vars are referenced by every install() call in engine/
# and extension/ subdir CMakeLists; do NOT inline literal permission lists
# in those files (drift hazard — one updated, others forgotten).
set(LAPLACE_INSTALL_DIR_PERMS
    OWNER_READ OWNER_WRITE OWNER_EXECUTE
    GROUP_READ GROUP_WRITE GROUP_EXECUTE
    WORLD_READ WORLD_EXECUTE
    SETGID
    CACHE INTERNAL "Mode 2775+setgid for every install-destination directory")
set(LAPLACE_INSTALL_FILE_PERMS
    OWNER_READ OWNER_WRITE
    GROUP_READ GROUP_WRITE
    WORLD_READ
    CACHE INTERNAL "Mode 0664 for every installed regular file (headers, .control, .sql)")
set(LAPLACE_INSTALL_EXEC_PERMS
    OWNER_READ OWNER_WRITE OWNER_EXECUTE
    GROUP_READ GROUP_WRITE GROUP_EXECUTE
    WORLD_READ WORLD_EXECUTE
    CACHE INTERNAL "Mode 0775 for every installed shared lib / module / executable")

# Sets the mode for dirs cmake creates IMPLICITLY (e.g. parent dirs of
# install(TARGETS LIBRARY DESTINATION lib) when those don't yet exist).
# install(DIRECTORY) still needs explicit DIRECTORY_PERMISSIONS — that one
# defaults to USE_SOURCE_PERMISSIONS, not this var.
set(CMAKE_INSTALL_DEFAULT_DIRECTORY_PERMISSIONS ${LAPLACE_INSTALL_DIR_PERMS})

# === Pre-install ownership-tolerant cleanup ===
#
# Runs FIRST in build/cmake_install.cmake because it's declared before any
# add_subdirectory call. Two phases:
#   1. chmod-recursive every Laplace install destination the installer owns
#      to mode 2775 (dirs) / 0664-0775 (files). chmod is owner-only, so this
#      is a no-op for files we don't own; that's expected — once the
#      structural perm invariant holds, the dirs are already 2775 and the
#      next install's pre-wipe succeeds without needing this phase.
#   2. rm -rf the Laplace install destinations. With phase-1's chmod plus
#      the structural invariant, every dir under them is g+w so any group
#      member can unlink any file regardless of owner.
#
# Output is captured (OUTPUT_QUIET + ERROR_QUIET) but the rc is checked. If
# rm fails it indicates a cross-user g-w dir from a pre-fix install — one-
# time recovery: `sudo rm -rf /opt/laplace/include/laplace`. Forward state
# under the structural fix cannot reach that condition.
install(CODE "
  message(STATUS \"Laplace pre-install cleanup under \${CMAKE_INSTALL_PREFIX}\")
  execute_process(
    COMMAND sh -c \"
      set -u
      paths='\${CMAKE_INSTALL_PREFIX}/include/laplace \${CMAKE_INSTALL_PREFIX}/share/laplace'
      for p in \$paths; do
        [ -e \\\"\$p\\\" ] || continue
        # Phase 1: chmod what we own (no-op for others); makes own-dirs g+w
        # so phase 2 can traverse + unlink inside them.
        find \\\"\$p\\\" -type d -exec chmod 2775 {} + 2>/dev/null || true
        find \\\"\$p\\\" -type f -exec chmod 0664 {} + 2>/dev/null || true
      done
      # Phase 2: nuke from orbit. -f swallows ENOENT; permission failures
      # surface as non-zero rc which set -e propagates. EVERY destination this
      # project installs files into must be wiped here: cmake file(INSTALL)
      # always chmod()s after copy and chmod is owner-only, so overwriting a
      # file the OTHER identity owns is EPERM unless the file was removed
      # first (that exact gap — share/laplace missing from this list — is what
      # kept resurrecting the manual-sudo wall on the perfcache).
      set -e
      rm -rf \$paths
      rm -f \${CMAKE_INSTALL_PREFIX}/lib/liblaplace_*.so* 2>/dev/null || true
      rm -f \${CMAKE_INSTALL_PREFIX}/lib/postgresql/${LAPLACE_PG_MAJOR}/laplace_*.so 2>/dev/null || true
      rm -f \${CMAKE_INSTALL_PREFIX}/share/postgresql/${LAPLACE_PG_MAJOR}/extension/laplace_* 2>/dev/null || true
    \"
    RESULT_VARIABLE _laplace_wipe_rc
  )
  if(NOT _laplace_wipe_rc EQUAL 0)
    message(FATAL_ERROR
      \"Laplace pre-install cleanup failed under \${CMAKE_INSTALL_PREFIX}.\"
      \"\\nLikely a pre-fix cross-user g-w directory from a previous install.\"
      \"\\nOne-time recovery (run once, never again under the structural fix):\"
      \"\\n    sudo rm -rf \${CMAKE_INSTALL_PREFIX}/include/laplace \${CMAKE_INSTALL_PREFIX}/share/laplace\"
      \"\\nThen re-run cmake --install. The post-install invariant (end of the\"
      \"\\nroot CMakeLists) prevents forward installs from recreating the state.\")
  endif()
")

# === Engine (3 shared libs per ADR 0024) ===
add_subdirectory(engine)

# === PG extensions (2 per ADR 0025) ===
add_subdirectory(extension)

# === Post-install permission-invariant enforcement (declared LAST → runs last) ===
#
# CMAKE_INSTALL_DEFAULT_DIRECTORY_PERMISSIONS does NOT survive the installer's
# umask for implicitly-created directories: the requested 2775 lands as 2755
# under the default 022 umask (empirical — see /opt/laplace/include/laplace
# created drwxr-sr-x on 2026-06-03 despite the var). A g-w dir under the shared
# prefix IS the cross-user wall: the other identity (CI laplace-runner vs the
# developer) can neither chmod it (chmod is owner-only) nor pre-wipe through it
# (unlink needs parent-dir write) — the state that demanded a manual
# `sudo rm -rf` before every cross-identity install.
#
# So enforce the invariant HERE, at creation time, while this installer still
# owns everything this install just created: normalize every dir under the
# laplace destinations to 2775 (a no-op on dirs another identity owns — those
# are caught by the verify), then VERIFY the whole tree and FAIL THE INSTALL
# if any group-unwritable dir remains. Drift dies in the run that creates it,
# for the identity that can still fix it — never as the NEXT identity's wall.
# Scope: include/ lib/ share/ ONLY — never the dep trees (geos/tree-sitter/…,
# their own lifecycle) and never pgsql-18/ (live cluster; PG refuses a
# group-writable data directory by design).
install(CODE "
  message(STATUS \"Laplace post-install permission invariant under \${CMAKE_INSTALL_PREFIX}\")
  execute_process(
    COMMAND sh -c \"
      set -u
      cd \\\"\${CMAKE_INSTALL_PREFIX}\\\" || exit 1
      for p in include lib share; do
        [ -e \\\"\$p\\\" ] || continue
        find \\\"\$p\\\" -type d -exec chmod 2775 {} + 2>/dev/null || true
      done
      bad=\$(find include lib share -type d ! -perm -020 2>/dev/null || true)
      if [ -n \\\"\$bad\\\" ]; then
        echo 'laplace post-install invariant violated (group-unwritable dirs):' >&2
        echo \\\"\$bad\\\" >&2
        exit 1
      fi
    \"
    RESULT_VARIABLE _laplace_inv_rc
  )
  if(NOT _laplace_inv_rc EQUAL 0)
    message(FATAL_ERROR
      \"Laplace post-install invariant FAILED: group-unwritable dir(s) under the \"
      \"shared prefix (paths on stderr above). They were created or left by an \"
      \"identity other than this installer. Fix once — chmod -R 2775 (as owner) or \"
      \"remove the listed dirs — and re-install; the invariant then self-maintains.\")
  endif()
")
