set(LAPLACE_DYNAMICS_HEADERS
    include/laplace/dynamics/init.h
    include/laplace/dynamics/procrustes.h
    include/laplace/dynamics/eigenmaps.h
    include/laplace/dynamics/gram_schmidt.h
    include/laplace/dynamics/bilinear_edges.h
)

set(LAPLACE_DYNAMICS_SOURCES
    src/init.cpp
    src/procrustes.cpp
    src/eigenmaps.cpp
    src/gram_schmidt.cpp
    src/bilinear_edges.cpp
)

add_library(laplace_dynamics SHARED ${LAPLACE_DYNAMICS_SOURCES})

if(WIN32)
    add_library(laplace_dynamics_static STATIC ${LAPLACE_DYNAMICS_SOURCES})
    target_include_directories(laplace_dynamics_static PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
    target_link_libraries(laplace_dynamics_static
        PUBLIC laplace_core_static laplace_spectra)
    target_compile_definitions(laplace_dynamics_static PRIVATE
        EIGEN_USE_MKL_ALL
        EIGEN_USE_BLAS
        EIGEN_USE_LAPACKE
        MKL_LP64
        LAPLACE_HAS_MKL=1
    )
    target_include_directories(laplace_dynamics_static PRIVATE "$ENV{MKLROOT}/include")
endif()

set_target_properties(laplace_dynamics PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_dynamics
)

target_include_directories(laplace_dynamics
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(laplace_dynamics
    PUBLIC  laplace_core laplace_spectra
)

option(LAPLACE_REQUIRE_MKL "Fail configure if MKL/TBB not found (Epic D production)" OFF)

set(MKL_INTERFACE "lp64" CACHE STRING "MKL int-size interface (lp64=int, ilp64=long long)")
set(MKL_THREADING "tbb_thread" CACHE STRING "MKL threading layer")

if(LAPLACE_REQUIRE_MKL)
    find_package(MKL CONFIG REQUIRED)
    find_package(TBB CONFIG REQUIRED)
else()
    find_package(MKL CONFIG QUIET)
    find_package(TBB CONFIG QUIET)
endif()

if(MKL_FOUND AND TBB_FOUND)
    message(STATUS "laplace_dynamics: MKL + TBB found — full Intel-toolchain integration enabled")
    target_compile_definitions(laplace_dynamics PRIVATE
        EIGEN_USE_MKL_ALL
        EIGEN_USE_BLAS
        EIGEN_USE_LAPACKE
        MKL_LP64
        MKL_THREADING_TBB
        LAPLACE_HAS_MKL=1
    )
    target_link_libraries(laplace_dynamics PRIVATE MKL::MKL TBB::tbb)
else()
    message(WARNING
        "laplace_dynamics: MKL/TBB NOT FOUND — building with Eigen-only fallback.\n"
        "  This is OK for local dev scaffolding but Production / Epic D requires MKL.\n"
        "  To enable full integration:\n"
        "    1. source /opt/intel/oneapi/setvars.sh\n"
        "    2. re-run cmake configure\n"
        "  To fail configure on missing MKL (Production): -DLAPLACE_REQUIRE_MKL=ON")
endif()

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

install(TARGETS laplace_dynamics
    LIBRARY
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
)
install(DIRECTORY include/laplace/dynamics
    DESTINATION include/laplace
    DIRECTORY_PERMISSIONS ${LAPLACE_INSTALL_DIR_PERMS}
    FILE_PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
    FILES_MATCHING PATTERN "*.h"
)
