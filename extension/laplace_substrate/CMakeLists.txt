set(EXT_NAME laplace_substrate)

if(NOT BASH)
    set(BASH bash)
endif()

file(GLOB_RECURSE _ext_sql_sources CONFIGURE_DEPENDS
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/*.sql.in
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/*.h.in
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/*/*.sql.in
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/*/*/*.sql.in
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/inference/*.sql.in
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/generated/*.sql.in
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.upgrade)
list(SORT _ext_sql_sources)
set(_ext_sql_hashes "")
foreach(_f ${_ext_sql_sources})
    file(SHA256 ${_f} _fh)
    string(APPEND _ext_sql_hashes "${_fh}")
endforeach()
string(SHA256 _ext_sql_hash "${_ext_sql_hashes}")
string(SUBSTRING ${_ext_sql_hash} 0 16 EXT_VERSION)

if(WIN32)
    add_library(${EXT_NAME} MODULE
        src/laplace_substrate.c
        src/consensus_fold_step.c
        src/consensus_fold_engine.c
        src/consensus_fold_walks.c
        src/generate_walk.c
        src/trajectory_corpus.c
        src/trajectory_generate.c
        src/variant_synth.c
        src/astar_path.c
        src/apply_batch.c
        src/graph_taxonomy.c
        src/graph_contrast.c
        src/graph_cascade.c
        src/foundry_crawl.c
        src/recall.c
        src/recall_route.c
        src/perfcache.c
        src/content_resolve.c
        src/descent_probe.c
        src/containers_of.c
        src/highway_mask.c
        src/consensus_fold_incremental.c
    )
    set_target_properties(${EXT_NAME} PROPERTIES
        PREFIX "" OUTPUT_NAME ${EXT_NAME} SUFFIX ".dll"
        WINDOWS_EXPORT_ALL_SYMBOLS TRUE)
    target_include_directories(${EXT_NAME} PRIVATE
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32_msvc"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}"
        "${LAPLACE_PG_INCLUDEDIR}"
    )
    target_compile_definitions(${EXT_NAME} PRIVATE
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS WIN32)
    target_link_libraries(${EXT_NAME} PRIVATE
        laplace_core
        laplace_dynamics
        "${LAPLACE_PG_LIBDIR}/postgres.lib"
    )
else()
    set(EXT_C_SOURCES
        src/laplace_substrate.c
        src/consensus_fold_step.c
        src/consensus_fold_engine.c
        src/consensus_fold_walks.c
        src/generate_walk.c
        src/trajectory_corpus.c
        src/trajectory_generate.c
        src/variant_synth.c
        src/astar_path.c
        src/apply_batch.c
        src/graph_taxonomy.c
        src/graph_contrast.c
        src/graph_cascade.c
        src/foundry_crawl.c
        src/recall.c
        src/recall_route.c
        src/perfcache.c
        src/content_resolve.c
        src/containers_of.c
        src/highway_mask.c
        src/consensus_fold_incremental.c
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
endif()

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

include(${CMAKE_CURRENT_SOURCE_DIR}/cmake/SqlManifest.cmake)
laplace_write_sql_chain(
    "${CMAKE_CURRENT_BINARY_DIR}/sql/generated/install_chain.sql.in"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql")
set(_install_deps ${SQL_CHAIN_DEPS})
laplace_write_sql_chain(
    "${CMAKE_CURRENT_BINARY_DIR}/sql/generated/upgrade_chain.sql.in"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.upgrade"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql")
set(_upgrade_deps ${SQL_CHAIN_DEPS})

set(EXT_SQL_MODULES
    sql/laplace_substrate.sql.in
    sql/laplace_substrate_upgrade.sql.in
    sql/manifest.install
    sql/manifest.upgrade
    ${_install_deps}
    ${_upgrade_deps}
)

set(EXT_BUILT_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}--${EXT_VERSION}.sql)
set(EXT_TMP_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.tmp)

add_custom_command(
    OUTPUT  ${EXT_BUILT_SQL}
    DEPENDS ${EXT_SQL_MODULES}
            ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND ${LAPLACE_SQLPP} ${LAPLACE_SQLPP_FLAGS}
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql/generated
            -I${CMAKE_CURRENT_BINARY_DIR}/sql
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/${EXT_NAME}.sql.in
            > ${EXT_TMP_SQL}
    COMMAND ${BASH} -c "grep -v '^#' ${EXT_TMP_SQL} \
            | perl -lpe \"s'MODULE_PATHNAME'\\\$libdir/${EXT_NAME}'g\" \
            > ${EXT_BUILT_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_TMP_SQL}
    COMMENT "Preprocessing ${EXT_NAME} .sql.in → ${EXT_NAME}--${EXT_VERSION}.sql"
    VERBATIM
)

set(EXT_UPGRADE_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}_upgrade.sql)
set(EXT_UPGRADE_TMP ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}_upgrade.tmp)
add_custom_command(
    OUTPUT  ${EXT_UPGRADE_SQL}
    DEPENDS ${EXT_SQL_MODULES}
            ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND ${LAPLACE_SQLPP} ${LAPLACE_SQLPP_FLAGS}
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql/generated
            -I${CMAKE_CURRENT_BINARY_DIR}/sql
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/${EXT_NAME}_upgrade.sql.in
            > ${EXT_UPGRADE_TMP}
    COMMAND ${BASH} -c "grep -v '^#' ${EXT_UPGRADE_TMP} \
            | perl -lpe \"s'MODULE_PATHNAME'\\\$libdir/${EXT_NAME}'g\" \
            > ${EXT_UPGRADE_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_UPGRADE_TMP}
    COMMENT "Preprocessing ${EXT_NAME} upgrade body → ${EXT_NAME}_upgrade.sql"
    VERBATIM
)

add_custom_target(${EXT_NAME}_sql ALL DEPENDS ${EXT_BUILT_SQL} ${EXT_UPGRADE_SQL})
add_dependencies(${EXT_NAME} ${EXT_NAME}_sql)

if(NOT WIN32)
    install(TARGETS ${EXT_NAME}
        LIBRARY
            DESTINATION ${LAPLACE_EXT_LIBDIR}
            PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
    )
    install(FILES
        ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
        ${EXT_BUILT_SQL}
        ${EXT_UPGRADE_SQL}
        DESTINATION ${LAPLACE_EXT_SHAREDIR}
        PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
    )
endif()

if(BUILD_TESTING AND NOT WIN32)
    add_subdirectory(tests)
endif()
