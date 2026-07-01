set(LAPLACE_SYNTHESIS_HEADERS
    include/laplace/synthesis/version.h
    include/laplace/synthesis/init.h
    include/laplace/synthesis/recipe.h
    include/laplace/synthesis/arch_template.h
    include/laplace/synthesis/feature_extractor.h
    include/laplace/synthesis/gguf_writer.h
    include/laplace/synthesis/bf16_decoder.h
    include/laplace/synthesis/tensor_decompose.h
    include/laplace/synthesis/qk_pairs_threshold.h
    include/laplace/synthesis/qk_pairs_threshold_pruned.h
    include/laplace/synthesis/qk_project_cached.h
    include/laplace/synthesis/format_writer.h
    include/laplace/synthesis/f32_gather.h
)

set(LAPLACE_SYNTHESIS_SOURCES
    src/init.cpp
    src/version.cpp
    src/recipe.cpp
    src/arch_template.cpp
    src/feature_extractor.cpp
    src/gguf_writer.cpp
    src/bf16_decoder.c
    src/tensor_decompose.cpp
    src/qk_pairs_threshold.cpp
    src/qk_pairs_threshold_pruned.cpp
    src/qk_project_cached.cpp
    src/format_writer.cpp
    src/f32_gather.c
)

add_library(laplace_synthesis SHARED ${LAPLACE_SYNTHESIS_SOURCES})

set_target_properties(laplace_synthesis PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_synthesis
)

target_include_directories(laplace_synthesis
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(laplace_synthesis
    PUBLIC  laplace_core laplace_dynamics
)

option(LAPLACE_SYNTHESIS_REQUIRE_MKL
    "Fail configure if MKL/TBB not found for synthesis (production)"
    OFF)

set(MKL_INTERFACE "lp64" CACHE STRING "MKL int-size interface (lp64=int, ilp64=long long)")
set(MKL_THREADING "tbb_thread" CACHE STRING "MKL threading layer")

if(LAPLACE_SYNTHESIS_REQUIRE_MKL)
    find_package(MKL CONFIG REQUIRED)
    find_package(TBB CONFIG REQUIRED)
else()
    find_package(MKL CONFIG QUIET)
    find_package(TBB CONFIG QUIET)
endif()

if(MKL_FOUND AND TBB_FOUND)
    message(STATUS "laplace_synthesis: MKL + TBB found — DGEMM token scorer enabled")
    target_compile_definitions(laplace_synthesis PRIVATE
        MKL_LP64
        MKL_THREADING_TBB
        LAPLACE_HAS_MKL=1
    )
    target_link_libraries(laplace_synthesis PRIVATE MKL::MKL TBB::tbb)
else()
    message(WARNING
        "laplace_synthesis: MKL/TBB NOT FOUND — token_attn_scorer uses scalar fallback.\n"
        "  Source /opt/intel/oneapi/setvars.sh and re-run cmake to enable DGEMM path.")
endif()

if(CMAKE_C_COMPILER_ID MATCHES "Intel")
    set_source_files_properties(src/bf16_decoder.c PROPERTIES
        COMPILE_OPTIONS "-mavx2")
elseif(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    set_source_files_properties(src/bf16_decoder.c PROPERTIES
        COMPILE_OPTIONS "-mavx2")
endif()

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

install(TARGETS laplace_synthesis
    LIBRARY
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
)
install(DIRECTORY include/laplace/synthesis
    DESTINATION include/laplace
    DIRECTORY_PERMISSIONS ${LAPLACE_INSTALL_DIR_PERMS}
    FILE_PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
    FILES_MATCHING PATTERN "*.h"
)
