# Native CTest scaffold — one Catch2-based test executable per service folder.
# Phase 1 / Track A — sample test only. Real per-service tests land in Phase 2.

add_executable(laplace_native_smoke smoke_test.c)
target_link_libraries(laplace_native_smoke PRIVATE laplace_native)
target_include_directories(laplace_native_smoke PRIVATE ${CMAKE_SOURCE_DIR}/ext/laplace_pg/include)

add_test(NAME laplace_native_smoke COMMAND laplace_native_smoke)
