cmake_minimum_required(VERSION 3.20)
project(laplace
    VERSION 0.1.0
    DESCRIPTION "Laplace — content-addressable geometric-attestation substrate"
    LANGUAGES C CXX
)

enable_testing()
include(CTest)

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

if(LAPLACE_INSTALL_STAGED)
    set(LAPLACE_EXT_LIBDIR
        "${CMAKE_INSTALL_PREFIX}/lib/postgresql/${LAPLACE_PG_MAJOR}")
    set(LAPLACE_EXT_SHAREDIR
        "${CMAKE_INSTALL_PREFIX}/share/postgresql/${LAPLACE_PG_MAJOR}/extension")
else()
    set(LAPLACE_EXT_LIBDIR   "${LAPLACE_PG_PKGLIBDIR}")
    set(LAPLACE_EXT_SHAREDIR "${LAPLACE_PG_SHAREDIR}/extension")
endif()

set(CMAKE_INSTALL_RPATH "${CMAKE_INSTALL_PREFIX}/lib")
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

find_program(LAPLACE_SQLPP NAMES cpp gpp_
    DOC "C preprocessor used to expand extension .sql.in modules into the built .sql artifact")
if(NOT LAPLACE_SQLPP)
    message(FATAL_ERROR
        "cpp (or gpp_) not found — required for .sql.in preprocessing. "
        "Install via 'apt install build-essential' (already in bootstrap_build_environment).")
endif()
message(STATUS "Laplace SQLPP: ${LAPLACE_SQLPP}")
set(LAPLACE_SQLPP_FLAGS -traditional-cpp -w -P -Upixel -Ubool)

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

set(CMAKE_INSTALL_DEFAULT_DIRECTORY_PERMISSIONS ${LAPLACE_INSTALL_DIR_PERMS})

install(CODE "
  message(STATUS \"Laplace pre-install cleanup under \${CMAKE_INSTALL_PREFIX}\")
  execute_process(
    COMMAND sh -c \"
      set -u
      paths='\${CMAKE_INSTALL_PREFIX}/include/laplace \${CMAKE_INSTALL_PREFIX}/share/laplace'
      for p in \$paths; do
        [ -e \\\"\$p\\\" ] || continue
        find \\\"\$p\\\" -type d -exec chmod 2775 {} + 2>/dev/null || true
        find \\\"\$p\\\" -type f -exec chmod 0664 {} + 2>/dev/null || true
      done
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

add_subdirectory(engine)

add_subdirectory(extension)

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
