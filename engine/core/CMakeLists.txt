set(LAPLACE_CORE_HEADERS
    include/laplace/core/version.h
    include/laplace/core/math4d.h
    include/laplace/core/hash128.h
    include/laplace/core/hilbert4d.h
    include/laplace/core/mantissa.h
    include/laplace/core/super_fibonacci.h
    include/laplace/core/glicko2.h
    include/laplace/core/score.h
    include/laplace/core/codepoint_table.h
    include/laplace/core/astar.h
    include/laplace/core/trajectory.h
    include/laplace/core/tier_tree.h
    include/laplace/core/intent_stage.h
    include/laplace/core/merkle_dedup.h
    include/laplace/core/hash_composer.h
    include/laplace/core/grapheme_floor.h
    include/laplace/core/grammar_registry.h
    include/laplace/core/grammar_decomposer.h
    include/laplace/core/grammar_compose.h
    include/laplace/core/grammar_tags.h
    include/laplace/core/grapheme_break.h
    include/laplace/core/word_break.h
    include/laplace/core/sentence_break.h
    include/laplace/core/normalize_nfc.h
    include/laplace/core/text_decomposer.h
    include/laplace/core/content_witness_batch.h
    include/laplace/core/unicode_seed.h
    include/laplace/core/relation_law.h
    include/laplace/core/pos_law.h
    include/laplace/core/highway_manifest.h
    include/laplace/core/highway_table.h
    include/laplace/core/attestation_engine.h
)

set(LAPLACE_CORE_SOURCES
    src/version.c
    src/math4d.c
    src/hash128.c
    src/hilbert4d.c
    src/mantissa.c
    src/super_fibonacci.c
    src/glicko2.c
    src/score.c
    src/codepoint_table.c
    src/astar.cpp
    src/trajectory.c
    src/tier_tree.c
    src/intent_stage.c
    src/merkle_dedup.c
    src/hash_composer.c
    src/grapheme_floor.c
    src/grammar_registry.c
    src/grammar_decomposer.c
    src/grammar_compose.cpp
    src/grammar_rows.c
    src/grammar_tags.c
    src/grapheme_break.c
    src/word_break.c
    src/sentence_break.c
    src/normalize_nfc.c
    src/text_decomposer.c
    src/content_witness_batch.c
    src/ucd_xml.cpp
    src/unicode_seed.cpp
    src/generated/relation_law.c
    src/generated/pos_law.c
    src/highway_table.c
    src/attestation_engine.c
)
set(LAPLACE_PERFCACHE_DIR "${CMAKE_CURRENT_BINARY_DIR}/perfcache")
set(LAPLACE_HIGHWAY_BIN "${LAPLACE_PERFCACHE_DIR}/laplace_highway_perfcache.bin"
    CACHE FILEPATH "Built highway perf-cache blob (consumed by tests + installed)" FORCE)

find_program(LAPLACE_CODEGEN_PYTHON NAMES python python3 REQUIRED)
set(LAPLACE_CODEGEN_SCRIPT ${CMAKE_CURRENT_SOURCE_DIR}/../../scripts/codegen-attestation-law.py)
set(LAPLACE_CODEGEN_OUTPUTS
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/relation_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/pos_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/highway_manifest.h
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/relation_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/pos_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../../extension/laplace_substrate/sql/generated/seed_relation_types.sql.in
    ${CMAKE_CURRENT_SOURCE_DIR}/../../extension/laplace_substrate/sql/generated/seed_pos.sql.in
    ${LAPLACE_HIGHWAY_BIN}
)
add_custom_command(
    OUTPUT  ${LAPLACE_CODEGEN_OUTPUTS}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
    COMMAND ${LAPLACE_CODEGEN_PYTHON} ${LAPLACE_CODEGEN_SCRIPT}
            "--bin-out-dir=${LAPLACE_PERFCACHE_DIR}"
    DEPENDS ${LAPLACE_CODEGEN_SCRIPT}
            ${CMAKE_CURRENT_SOURCE_DIR}/../manifest/relation_types.toml
            ${CMAKE_CURRENT_SOURCE_DIR}/../manifest/pos_tags.toml
    COMMENT "Codegen attestation law + highway perfcache from manifest TOML"
    VERBATIM
)

add_library(laplace_core SHARED ${LAPLACE_CORE_SOURCES})

