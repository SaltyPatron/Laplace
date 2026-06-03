# engine/core/tools/ucd_tables_emit/CMakeLists.txt
#
# Perf-cache emitter. Reads UCDXML + DUCET and writes
# the T0 codepoint perf-cache binary blob.
#
# Links against the SHIPPED liblaplace_core.so for super_fibonacci /
# hilbert4d / hash128 — same compiled object the C# decomposer's P/Invoke
# resolves. Anything else (re-compiling the .c files into this executable
# under different flags / FMA contraction / EVAL_METHOD) produces 1-ULP
# floating-point drift between the build-time blob and the runtime seed
# (caught 2026-05-25: U+0022 Z coord -0.46815255662448024 vs -0.4681525566244802).
# : same input ⇒ byte-identical output across every consumer.

add_executable(laplace_ucd_tables_emit
    main.cpp
)

target_compile_features(laplace_ucd_tables_emit PRIVATE cxx_std_20)

target_include_directories(laplace_ucd_tables_emit PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

# libxml2 SAX parser — system dep per the approved-libraries list
# (libicu/libxml2/libsqlite3 carve-out).
find_package(LibXml2 REQUIRED)
target_link_libraries(laplace_ucd_tables_emit PRIVATE
    laplace_core            # single source of math truth — same .so the app loads
    LibXml2::LibXml2)

set_target_properties(laplace_ucd_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")
