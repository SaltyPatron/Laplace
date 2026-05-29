# engine/synthesis/tests/CMakeLists.txt
#
# GoogleTest-based unit tests for liblaplace_synthesis.

set(LAPLACE_SYNTHESIS_TEST_SOURCES
    test_version.cpp
    test_recipe.cpp
    test_arch_template.cpp
    test_feature_extractor.cpp
    test_gguf_writer.cpp
    test_bf16_decoder.cpp
    test_token_attn_scorer.cpp
    test_tensor_decompose.cpp
    test_weight_projection.cpp
    test_per_token_magnitude.cpp
    test_projection_per_token.cpp
    test_qk_pairs_threshold.cpp
    test_qk_pairs_threshold_pruned.cpp
    test_qk_project_cached.cpp
)

add_executable(laplace_synthesis_tests ${LAPLACE_SYNTHESIS_TEST_SOURCES})

target_link_libraries(laplace_synthesis_tests
    PRIVATE laplace_synthesis GTest::gtest_main
)

gtest_discover_tests(laplace_synthesis_tests)
