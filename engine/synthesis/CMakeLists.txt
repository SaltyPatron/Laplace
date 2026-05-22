# engine/synthesis/CMakeLists.txt
#
# liblaplace_synthesis.so — Substrate Synthesis: parse a recipe JSON,
# materialize tensors from substrate state, emit a model file.
# Per ADR 0024, this lib inherits dynamics' MKL/TBB/Spectra footprint.
#
# Implementations land Chunks 7-8 (Stories 7.1 onward).

set(LAPLACE_SYNTHESIS_HEADERS
    include/laplace/synthesis/version.h
    include/laplace/synthesis/recipe.h
    include/laplace/synthesis/arch_template.h
    include/laplace/synthesis/feature_extractor.h
    include/laplace/synthesis/gguf_writer.h
)

set(LAPLACE_SYNTHESIS_SOURCES
    src/version.cpp
    src/recipe.cpp
    src/arch_template.cpp
    src/feature_extractor.cpp
    src/gguf_writer.cpp
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

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

install(TARGETS laplace_synthesis
    LIBRARY DESTINATION lib
)
install(DIRECTORY include/laplace/synthesis
    DESTINATION include/laplace
    FILES_MATCHING PATTERN "*.h"
)
