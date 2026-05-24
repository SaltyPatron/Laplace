# engine/core/CMakeLists.txt
#
# liblaplace_core.so — substrate primitives (per ADR 0024).
# coord4d, hash128 (BLAKE3), hilbert4d, mantissa, geometry serde,
# Glicko-2 fixed-point, A* primitives.
#
# Per ADR 0030: this lib MUST NOT link MKL. Verified at build time by
# ldd check (lands in Epic D).

set(LAPLACE_CORE_HEADERS
    include/laplace/core/version.h
    include/laplace/core/math4d.h
    include/laplace/core/hash128.h
    include/laplace/core/hilbert4d.h
    include/laplace/core/mantissa.h
    include/laplace/core/super_fibonacci.h
    include/laplace/core/glicko2.h
    include/laplace/core/codepoint_table.h
    include/laplace/core/astar.h
    include/laplace/core/trajectory.h
    include/laplace/core/tier_tree.h
    include/laplace/core/intent_stage.h
    include/laplace/core/merkle_dedup.h
    include/laplace/core/hash_composer.h
)

# No coord4d_t / geometry4d_t custom datatypes — POINT4D, LWPOINT, LWLINE,
# LWPOLY (from external/postgis/liblwgeom) are the canonical types; engine
# math kernels operate on raw XYZM-packed double buffers (per RULES.md R1
# + R22).
set(LAPLACE_CORE_SOURCES
    src/version.c
    src/math4d.c
    src/hash128.c
    src/hilbert4d.c
    src/mantissa.c
    src/super_fibonacci.c
    src/glicko2.c
    src/codepoint_table.c
    src/astar.c
    src/trajectory.c
    src/tier_tree.c
    src/intent_stage.c
    src/merkle_dedup.c
    src/hash_composer.c
)

add_library(laplace_core SHARED ${LAPLACE_CORE_SOURCES})

set_target_properties(laplace_core PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_core
    PUBLIC_HEADER "${LAPLACE_CORE_HEADERS}"
)

target_include_directories(laplace_core
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(laplace_core
    PUBLIC  laplace_eigen        # template-only; no MKL leak
    PRIVATE blake3               # static link; no shared blake3 dep at runtime
)

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

install(TARGETS laplace_core
    LIBRARY
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
    PUBLIC_HEADER
        DESTINATION include/laplace/core
        PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)
