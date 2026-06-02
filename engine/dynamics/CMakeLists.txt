# engine/dynamics/CMakeLists.txt
#
# liblaplace_dynamics.so — Procrustes / Laplacian eigenmaps /
# Gram-Schmidt / lottery-ticket sparsity. Per ADR 0024 + 0030, this lib
# pulls in oneMKL + oneTBB + Spectra; liblaplace_core MUST NOT.
#
# MKL/TBB integration follows ADR 0030: EIGEN_USE_MKL_ALL dispatches
# Eigen's dense ops to MKL; MKL_THREADING_TBB unifies the scheduler;
# MKL_CBWR set at process init (laplace_dynamics_init) locks reduction
# order for substrate determinism per RULES.md R7.

set(LAPLACE_DYNAMICS_HEADERS
    include/laplace/dynamics/init.h
    include/laplace/dynamics/procrustes.h
    include/laplace/dynamics/eigenmaps.h
    include/laplace/dynamics/gram_schmidt.h
    include/laplace/dynamics/sparsity.h
    include/laplace/dynamics/circuit_extract.h
    include/laplace/dynamics/bilinear_edges.h
)

set(LAPLACE_DYNAMICS_SOURCES
    src/init.cpp
    src/procrustes.cpp
    src/eigenmaps.cpp
    src/gram_schmidt.cpp
    src/sparsity.cpp
    src/circuit_extract.cpp
    src/bilinear_edges.cpp
)

add_library(laplace_dynamics SHARED ${LAPLACE_DYNAMICS_SOURCES})

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

# === oneMKL + oneTBB integration (per ADR 0030) ===
#
# find_package looks at oneAPI's MKLConfig.cmake / TBBConfig.cmake, which
# are installed under /opt/intel/oneapi/<mkl|tbb>/latest/lib/cmake/.
# Sourcing /opt/intel/oneapi/setvars.sh sets the env vars find_package
# needs (MKLROOT, TBBROOT, CMAKE_PREFIX_PATH).
#
# Per RULES.md R9 (no silent failures): if MKL/TBB are not findable,
# configure prints a clear actionable message but allows the build to
# proceed with Eigen-only fallback during local dev. Production builds
# (and Epic D Story D.4+) require strict MKL — flip LAPLACE_REQUIRE_MKL
# to ON to make missing MKL/TBB a fatal configure error.

option(LAPLACE_REQUIRE_MKL "Fail configure if MKL/TBB not found (Epic D production)" OFF)

# Pin MKL interface to LP64 (32-bit int) BEFORE find_package(MKL). oneMKL's
# MKLConfig.cmake selects between lp64 and ilp64 based on this variable;
# without it some versions default to ilp64, which makes lapack_int
# resolve to `long long` while our EIGEN_USE_LAPACKE path assumes `int*`
# and fails compile in Eigen/src/QR/ColPivHouseholderQR_LAPACKE.h. The
# MKL_LP64 compile define below (passed to Eigen) must match this choice.
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
