# extension/laplace_geom/CMakeLists.txt
#
# laplace_geom extension — general-purpose 4D PostGIS additions per
# ADR 0025. CMake-driven build (no PGXS per ADR 0032). Modular SQL via
# cpp preprocessor (per ADR 0034).

set(EXT_NAME    laplace_geom)
set(EXT_VERSION ${PROJECT_VERSION})

# === C wrapper sources (PG_FUNCTION_INFO_V1) ===
set(EXT_C_SOURCES
    src/laplace_geom.c
)

add_library(${EXT_NAME} MODULE ${EXT_C_SOURCES})

# PG extension naming: no `lib` prefix; `.so` suffix even on macOS would be
# wrong (PG uses .so/.dll); for our Linux-only target this is fine.
set_target_properties(${EXT_NAME} PROPERTIES
    PREFIX ""
    OUTPUT_NAME ${EXT_NAME}
)

target_include_directories(${EXT_NAME} PRIVATE
    ${LAPLACE_PG_INCLUDEDIR_SERVER}
)

# === liblwgeom for ST_*_4d geometry deserialization ===
# postgis-3.so hides its internal lwgeom API (-fvisibility=hidden — only
# 9 lwgeom_* symbols exported), and stock apt PostGIS doesn't ship dev
# headers. We statically link against the liblwgeom the PostGIS submodule
# build produced — installed at ${LAPLACE_DEPS_LWGEOM_PREFIX}/{include,lib}/
# by external/CMakeLists.txt's postgis ExternalProject step. Static link
# keeps our wrappers' lwgeom independent of whatever postgis-3.so is
# loaded at runtime (system PG or custom); we only use lwgeom for raw
# gserialized → POINT4D extraction, no cross-function state.
#
# Lookup is independent of LAPLACE_PG_PREFIX (which selects the RUNTIME PG
# the extension will be loaded into — stock or custom). liblwgeom always
# comes from the PostGIS submodule build at /opt/laplace/pgsql-18/, since
# that's where `just build-deps` produces it regardless of which PG the
# substrate is targeting at runtime.
#
# Prerequisites: `just build-deps` must have run. The check below fails
# the configure with a clear pointer if it hasn't.
set(LAPLACE_DEPS_LWGEOM_PREFIX "/opt/laplace/pgsql-18"
    CACHE PATH "Prefix where liblwgeom.{h,a} are installed by external/CMakeLists.txt's postgis step")
find_path(LAPLACE_LIBLWGEOM_INCLUDE_DIR
    NAMES liblwgeom.h
    HINTS "${LAPLACE_DEPS_LWGEOM_PREFIX}/include"
    NO_DEFAULT_PATH
)
find_library(LAPLACE_LIBLWGEOM_STATIC
    NAMES liblwgeom.a
    HINTS "${LAPLACE_DEPS_LWGEOM_PREFIX}/lib"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_LIBLWGEOM_INCLUDE_DIR OR NOT LAPLACE_LIBLWGEOM_STATIC)
    message(FATAL_ERROR
        "laplace_geom requires liblwgeom from the PostGIS submodule build. "
        "Expected: ${LAPLACE_DEPS_LWGEOM_PREFIX}/include/liblwgeom.h + "
        "${LAPLACE_DEPS_LWGEOM_PREFIX}/lib/liblwgeom.a. "
        "Run `just build-deps` first (it produces both as part of the "
        "PostGIS ExternalProject install step in external/CMakeLists.txt). "
        "Override the lookup prefix via -DLAPLACE_DEPS_LWGEOM_PREFIX=<path>.")
endif()
message(STATUS "laplace_geom: liblwgeom.h = ${LAPLACE_LIBLWGEOM_INCLUDE_DIR}/liblwgeom.h")
message(STATUS "laplace_geom: liblwgeom.a = ${LAPLACE_LIBLWGEOM_STATIC}")
target_include_directories(${EXT_NAME} PRIVATE ${LAPLACE_LIBLWGEOM_INCLUDE_DIR})

# liblwgeom.h transitively #includes "proj.h" — must be on the include
# path. PROJ from external/CMakeLists.txt's proj step lands at
# /opt/laplace/proj/include/.
find_path(LAPLACE_PROJ_INCLUDE_DIR
    NAMES proj.h
    HINTS "/opt/laplace/proj/include"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_PROJ_INCLUDE_DIR)
    message(FATAL_ERROR
        "laplace_geom needs proj.h (transitively pulled in by liblwgeom.h). "
        "Expected /opt/laplace/proj/include/proj.h. Run `just build-deps`.")
endif()
target_include_directories(${EXT_NAME} PRIVATE ${LAPLACE_PROJ_INCLUDE_DIR})

