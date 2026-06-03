# engine/core/CMakeLists.txt
#
# liblaplace_core.so — substrate primitives.
# coord4d, hash128 (BLAKE3), hilbert4d, mantissa, geometry serde,
# Glicko-2 fixed-point, A* primitives.
#
#: this lib MUST NOT link MKL. Verified at build time by
# ldd check (lands in Epic D).

set(LAPLACE_CORE_HEADERS
    include/laplace/core/version.h
    include/laplace/core/math4d.h
    include/laplace/core/hash128.h
    include/laplace/core/hilbert4d.h
    include/laplace/core/mantissa.h
    include/laplace/core/super_fibonacci.h
    include/laplace/core/glicko2.h
    include/laplace/core/codepoint_table.h
    include/laplace/core/astar.h
    include/laplace/core/trajectory.h
    include/laplace/core/tier_tree.h
    include/laplace/core/intent_stage.h
    include/laplace/core/merkle_dedup.h
    include/laplace/core/hash_composer.h
    include/laplace/core/grapheme_break.h
    include/laplace/core/word_break.h
    include/laplace/core/sentence_break.h
    include/laplace/core/normalize_nfc.h
    include/laplace/core/text_decomposer.h
    include/laplace/core/unicode_seed.h
)

# No coord4d_t / geometry4d_t custom datatypes — POINT4D, LWPOINT, LWLINE,
# LWPOLY (from external/postgis/liblwgeom) are the canonical types; engine
# math kernels operate on raw XYZM-packed double buffers (
# + R22).
set(LAPLACE_CORE_SOURCES
    src/version.c
    src/math4d.c
    src/hash128.c
    src/hilbert4d.c
    src/mantissa.c
    src/super_fibonacci.c
    src/glicko2.c
    src/codepoint_table.c
    src/astar.c
    src/trajectory.c
    src/tier_tree.c
    src/intent_stage.c
    src/merkle_dedup.c
    src/hash_composer.c
    src/grapheme_break.c
    src/word_break.c
    src/sentence_break.c
    src/normalize_nfc.c
    src/text_decomposer.c
    src/unicode_seed.cpp
)

# === T0 codepoint perf-cache ===
# liblaplace_core is DECOUPLED from UCD source: it compiles NO generated
# tables and has no UCD/UCA build dependency. The runtime codepoint_table
# loader mmaps a perf-cache BINARY blob at startup; the UAX#29/UAX#15
# state machines read their property lookups from that mapping.
#
# The blob is produced out-of-band by laplace_ucd_tables_emit from UCDXML
# (UAX#42) + DUCET (UCA allkeys.txt) — the sole UCD/UCA reader in the
# tree. It is a standalone artifact: consumed by the conformance tests,
# installed for the app/runtime to load, and deployable to mobile/embedded
#. It is NOT linked into the .so.

add_library(laplace_core SHARED ${LAPLACE_CORE_SOURCES})

set(LAPLACE_PERFCACHE_DIR "${CMAKE_CURRENT_BINARY_DIR}/perfcache")
set(LAPLACE_PERFCACHE_XML "${LAPLACE_PERFCACHE_DIR}/ucd.nounihan.flat.xml")
set(LAPLACE_PERFCACHE_BIN "${LAPLACE_PERFCACHE_DIR}/laplace_t0_perfcache.bin"
    CACHE FILEPATH "Built T0 codepoint perf-cache blob (consumed by tests + installed)" FORCE)

# Extract the UCDXML zip (~67 MB uncompressed) once; stamp-cached on the
# .zip mtime.
add_custom_command(
    OUTPUT ${LAPLACE_PERFCACHE_XML}
    COMMAND ${CMAKE_COMMAND} -E make_directory ${LAPLACE_PERFCACHE_DIR}
    COMMAND ${CMAKE_COMMAND} -E tar xf "${LAPLACE_UCDXML_ZIP}"
            WORKING_DIRECTORY ${LAPLACE_PERFCACHE_DIR}
    DEPENDS "${LAPLACE_UCDXML_ZIP}"
    COMMENT "Extracting UCDXML ${LAPLACE_UNICODE_VERSION} (${LAPLACE_UCDXML_ZIP})"
    VERBATIM
)

add_custom_command(
    OUTPUT ${LAPLACE_PERFCACHE_BIN}
    COMMAND $<TARGET_FILE:laplace_ucd_tables_emit>
            --ucdxml      "${LAPLACE_PERFCACHE_XML}"
            --ducet       "${LAPLACE_DUCET_FILE}"
            --ucd-version "${LAPLACE_UNICODE_VERSION}"
            --uca-version "${LAPLACE_UCA_VERSION}"
            --output      ${LAPLACE_PERFCACHE_BIN}
    DEPENDS laplace_ucd_tables_emit ${LAPLACE_PERFCACHE_XML} "${LAPLACE_DUCET_FILE}"
    COMMENT "Emitting T0 perf-cache blob (UCD ${LAPLACE_UNICODE_VERSION} / UCA ${LAPLACE_UCA_VERSION})"
    VERBATIM
)

add_custom_target(laplace_t0_perfcache ALL DEPENDS ${LAPLACE_PERFCACHE_BIN})

# Re-emit the T0 perf-cache and byte-compare to the stamp-built artifact.
# / : same UCD + UCA + emit tool → identical bytes.
set(LAPLACE_PERFCACHE_REFERENCE_BIN "${LAPLACE_PERFCACHE_DIR}/laplace_t0_perfcache.reference.bin")
add_custom_target(laplace_verify_perfcache_determinism
    COMMAND ${CMAKE_COMMAND} -E copy ${LAPLACE_PERFCACHE_BIN} ${LAPLACE_PERFCACHE_REFERENCE_BIN}
    COMMAND ${CMAKE_COMMAND} -E remove ${LAPLACE_PERFCACHE_BIN}
    COMMAND $<TARGET_FILE:laplace_ucd_tables_emit>
            --ucdxml      "${LAPLACE_PERFCACHE_XML}"
            --ducet       "${LAPLACE_DUCET_FILE}"
            --ucd-version "${LAPLACE_UNICODE_VERSION}"
            --uca-version "${LAPLACE_UCA_VERSION}"
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

find_package(LibXml2 REQUIRED)
target_link_libraries(laplace_core
    PUBLIC  laplace_eigen        # template-only; no MKL leak
    PRIVATE blake3               # static link; no shared blake3 dep at runtime
    PRIVATE LibXml2::LibXml2     # for laplace_unicode_seed_compute (UCDXML parse)
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

# The T0 perf-cache blob ships alongside the library — the runtime loads
# it at startup (codepoint_table_load_perfcache). Versioned filename so a
# UCD bump is a side-by-side artifact, never an in-place overwrite.
install(FILES ${LAPLACE_PERFCACHE_BIN}
    DESTINATION share/laplace
    RENAME "laplace_t0_perfcache_${LAPLACE_UNICODE_VERSION}.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)
