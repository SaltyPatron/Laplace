set(LAPLACE_CORE_TEST_SOURCES
    perfcache_env.cpp
    test_version.cpp
    test_math4d.cpp
    test_hash128.cpp
    test_hilbert4d.cpp
    test_mantissa.cpp
    test_super_fibonacci.cpp
    test_glicko2.cpp
    test_score.cpp
    test_codepoint_table.cpp
    test_astar.cpp
    test_trajectory.cpp
    test_tier_tree.cpp
    test_intent_stage.cpp
    test_merkle_dedup.cpp
    test_hash_composer.cpp
    test_grapheme_break.cpp
    test_word_break.cpp
    test_sentence_break.cpp
    test_normalize_nfc.cpp
    test_text_decomposer.cpp
    test_grammar_decomposer.cpp
    test_grammar_compose.cpp
    test_grammar_tags.cpp
)

add_compile_definitions(LAPLACE_UCD_PATH_FOR_TESTS="${LAPLACE_UCD_CONFORMANCE_DIR}")

add_compile_definitions(LAPLACE_PERFCACHE_PATH_FOR_TESTS="${LAPLACE_PERFCACHE_BIN}")

add_executable(laplace_core_tests ${LAPLACE_CORE_TEST_SOURCES})

add_dependencies(laplace_core_tests laplace_t0_perfcache)

target_link_libraries(laplace_core_tests
    PRIVATE laplace_core GTest::gtest_main
)

# PRE_TEST: discover at ctest time (not link time) so env.cmd PATH + copied DLLs are present.
# POST_BUILD discovery fails with 0xc0000139 when laplace_core export ordinals drift vs a stale .exe.
gtest_discover_tests(laplace_core_tests
    DISCOVERY_MODE PRE_TEST
    DISCOVERY_TIMEOUT 120
)

if(WIN32)
    set(_LAPLACE_LIBXML2_DLL "C:/Program Files/PostgreSQL/18/bin/libxml2.dll")
    add_custom_command(TARGET laplace_core_tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                $<TARGET_FILE:laplace_core>
                $<TARGET_FILE_DIR:laplace_core_tests>
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${_LAPLACE_LIBXML2_DLL}"
                $<TARGET_FILE_DIR:laplace_core_tests>
        COMMENT "Copy laplace_core.dll + libxml2.dll beside laplace_core_tests.exe")
endif()
