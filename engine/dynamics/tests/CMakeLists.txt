set(LAPLACE_DYNAMICS_TEST_SOURCES
    test_init.cpp
    test_toolchain.cpp
    test_procrustes.cpp
    test_eigenmaps.cpp
    test_gram_schmidt.cpp
    test_bilinear_edges.cpp
)

add_executable(laplace_dynamics_tests ${LAPLACE_DYNAMICS_TEST_SOURCES})

target_link_libraries(laplace_dynamics_tests
    PRIVATE laplace_dynamics GTest::gtest_main
)

gtest_discover_tests(laplace_dynamics_tests)
