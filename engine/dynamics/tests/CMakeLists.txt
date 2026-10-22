set(LAPLACE_DYNAMICS_TEST_SOURCES
    test_init.cpp
    test_toolchain.cpp
    test_procrustes.cpp
    test_eigenmaps.cpp
    test_gram_schmidt.cpp
    test_bilinear_edges.cpp
    test_model_math.cpp
)

add_executable(laplace_dynamics_tests ${LAPLACE_DYNAMICS_TEST_SOURCES})

target_link_libraries(laplace_dynamics_tests
    PRIVATE laplace_dynamics GTest::gtest_main
)

gtest_discover_tests(laplace_dynamics_tests)

# Do not link the engine into this host: a linked test executable exposes its
# dependencies globally and misses the managed host's RTLD_LOCAL load order.
add_executable(laplace_dynamics_loader_test test_loader.cpp)
target_include_directories(laplace_dynamics_loader_test PRIVATE
    ../include ../../core/include)
target_link_libraries(laplace_dynamics_loader_test PRIVATE ${CMAKE_DL_LIBS})
add_dependencies(laplace_dynamics_loader_test laplace_core laplace_dynamics)
add_test(NAME LaplaceDynamicsLoader.LocalScopeKernel
    COMMAND laplace_dynamics_loader_test
        $<TARGET_FILE:laplace_core> $<TARGET_FILE:laplace_dynamics>)