# liblwgeom static-links a lot of geometry code that transitively
# references libgeos (lwgeom_geos.o, lwgeom_topo.o, etc.) and libproj
# (lwspheroid.o → geod_*). We link all three so every undefined
# symbol resolves at PG load time.
#
# Critical: this only works when PG itself runs against the consistent
# /opt/laplace dep set. If the running PG already loaded SYSTEM
# postgis-3.so → SYSTEM libgeos 3.10.2, our extension's libgeos
# resolves to the same pinned 3.10.2 at load (despite our RUNPATH
# preferring 3.12.2) and PostGIS 3.6.3 symbols like
# GEOSConcaveHullOfPolygons are missing. The substrate runtime
# therefore must use the /opt/laplace/pgsql-18 cluster — system PG
# is not supported once laplace_geom is loaded into the backend.
find_library(LAPLACE_LIBGEOS_C geos_c
    HINTS "/opt/laplace/geos/lib"
    NO_DEFAULT_PATH)
find_library(LAPLACE_LIBGEOS geos
    HINTS "/opt/laplace/geos/lib"
    NO_DEFAULT_PATH)
find_library(LAPLACE_LIBPROJ proj
    HINTS "/opt/laplace/proj/lib"
    NO_DEFAULT_PATH)
if(NOT LAPLACE_LIBGEOS_C OR NOT LAPLACE_LIBGEOS OR NOT LAPLACE_LIBPROJ)
    message(FATAL_ERROR
        "laplace_geom needs libgeos_c.so + libgeos.so (transitive: "
        "liblwgeom GEOS algorithms) + libproj.so (transitive: liblwgeom "
        "spheroid math). Expected libgeos*.so in /opt/laplace/geos/lib/ "
        "and libproj.so in /opt/laplace/proj/lib/. Run `just build-deps`.")
endif()

target_link_libraries(${EXT_NAME} PRIVATE
    laplace_core
    ${LAPLACE_LIBLWGEOM_STATIC}
    ${LAPLACE_LIBGEOS_C}
    ${LAPLACE_LIBGEOS}
    ${LAPLACE_LIBPROJ}
)
target_link_options(${EXT_NAME} PRIVATE
    "-Wl,-rpath,$<TARGET_FILE_DIR:laplace_core>"
)

# Per RULES.md R6: the extension is THIN wrappers. Heavy lifting is in
# liblaplace_core; the .c file here is just PG_FUNCTION_INFO_V1 wrappers.

# === .control file (built from .control.in via configure_file) ===
configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/${EXT_NAME}.control.in
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    @ONLY
)

# === sqldefines.h shared macros (built via configure_file) ===
configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/sqldefines.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    @ONLY
)

# === Modular SQL via cpp preprocessor (per ADR 0034) ===
#
# Entry: sql/laplace_geom.sql.in (which #includes the numbered modules).
# Build: cpp -traditional-cpp -w -P -Upixel -Ubool  →  grep -v ^#  →
#        perl s/MODULE_PATHNAME/$libdir/laplace_geom/  →  perl s/@extschema@\.//
# Output: build/extension/laplace_geom/laplace_geom--0.1.0.sql

set(EXT_SQL_MODULES
    sql/laplace_geom.sql.in
    sql/01_meta.sql.in
    sql/02_hash128.sql.in
    sql/03_hash128_ops.sql.in
    sql/04_hilbert.sql.in
    sql/05_mantissa.sql.in
    sql/06_st_4d.sql.in
    sql/07_s3_opclass.sql.in
)

set(EXT_BUILT_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}--${EXT_VERSION}.sql)
set(EXT_TMP_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.tmp)

add_custom_command(
    OUTPUT  ${EXT_BUILT_SQL}
    DEPENDS ${EXT_SQL_MODULES}
            ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND ${LAPLACE_SQLPP} -traditional-cpp -w -P -Upixel -Ubool
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_BINARY_DIR}/sql
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/${EXT_NAME}.sql.in
            > ${EXT_TMP_SQL}
    COMMAND bash -c "grep -v '^#' ${EXT_TMP_SQL} \
            | perl -lpe \"s'MODULE_PATHNAME'\\\$libdir/${EXT_NAME}'g\" \
            | perl -lpe \"s'\@extschema\@\\\\.''g\" \
            > ${EXT_BUILT_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_TMP_SQL}
    COMMENT "Preprocessing ${EXT_NAME} .sql.in modules → ${EXT_NAME}--${EXT_VERSION}.sql"
    VERBATIM
)

add_custom_target(${EXT_NAME}_sql ALL DEPENDS ${EXT_BUILT_SQL})
add_dependencies(${EXT_NAME} ${EXT_NAME}_sql)

# === Install ===
install(TARGETS ${EXT_NAME}
    LIBRARY
        DESTINATION ${LAPLACE_EXT_LIBDIR}
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
)
install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    ${EXT_BUILT_SQL}
    DESTINATION ${LAPLACE_EXT_SHAREDIR}
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

# === Tests (pg_regress via CTest — see tests/CMakeLists.txt for the
# fixture + add_test() wiring; landed by B′.9 #196). ===
if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
