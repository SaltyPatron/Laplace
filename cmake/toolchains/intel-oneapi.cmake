set(_ONEAPI_ROOT     "/opt/intel/oneapi" CACHE PATH "Intel oneAPI install root")
set(_ONEAPI_COMPILER "${_ONEAPI_ROOT}/compiler/latest")

if(NOT EXISTS "${_ONEAPI_COMPILER}/bin/icx")
    message(FATAL_ERROR "Intel oneAPI compiler not found at ${_ONEAPI_COMPILER}/bin/icx")
endif()

set(CMAKE_C_COMPILER   "${_ONEAPI_COMPILER}/bin/icx"   CACHE FILEPATH "C compiler")
set(CMAKE_CXX_COMPILER "${_ONEAPI_COMPILER}/bin/icpx"  CACHE FILEPATH "C++ compiler")

set(LAPLACE_TARGET_ISA "$ENV{LAPLACE_TARGET_ISA}")
if(NOT LAPLACE_TARGET_ISA)
    set(LAPLACE_TARGET_ISA "AVX2")
endif()

if(LAPLACE_TARGET_ISA STREQUAL "AVX2")
    set(_MARCH "-march=haswell")
elseif(LAPLACE_TARGET_ISA STREQUAL "AVX512")
    set(_MARCH "-march=sapphirerapids")
elseif(LAPLACE_TARGET_ISA STREQUAL "native")
    set(_MARCH "-march=native")
else()
    message(FATAL_ERROR
        "Unknown LAPLACE_TARGET_ISA='${LAPLACE_TARGET_ISA}' "
        "(expected: AVX2, AVX512, or native)")
endif()

set(CMAKE_C_FLAGS_INIT   "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")
set(CMAKE_CXX_FLAGS_INIT "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")

list(APPEND CMAKE_PREFIX_PATH
    "${_ONEAPI_ROOT}/mkl/latest/lib/cmake"
    "${_ONEAPI_ROOT}/tbb/latest/lib/cmake"
)
