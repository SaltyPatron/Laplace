if(WIN32)
    add_library(laplace_substrate MODULE
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

# Version = a 16-hex content hash of the SQL sources, so it changes IFF the SQL changes. This is what
# makes `ALTER EXTENSION laplace_substrate UPDATE` usable: a SQL edit yields a new version, and the
# deploy applies the (idempotent) upgrade body in place — no reseed. A fixed version would make every
# update a forbidden same-version no-op (the reason SQL changes used to require fresh_db).
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
    COMMAND ${LAPLACE_SQLPP} -traditional-cpp -w -P -Upixel -Ubool
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql/generated
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

# Upgrade body (run by `ALTER EXTENSION ... UPDATE`): same preprocessing as the install script but
# over laplace_substrate_upgrade.sql.in (modules minus one-time data 10/21), all idempotent. Generic
# filename; the deploy copies it to laplace_substrate--<installed>--<EXT_VERSION>.sql for the actual
# installed version (core Postgres has no ANY-from wildcard; a from--to script is named per deploy).
set(EXT_UPGRADE_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}_upgrade.sql)
set(EXT_UPGRADE_TMP ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}_upgrade.tmp)
add_custom_command(
    OUTPUT  ${EXT_UPGRADE_SQL}
    DEPENDS ${EXT_SQL_MODULES}
            ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND ${LAPLACE_SQLPP} -traditional-cpp -w -P -Upixel -Ubool
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql/generated
            -I${CMAKE_CURRENT_BINARY_DIR}/sql
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/${EXT_NAME}_upgrade.sql.in
            > ${EXT_UPGRADE_TMP}
    COMMAND bash -c "grep -v '^#' ${EXT_UPGRADE_TMP} \
            | perl -lpe \"s'MODULE_PATHNAME'\\\$libdir/${EXT_NAME}'g\" \
            > ${EXT_UPGRADE_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_UPGRADE_TMP}
    COMMENT "Preprocessing ${EXT_NAME} upgrade body → ${EXT_NAME}_upgrade.sql"
    VERBATIM
)

add_custom_target(${EXT_NAME}_sql ALL DEPENDS ${EXT_BUILT_SQL} ${EXT_UPGRADE_SQL})
add_dependencies(${EXT_NAME} ${EXT_NAME}_sql)

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

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
