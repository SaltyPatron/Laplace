find_program(_LAPLACE_GCC NAMES gcc REQUIRED)
find_program(_LAPLACE_GXX NAMES g++ REQUIRED)

set(CMAKE_C_COMPILER   "${_LAPLACE_GCC}" CACHE FILEPATH "C compiler (gcc)")
set(CMAKE_CXX_COMPILER "${_LAPLACE_GXX}" CACHE FILEPATH "C++ compiler (g++)")

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
