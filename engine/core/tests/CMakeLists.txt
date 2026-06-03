# engine/core/tests/CMakeLists.txt
#
# GoogleTest-based unit tests for liblaplace_core.
# gtest_discover_tests registers each TEST() with CTest automatically.

set(LAPLACE_CORE_TEST_SOURCES
    perfcache_env.cpp
    test_version.cpp
    test_math4d.cpp
    test_hash128.cpp
    test_hilbert4d.cpp
    test_mantissa.cpp
    test_super_fibonacci.cpp
    test_glicko2.cpp
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
)

# Conformance tests need the UCD test corpus path baked in.
# Test corpora live alongside the .txt files; UCDXML doesn't include them.
add_compile_definitions(LAPLACE_UCD_PATH_FOR_TESTS="${LAPLACE_UCD_CONFORMANCE_DIR}")

# The state machines + codepoint_table now read from a runtime-loaded
# perf-cache. perfcache_env.cpp registers a GoogleTest global environment
# that mmaps this blob before any test runs (: the same blob the
# emitter produces from the pinned UCD/UCA drives every conformance test).
add_compile_definitions(LAPLACE_PERFCACHE_PATH_FOR_TESTS="${LAPLACE_PERFCACHE_BIN}")

add_executable(laplace_core_tests ${LAPLACE_CORE_TEST_SOURCES})

# The blob must exist before the test binary runs.
add_dependencies(laplace_core_tests laplace_t0_perfcache)

target_link_libraries(laplace_core_tests
    PRIVATE laplace_core GTest::gtest_main
)

gtest_discover_tests(laplace_core_tests)
