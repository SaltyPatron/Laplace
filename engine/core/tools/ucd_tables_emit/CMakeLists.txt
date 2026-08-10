add_executable(laplace_ucd_tables_emit
    main.cpp
)

target_compile_features(laplace_ucd_tables_emit PRIVATE cxx_std_20)

target_include_directories(laplace_ucd_tables_emit PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

target_link_libraries(laplace_ucd_tables_emit PRIVATE
    laplace_core)

set_target_properties(laplace_ucd_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")

# GENERATOR FINGERPRINT — the source-hash gate must invalidate itself.
#
# The blob's staleness key is source_hash = blake3(xml || ducet || ucd_ver ||
# uca_ver || GENERATOR_TAG || scope). GENERATOR_TAG was a hand-edited string
# constant with a comment asking the author to "bump when the emit LOGIC
# changes". Nothing enforced it, so a change to the rank/coord computation left
# the hash identical, the emit tool printed "sources unchanged — crawl skipped",
# and the blob silently kept the OLD geometry through every build including CI.
#
# MEASURED 2026-08-10: the Hangul collation fix altered rank assignment in
# unicode_seed.cpp without touching GENERATOR_TAG. A full CI build reported
# "Build — engine, extensions, app, perfcache: success" while regenerating
# nothing. Green meant "it compiles", not "the fix is in the blob".
#
# Hash the generator's own sources at configure time instead. Any edit to the
# emit logic or the seed computation changes the fingerprint, which changes
# source_hash, which forces the regenerate — with no one having to remember.
set(_LAPLACE_GEN_SRCS
    "${CMAKE_CURRENT_SOURCE_DIR}/main.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../src/unicode_seed.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../src/super_fibonacci.c"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../src/hilbert4d.c")
set(_LAPLACE_GEN_MIX "")
foreach(_src IN LISTS _LAPLACE_GEN_SRCS)
    if(EXISTS "${_src}")
        file(SHA256 "${_src}" _h)
        string(APPEND _LAPLACE_GEN_MIX "${_h}")
    endif()
endforeach()
string(SHA256 _LAPLACE_GEN_FINGERPRINT "${_LAPLACE_GEN_MIX}")
string(SUBSTRING "${_LAPLACE_GEN_FINGERPRINT}" 0 16 _LAPLACE_GEN_FINGERPRINT)
message(STATUS "Laplace perfcache generator fingerprint: ${_LAPLACE_GEN_FINGERPRINT}")

target_compile_definitions(laplace_ucd_tables_emit PRIVATE
    LAPLACE_GENERATOR_FINGERPRINT="${_LAPLACE_GEN_FINGERPRINT}")

# CONFIGURE_DEPENDS equivalent: re-run cmake when a generator source changes, so
# the fingerprint above is never computed from a stale read.
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS ${_LAPLACE_GEN_SRCS})