if(WIN32)
    add_library(laplace_core_static STATIC ${LAPLACE_CORE_SOURCES})
    target_include_directories(laplace_core_static PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
    target_link_libraries(laplace_core_static
        PUBLIC  laplace_eigen
        PRIVATE blake3)
endif()


add_subdirectory(grammars)
target_link_libraries(laplace_core PRIVATE tree_sitter_rt ${LAPLACE_TS_GRAMMARS})
if(WIN32)
    target_link_libraries(laplace_core_static PRIVATE tree_sitter_rt ${LAPLACE_TS_GRAMMARS})
endif()
set(LAPLACE_PERFCACHE_XML "${LAPLACE_PERFCACHE_DIR}/ucd.nounihan.flat.xml")
set(LAPLACE_PERFCACHE_BIN "${LAPLACE_PERFCACHE_DIR}/laplace_t0_perfcache.bin"
    CACHE FILEPATH "Built T0 codepoint perf-cache blob (consumed by tests + installed)" FORCE)

add_custom_command(
    OUTPUT ${LAPLACE_PERFCACHE_XML}
    COMMAND ${CMAKE_COMMAND} -E make_directory ${LAPLACE_PERFCACHE_DIR}
    COMMAND ${CMAKE_COMMAND} -E tar xf "${LAPLACE_UCDXML_ZIP}"
            WORKING_DIRECTORY ${LAPLACE_PERFCACHE_DIR}
    DEPENDS "${LAPLACE_UCDXML_ZIP}"
    COMMENT "Extracting UCDXML ${LAPLACE_UNICODE_VERSION} (${LAPLACE_UCDXML_ZIP})"
    VERBATIM
)

# Source-hash gate in laplace_ucd_tables_emit makes re-runs a sub-second no-op
# when UCD/DUCET/generator/scope are unchanged, so DEPENDS may include the tool.
add_custom_command(
    OUTPUT ${LAPLACE_PERFCACHE_BIN}
    COMMAND $<TARGET_FILE:laplace_ucd_tables_emit>
            --ucdxml      "${LAPLACE_PERFCACHE_XML}"
            --ducet       "${LAPLACE_DUCET_FILE}"
            --ucd-version "${LAPLACE_UNICODE_VERSION}"
            --uca-version "${LAPLACE_UCA_VERSION}"
            --scope       full
            --output      ${LAPLACE_PERFCACHE_BIN}
    DEPENDS ${LAPLACE_PERFCACHE_XML} "${LAPLACE_DUCET_FILE}"
            $<TARGET_FILE:laplace_ucd_tables_emit>
    COMMENT "Emitting T0 perf-cache blob (UCD ${LAPLACE_UNICODE_VERSION} / UCA ${LAPLACE_UCA_VERSION})"
    VERBATIM
)

add_custom_target(laplace_t0_perfcache       ALL DEPENDS ${LAPLACE_PERFCACHE_BIN})
add_custom_target(laplace_highway_perfcache  ALL DEPENDS ${LAPLACE_HIGHWAY_BIN})

set(LAPLACE_PERFCACHE_REFERENCE_BIN "${LAPLACE_PERFCACHE_DIR}/laplace_t0_perfcache.reference.bin")
add_custom_target(laplace_verify_perfcache_determinism
    COMMAND ${CMAKE_COMMAND} -E copy ${LAPLACE_PERFCACHE_BIN} ${LAPLACE_PERFCACHE_REFERENCE_BIN}
    COMMAND ${CMAKE_COMMAND} -E remove ${LAPLACE_PERFCACHE_BIN}
    COMMAND $<TARGET_FILE:laplace_ucd_tables_emit>
            --ucdxml      "${LAPLACE_PERFCACHE_XML}"
            --ducet       "${LAPLACE_DUCET_FILE}"
            --ucd-version "${LAPLACE_UNICODE_VERSION}"
            --uca-version "${LAPLACE_UCA_VERSION}"
            --scope       full
            --output      ${LAPLACE_PERFCACHE_BIN}
    COMMAND ${CMAKE_COMMAND} -E compare_files ${LAPLACE_PERFCACHE_BIN} ${LAPLACE_PERFCACHE_REFERENCE_BIN}
    DEPENDS laplace_t0_perfcache laplace_ucd_tables_emit ${LAPLACE_PERFCACHE_XML} "${LAPLACE_DUCET_FILE}"
    COMMENT "Verify T0 perf-cache emit is byte-deterministic"
    VERBATIM
)

set_target_properties(laplace_core PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_core
    PUBLIC_HEADER "${LAPLACE_CORE_HEADERS}"
)

target_include_directories(laplace_core
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(laplace_core
    PUBLIC  laplace_eigen
    PRIVATE blake3
)

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

install(TARGETS laplace_core
    LIBRARY
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
    PUBLIC_HEADER
        DESTINATION include/laplace/core
        PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

# Ship the linker PDB alongside the DLL so a deployed native crash symbolizes.
# Windows/MSVC only — TARGET_PDB_FILE is invalid on Linux CI (breaks cmake --generate).
if(WIN32)
    install(FILES $<TARGET_PDB_FILE:laplace_core>
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
        OPTIONAL
    )
endif()

install(FILES ${LAPLACE_PERFCACHE_BIN}
    DESTINATION share/laplace
    RENAME "laplace_t0_perfcache_${LAPLACE_UNICODE_VERSION}.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

install(FILES ${LAPLACE_HIGHWAY_BIN}
    DESTINATION share/laplace
    RENAME "laplace_highway_perfcache.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)
