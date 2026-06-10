if(WIN32)
    add_library(laplace_substrate MODULE
        src/laplace_substrate.c
        src/generate_walk.c
        src/generation_native.c
        src/astar_path.c
    )
    set_target_properties(laplace_substrate PROPERTIES
        PREFIX "" OUTPUT_NAME laplace_substrate SUFFIX ".dll"
        WINDOWS_EXPORT_ALL_SYMBOLS TRUE)
    target_include_directories(laplace_substrate PRIVATE
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32_msvc"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}"
        "${LAPLACE_PG_INCLUDEDIR}"
    )
    target_compile_definitions(laplace_substrate PRIVATE
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS WIN32)
    target_link_libraries(laplace_substrate PRIVATE
        laplace_core
        laplace_dynamics
        "${LAPLACE_PG_LIBDIR}/postgres.lib"
    )
    return()
endif()

set(EXT_NAME    laplace_substrate)
set(EXT_VERSION ${PROJECT_VERSION})

set(EXT_C_SOURCES
    src/laplace_substrate.c
    src/generate_walk.c
    src/generation_native.c
    src/astar_path.c
)

add_library(${EXT_NAME} MODULE ${EXT_C_SOURCES})

set_target_properties(${EXT_NAME} PROPERTIES
    PREFIX ""
    OUTPUT_NAME ${EXT_NAME}
)

target_include_directories(${EXT_NAME} PRIVATE
    ${LAPLACE_PG_INCLUDEDIR_SERVER}
)

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
    sql/10_bootstrap.sql.in
    sql/11_entities_exist_bitmap.sql.in
    sql/12_consensus_schema.sql.in
    sql/13_mu_law.sql.in
    sql/14_period_fold.sql.in
    sql/15_readback.sql.in
    sql/16_inspect.sql.in
    sql/17_consensus_reads.sql.in
    sql/18_ops_surface.sql.in
    sql/20_converse.sql.in
    sql/21_seed.sql.in
    sql/22_cascade_surface.sql.in
    sql/23_structural_surface.sql.in
    sql/24_identity_health.sql.in
    sql/25_intent_preflight.sql.in
    sql/26_generation.sql.in
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
            > ${EXT_BUILT_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_TMP_SQL}
    COMMENT "Preprocessing ${EXT_NAME} .sql.in modules → ${EXT_NAME}--${EXT_VERSION}.sql"
    VERBATIM
)

add_custom_target(${EXT_NAME}_sql ALL DEPENDS ${EXT_BUILT_SQL})
add_dependencies(${EXT_NAME} ${EXT_NAME}_sql)

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

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
