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
)

add_compile_definitions(LAPLACE_UCD_PATH_FOR_TESTS="${LAPLACE_UCD_CONFORMANCE_DIR}")

add_compile_definitions(LAPLACE_PERFCACHE_PATH_FOR_TESTS="${LAPLACE_PERFCACHE_BIN}")

add_executable(laplace_core_tests ${LAPLACE_CORE_TEST_SOURCES})

add_dependencies(laplace_core_tests laplace_t0_perfcache)

target_link_libraries(laplace_core_tests
    PRIVATE laplace_core GTest::gtest_main
)

gtest_discover_tests(laplace_core_tests)
