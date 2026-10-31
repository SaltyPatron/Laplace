# An empty Native selection retains the monorepo's existing fallback behavior.
# Do not persist an implicit environment/external default in the cache.
if(NOT DEFINED LAPLACE_NATIVE OR LAPLACE_NATIVE STREQUAL "")
    if(DEFINED ENV{LAPLACE_NATIVE} AND NOT "$ENV{LAPLACE_NATIVE}" STREQUAL "")
        set(LAPLACE_NATIVE "$ENV{LAPLACE_NATIVE}")
    else()
        set(LAPLACE_NATIVE "${LAPLACE_EXTERNAL}/laplace-native")
    endif()
endif()
if(DEFINED CACHE{LAPLACE_NATIVE})
    # Keep CMake's normalization of an explicitly supplied, untyped -D path.
    set(LAPLACE_NATIVE "${LAPLACE_NATIVE}" CACHE PATH "Laplace-Native checkout: identity (lp_id_compose)")
endif()
file(TO_CMAKE_PATH "${LAPLACE_NATIVE}" LAPLACE_NATIVE)
