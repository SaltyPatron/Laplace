set(LAPLACE_CORE_TEST_SOURCES
    perfcache_env.cpp
    test_version.cpp
    test_math4d.cpp
    test_hash128.cpp
    test_hilbert4d.cpp
    test_mantissa.cpp
    test_super_fibonacci.cpp
    test_byte_atoms.cpp
    test_image_decomposer.cpp
    test_audio_decomposer.cpp
    test_media_decode.cpp
    test_glicko2.cpp
    test_glicko2_extreme.cpp
    test_score.cpp
    test_codepoint_table.cpp
    test_modality_number_table.cpp
    test_astar.cpp
    test_trajectory.cpp
    test_tier_tree.cpp
    test_intent_stage.cpp
    test_intent_stage_physicality_law.cpp
    test_ordered_composition.cpp
    test_absence_filter.cpp
    test_merkle_dedup.cpp
    test_hash_composer.cpp
    test_physicality_id.cpp
    test_physicality_descriptor.cpp
    test_physicality_descriptor_frontier.cpp
    test_physicality_descriptor_provider.cpp
    test_centroid_workspace.cpp
    test_physicality_descriptor_transport.cpp
    test_physicality_descriptor_admission.cpp
    test_physicality_descriptor_retention.cpp
    test_grapheme_break.cpp
    test_word_break.cpp
    test_sentence_break.cpp
    test_normalize_nfc.cpp
    test_identity_key.cpp
    test_text_decomposer.cpp
    test_content_root_placement.cpp
    test_text_roundtrip_gate.cpp
    test_xml_stream.cpp
    test_grammar_decomposer.cpp
    test_grammar_compose.cpp
    test_grammar_physicality.cpp
    test_grammar_tags.cpp
    test_relation_law.cpp
)

add_compile_definitions(LAPLACE_UCD_PATH_FOR_TESTS="${LAPLACE_UCD_CONFORMANCE_DIR}")

add_compile_definitions(LAPLACE_PERFCACHE_PATH_FOR_TESTS="${LAPLACE_PERFCACHE_BIN}")
add_compile_definitions(
    LAPLACE_MODALITY_NUMBER_PERFCACHE_PATH_FOR_TESTS="${LAPLACE_MODALITY_NUMBER_PERFCACHE_BIN}")

# Committed Syzygy fixture tables (KQvK/KRvK, ~14 KB) for the probe kernel.
add_compile_definitions(
    LAPLACE_SYZYGY_FIXTURE_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../../test-data/syzygy")

# This one historical test file is a binary COPY serializer/partition fixture and
# deliberately contains arbitrary ids/synthetic XYZM bytes. Isolate those fixture
# bytes from the new semantic law without weakening the production function. The
# dedicated physicality-law test above compiles without this adapter.
if(MSVC)
    set_source_files_properties(test_intent_stage.cpp PROPERTIES
        COMPILE_OPTIONS "/FI${CMAKE_CURRENT_SOURCE_DIR}/intent_stage_fixture_adapter.h")
else()
    set_source_files_properties(test_intent_stage.cpp PROPERTIES
        COMPILE_OPTIONS "-include;${CMAKE_CURRENT_SOURCE_DIR}/intent_stage_fixture_adapter.h")
endif()

add_executable(laplace_core_tests ${LAPLACE_CORE_TEST_SOURCES})

# The same production decoder is also testable with plain C and no perfcache
# fixture: ParseStructure already contains exact IDs and schema delimiters.
add_executable(laplace_ud_parse_tests test_ud_parse.c)
target_link_libraries(laplace_ud_parse_tests PRIVATE laplace_core)
add_test(NAME laplace_ud_parse_tests COMMAND laplace_ud_parse_tests)
add_dependencies(laplace_core_tests laplace_ud_parse_tests)

add_executable(laplace_task_shape_tests test_task_shape.c)
target_link_libraries(laplace_task_shape_tests PRIVATE laplace_core)
add_test(NAME laplace_task_shape_tests COMMAND laplace_task_shape_tests)
add_dependencies(laplace_core_tests laplace_task_shape_tests)

add_dependencies(laplace_core_tests laplace_t0_perfcache laplace_modality_number_perfcache)

target_link_libraries(laplace_core_tests
    PRIVATE laplace_core GTest::gtest_main
)

# Probe-kernel tests link laplace_syzygy, which is ingest-side tooling: never part
# of laplace_core, never loaded by PostgreSQL.
add_executable(laplace_syzygy_tests test_syzygy.cpp)
target_link_libraries(laplace_syzygy_tests PRIVATE laplace_syzygy GTest::gtest_main)
gtest_discover_tests(laplace_syzygy_tests
    DISCOVERY_MODE PRE_TEST
    DISCOVERY_TIMEOUT 120
)


gtest_discover_tests(laplace_core_tests
    DISCOVERY_MODE PRE_TEST
    DISCOVERY_TIMEOUT 120
)

if(WIN32)
    add_custom_command(TARGET laplace_core_tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                $<TARGET_FILE:laplace_core>
                $<TARGET_FILE_DIR:laplace_core_tests>
        COMMENT "Copy laplace_core.dll beside laplace_core_tests.exe")

    if(CMAKE_CXX_FLAGS MATCHES "fsanitize=address" OR CMAKE_C_FLAGS MATCHES "fsanitize=address")
        get_filename_component(_ICX_BIN "${CMAKE_CXX_COMPILER}" DIRECTORY)
        file(GLOB _INTEL_ASAN_RT "${_ICX_BIN}/../lib/clang/*/lib/windows/clang_rt.asan_dynamic-x86_64.dll")
        if(_INTEL_ASAN_RT)
            list(GET _INTEL_ASAN_RT 0 _INTEL_ASAN_RT0)
            add_custom_command(TARGET laplace_core_tests POST_BUILD
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                        "${_INTEL_ASAN_RT0}" $<TARGET_FILE_DIR:laplace_core_tests>
                COMMENT "Copy Intel asan runtime beside laplace_core_tests.exe (avoid MSVC asan shadowing)")
        endif()
    endif()
endif()

# The source-policy suite deliberately does not load a native library. This
# post-build case pins the Python publication verifier to this exact core,
# checks known BLAKE3 vectors, and rejects a corrupted native-framed floor.
if(UNIX)
    set(_chess_floor_test_work "${CMAKE_CURRENT_BINARY_DIR}/chess-floor-artifact-test")
    file(MAKE_DIRECTORY "${_chess_floor_test_work}")
    add_test(NAME laplace_chess_floor_artifacts_native
        COMMAND "${CMAKE_COMMAND}" -E env
            "TMPDIR=${_chess_floor_test_work}"
            "LAPLACE_TEST_NATIVE_CORE=$<TARGET_FILE:laplace_core>"
            "${LAPLACE_CODEGEN_PYTHON}"
            "${CMAKE_SOURCE_DIR}/scripts/test-chess-floor-artifacts.py"
            ArtifactTests.test_native_hash_abi_and_floor_checksum_when_current_library_is_available)
    set_tests_properties(laplace_chess_floor_artifacts_native PROPERTIES
        TIMEOUT 60 LABELS "chess;artifact")
endif()
