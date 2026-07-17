set(EXT_NAME laplace_substrate)

include(${CMAKE_CURRENT_SOURCE_DIR}/cmake/SqlManifest.cmake)

# EXT_VERSION and the completeness gate are derived from the MANIFESTS, not a
# filesystem glob. Two consequences, both deliberate:
#   1. The version is a function of exactly what ships (the manifest-listed
#      modules + shims + sqldefines) — a stray/orphaned .sql.in that ships
#      nothing can no longer perturb the version string (the old glob footgun).
#   2. laplace_check_manifest_complete FAILS configuration if any *.sql.in under
#      sql/ is neither manifest-referenced nor allow-listed — which makes the
#      legacy numbered-monolith / orphaned-module pattern impossible to
#      reintroduce (a file that isn't wired in breaks the build instead of
#      rotting unnoticed, as 43_table_tuning and finish_consensus_fold_steps did).
laplace_manifest_files(_install_modules
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install" "${CMAKE_CURRENT_SOURCE_DIR}/sql")
laplace_manifest_files(_upgrade_modules
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.upgrade" "${CMAKE_CURRENT_SOURCE_DIR}/sql")

set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.upgrade")

laplace_check_manifest_complete("${CMAKE_CURRENT_SOURCE_DIR}/sql"
    ${_install_modules} ${_upgrade_modules})

set(_ext_version_inputs
    ${_install_modules}
    ${_upgrade_modules}
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/laplace_substrate.sql.in"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/laplace_substrate_upgrade.sql.in"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/sqldefines.h.in"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.upgrade")
list(REMOVE_DUPLICATES _ext_version_inputs)
list(SORT _ext_version_inputs)
set(_ext_sql_hashes "")
foreach(_f ${_ext_version_inputs})
    file(SHA256 ${_f} _fh)
    string(APPEND _ext_sql_hashes "${_fh}")
endforeach()
string(SHA256 _ext_sql_hash "${_ext_sql_hashes}")
string(SUBSTRING ${_ext_sql_hash} 0 16 EXT_VERSION)

# One source list for every platform. MODULE libraries link with undefined
# symbols allowed (PG extensions resolve against the postgres binary at dlopen),
# so a file missing from one platform's list only surfaces as a runtime
# `undefined symbol` when the extension is first loaded — never at build time.
set(EXT_C_SOURCES
    src/laplace_substrate.c
    src/consensus_fold_step.c
    src/generate_walk.c
    src/trajectory_corpus.c
    src/trajectory_generate.c
    src/variant_synth.c
    src/astar_path.c
    src/graph_taxonomy.c
    src/graph_contrast.c
    src/graph_cascade.c
    src/foundry_crawl.c
    src/explore_web.c
    src/recall.c
    src/recall_route.c
    src/perfcache.c
    src/content_resolve.c
    src/descent_probe.c
    src/containers_of.c
    src/geometry_successors.c
    src/highway_mask.c
    src/model_factor.c
    src/lexical_case.c
    src/steered_walk.c
    src/realize_batch.c
)

if(WIN32)
    add_library(${EXT_NAME} MODULE ${EXT_C_SOURCES})
    set_target_properties(${EXT_NAME} PROPERTIES
        PREFIX "" OUTPUT_NAME ${EXT_NAME} SUFFIX ".dll"
        WINDOWS_EXPORT_ALL_SYMBOLS TRUE)
    # SYSTEM: the PG server headers trip clang pointer-sign/compat warnings
    # (generic-msvc.h) that are PostgreSQL's to fix, not ours — keep our build clean.
    target_include_directories(${EXT_NAME} SYSTEM PRIVATE
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
    add_library(${EXT_NAME} MODULE ${EXT_C_SOURCES})
    set_target_properties(${EXT_NAME} PROPERTIES
        PREFIX ""
        OUTPUT_NAME ${EXT_NAME}
    )
    target_include_directories(${EXT_NAME} SYSTEM PRIVATE
        ${LAPLACE_PG_INCLUDEDIR_SERVER}
    )
    # In-tree deps resolve via CMake's automatic build-tree RPATH; never add
    # -Wl,-rpath build dirs here — literal linker rpaths survive `cmake
    # --install` untouched, so the installed .so keeps resolving
    # liblaplace_dynamics out of the CI workspace build tree (a stub no-MKL
    # rebuild there crash-looped the production postmaster, 2026-07-16).
    target_link_libraries(${EXT_NAME} PRIVATE laplace_core laplace_dynamics)
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
