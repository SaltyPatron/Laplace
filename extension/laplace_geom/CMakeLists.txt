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

# Link against laplace_core engine .so (the extension calls C ABI
# functions from liblaplace_core for hash/coord/hilbert math). rpath
# points at the engine's install location so the extension can find
# the engine .so at PG load time.
target_link_libraries(${EXT_NAME} PRIVATE laplace_core)
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
    LIBRARY DESTINATION ${LAPLACE_EXT_LIBDIR}
)
install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    ${EXT_BUILT_SQL}
    DESTINATION ${LAPLACE_EXT_SHAREDIR}
)

# === Tests (pg_regress via CTest — real wiring lands when first
# functions ship in Chunk 1+) ===
if(BUILD_TESTING)
    # Placeholder — pg_regress integration via add_test() lands with the
    # first real SQL function (Chunk 1 Story 1.x). For now: the build
    # succeeds, the .so + .control + .sql are produced, and CTest is
    # ready for test cases under tests/sql/ + tests/expected/.
endif()
