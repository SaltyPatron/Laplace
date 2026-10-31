# An empty Native selection retains the monorepo's existing fallback behavior.
# Do not persist an implicit environment/external default in the cache.
if(NOT DEFINED LAPLACE_NATIVE OR LAPLACE_NATIVE STREQUAL "")
    if(DEFINED ENV{LAPLACE_NATIVE} AND NOT "$ENV{LAPLACE_NATIVE}" STREQUAL "")
        set(LAPLACE_NATIVE "$ENV{LAPLACE_NATIVE}")
    else()
        set(LAPLACE_NATIVE "${LAPLACE_EXTERNAL}/laplace-native")
    endif()
endif()
file(TO_CMAKE_PATH "${LAPLACE_NATIVE}" LAPLACE_NATIVE)
