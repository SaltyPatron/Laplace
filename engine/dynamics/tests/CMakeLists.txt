# engine/dynamics/tests/CMakeLists.txt
#
# GoogleTest-based unit tests for liblaplace_dynamics. Real test bodies
# land Chunk 6 (Procrustes/eigenmaps/Gram-Schmidt/sparsity). Stubs for now.

set(LAPLACE_DYNAMICS_TEST_SOURCES
    test_init.cpp
    test_procrustes.cpp
    test_eigenmaps.cpp
    test_gram_schmidt.cpp
    test_sparsity.cpp
)

add_executable(laplace_dynamics_tests ${LAPLACE_DYNAMICS_TEST_SOURCES})

target_link_libraries(laplace_dynamics_tests
    PRIVATE laplace_dynamics GTest::gtest_main
)

gtest_discover_tests(laplace_dynamics_tests)
