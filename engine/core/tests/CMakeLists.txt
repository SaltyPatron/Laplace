set(LAPLACE_CORE_TEST_SOURCES
    perfcache_env.cpp
    test_version.cpp
    test_math4d.cpp
    test_hash128.cpp
    test_hilbert4d.cpp
    test_mantissa.cpp
    test_super_fibonacci.cpp
    test_image_decomposer.cpp
    test_audio_decomposer.cpp
    test_media_decode.cpp
    test_glicko2.cpp
    test_score.cpp
    test_codepoint_table.cpp
    test_modality_number_table.cpp
    test_astar.cpp
    test_trajectory.cpp
    test_tier_tree.cpp
    test_intent_stage.cpp
    test_absence_filter.cpp
    test_merkle_dedup.cpp
    test_hash_composer.cpp
    test_physicality_id.cpp
    test_grapheme_break.cpp
    test_word_break.cpp
    test_sentence_break.cpp
    test_normalize_nfc.cpp
    test_text_decomposer.cpp
    test_grammar_decomposer.cpp
    test_grammar_compose.cpp
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

add_executable(laplace_core_tests ${LAPLACE_CORE_TEST_SOURCES})

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
