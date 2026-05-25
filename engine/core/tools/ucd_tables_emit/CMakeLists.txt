# engine/core/tools/ucd_tables_emit/CMakeLists.txt
#
# Perf-cache emitter (ADR 0006 + ADR 0053). Reads UCDXML + DUCET and
# writes the T0 codepoint perf-cache binary blob. Build-time tool;
# UnicodeDecomposer's build-time half. Never links into liblaplace_core.
#
# Links the math kernels (super_fibonacci / hilbert4d / hash128) directly
# as source — NOT the full liblaplace_core.so — to avoid a build-graph
# cycle (liblaplace_core must not depend on the perfcache, and the
# perfcache emitter must not depend on a populated codepoint_table). This
# is ADR 0053's "laplace_core_internal" bootstrap variant: pull in only
# the pure math kernels, which have no UCD-table dependency.

add_executable(laplace_ucd_tables_emit
    main.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../../src/super_fibonacci.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../../src/hilbert4d.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../../src/hash128.c
)

target_compile_features(laplace_ucd_tables_emit PRIVATE cxx_std_20)

target_include_directories(laplace_ucd_tables_emit PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

# libxml2 SAX parser — system dep per STANDARDS.md approved libraries
# (libicu/libxml2/libsqlite3 carve-out). BLAKE3 for codepoint hashes +
# the body CRC trailer.
find_package(LibXml2 REQUIRED)
target_link_libraries(laplace_ucd_tables_emit PRIVATE LibXml2::LibXml2 blake3)

set_target_properties(laplace_ucd_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")
