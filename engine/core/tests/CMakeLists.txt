# engine/core/tests/CMakeLists.txt
#
# GoogleTest-based unit tests for liblaplace_core. Real test bodies land
# in Chunk 1 (coord4d/hash128/hilbert4d/mantissa). For now these are
# scaffolding: one TEST() per module proving the C ABI links and a
# placeholder assertion passes. gtest_discover_tests registers each
# TEST() with CTest automatically.

set(LAPLACE_CORE_TEST_SOURCES
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
)

add_executable(laplace_core_tests ${LAPLACE_CORE_TEST_SOURCES})

target_link_libraries(laplace_core_tests
    PRIVATE laplace_core GTest::gtest_main
)

gtest_discover_tests(laplace_core_tests)
