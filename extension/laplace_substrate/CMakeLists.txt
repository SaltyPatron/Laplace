# extension/laplace_substrate/CMakeLists.txt
#
# laplace_substrate extension — substrate domain per ADR 0025.
# CMake-driven build (no PGXS per ADR 0032). Modular SQL via cpp
# preprocessor (per ADR 0034). Same pattern as laplace_geom.

set(EXT_NAME    laplace_substrate)
set(EXT_VERSION ${PROJECT_VERSION})

set(EXT_C_SOURCES
    src/laplace_substrate.c
)

add_library(${EXT_NAME} MODULE ${EXT_C_SOURCES})

set_target_properties(${EXT_NAME} PROPERTIES
    PREFIX ""
    OUTPUT_NAME ${EXT_NAME}
)

target_include_directories(${EXT_NAME} PRIVATE
    ${LAPLACE_PG_INCLUDEDIR_SERVER}
)

# laplace_substrate links liblaplace_core (Glicko-2, A*, cascade) and
# liblaplace_dynamics (Procrustes, eigenmaps, sparsity) — the full
# engine stack. rpath points at the engine lib install location.
target_link_libraries(${EXT_NAME} PRIVATE laplace_core laplace_dynamics)
target_link_options(${EXT_NAME} PRIVATE
    "-Wl,-rpath,$<TARGET_FILE_DIR:laplace_core>"
    "-Wl,-rpath,$<TARGET_FILE_DIR:laplace_dynamics>"
)

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/${EXT_NAME}.control.in
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    @ONLY
)

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/sqldefines.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    @ONLY
)

set(EXT_SQL_MODULES
    sql/laplace_substrate.sql.in
    sql/01_schema.sql.in
    sql/02_entities.sql.in
    sql/03_physicalities.sql.in
    sql/04_attestations.sql.in
    sql/05_indexes.sql.in
    sql/06_glicko2.sql.in
    sql/07_cascade.sql.in
    sql/08_sp_trajectory_ops.sql.in
    sql/09_brin_tier_ops.sql.in
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

install(TARGETS ${EXT_NAME}
    LIBRARY DESTINATION ${LAPLACE_EXT_LIBDIR}
)
install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    ${EXT_BUILT_SQL}
    DESTINATION ${LAPLACE_EXT_SHAREDIR}
)
