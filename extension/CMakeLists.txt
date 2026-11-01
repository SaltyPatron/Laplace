# Required on every platform: the .sql.in postprocess rules in laplace_geom and
# laplace_substrate run through `bash -c "grep | perl"`. Fail at configure time,
# not with a cryptic `/bin/sh: -c: not found` mid-ninja.
find_program(BASH bash
    HINTS "C:/Program Files/Git/bin"
    DOC "bash for SQL postprocess (grep/perl)")
if(NOT BASH)
    message(FATAL_ERROR "bash not found — required for extension SQL postprocess")
endif()
message(STATUS "Laplace SQL bash: ${BASH}")
include("${CMAKE_CURRENT_LIST_DIR}/cmake/LaplaceWindowsExports.cmake")

if(WIN32 AND CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    cmake_minimum_required(VERSION 3.20)
    project(laplace_extensions VERSION 0.1.0 LANGUAGES C CXX)

    get_filename_component(LAPLACE_SQLPP "${CMAKE_C_COMPILER}" ABSOLUTE)
    set(LAPLACE_SQLPP_FLAGS -E -traditional-cpp -w -Upixel -Ubool)
    message(STATUS "Laplace SQLPP: ${LAPLACE_SQLPP}")

    find_program(LAPLACE_PG_CONFIG pg_config
        HINTS "$ENV{LAPLACE_PG_DIR}/bin" "$ENV{PGBIN}")
    if(NOT LAPLACE_PG_CONFIG)
        message(FATAL_ERROR "pg_config not found")
    endif()
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --includedir-server
        OUTPUT_VARIABLE LAPLACE_PG_INCLUDEDIR_SERVER OUTPUT_STRIP_TRAILING_WHITESPACE)
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --includedir
        OUTPUT_VARIABLE LAPLACE_PG_INCLUDEDIR OUTPUT_STRIP_TRAILING_WHITESPACE)
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --libdir
        OUTPUT_VARIABLE LAPLACE_PG_LIBDIR OUTPUT_STRIP_TRAILING_WHITESPACE)
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --pkglibdir
        OUTPUT_VARIABLE LAPLACE_PG_PKGLIBDIR OUTPUT_STRIP_TRAILING_WHITESPACE)
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --sharedir
        OUTPUT_VARIABLE LAPLACE_PG_SHAREDIR OUTPUT_STRIP_TRAILING_WHITESPACE)
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --bindir
        OUTPUT_VARIABLE LAPLACE_PG_BINDIR OUTPUT_STRIP_TRAILING_WHITESPACE)
    execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --version
        OUTPUT_VARIABLE LAPLACE_PG_VERSION OUTPUT_STRIP_TRAILING_WHITESPACE
        COMMAND_ERROR_IS_FATAL ANY)
    string(REGEX MATCH "PostgreSQL +([0-9]+)" _pg_major_match "${LAPLACE_PG_VERSION}")
    set(LAPLACE_PG_MAJOR "${CMAKE_MATCH_1}")
    if(NOT LAPLACE_PG_MAJOR)
        message(FATAL_ERROR "Could not parse PostgreSQL version: ${LAPLACE_PG_VERSION}")
    endif()
    message(STATUS "PG server includes: ${LAPLACE_PG_INCLUDEDIR_SERVER}")
    message(STATUS "PG pkglibdir:       ${LAPLACE_PG_PKGLIBDIR}")

    set(LAPLACE_ENGINE_BUILD "${LAPLACE_ENGINE_BUILD}" CACHE PATH
        "Engine build tree holding laplace_core/dynamics static libs")
    if(LAPLACE_ENGINE_BUILD STREQUAL "")
        if(DEFINED ENV{LAPLACE_ENGINE_BUILD} AND NOT "$ENV{LAPLACE_ENGINE_BUILD}" STREQUAL "")
            set(LAPLACE_ENGINE_BUILD "$ENV{LAPLACE_ENGINE_BUILD}")
        elseif(DEFINED ENV{LAPLACE_BUILD_ROOT} AND NOT "$ENV{LAPLACE_BUILD_ROOT}" STREQUAL "")
            set(LAPLACE_ENGINE_BUILD "$ENV{LAPLACE_BUILD_ROOT}/build-win")
        else()
            set(LAPLACE_ENGINE_BUILD "D:/Data/Laplace/build-win")
        endif()
    endif()
    # Check exactly the two archives imported below. The PostgreSQL adapter
    # uses sequential MKL initialization, not the application's TBB archive.
    foreach(_archive core/laplace_core_static.lib dynamics/laplace_dynamics_pg_static.lib)
        if(NOT EXISTS "${LAPLACE_ENGINE_BUILD}/${_archive}")
            message(FATAL_ERROR "Missing ${LAPLACE_ENGINE_BUILD}/${_archive} — build the extension prerequisites (scripts\\win\\build-extensions.cmd)")
        endif()
    endforeach()

    if(NOT DEFINED ENV{MKLROOT})
        message(FATAL_ERROR "MKLROOT not set — run via scripts\\win\\build-extensions.cmd (env.cmd chain). "
            "Do NOT cmake --install to C:\\Program Files\\PostgreSQL; deploy with scripts\\win\\install-extensions.cmd")
    endif()
    set(LAPLACE_MKL_STATIC
        "$ENV{MKLROOT}/lib/mkl_intel_lp64.lib"
        "$ENV{MKLROOT}/lib/mkl_sequential.lib"
        "$ENV{MKLROOT}/lib/mkl_core.lib")

    add_library(laplace_core STATIC IMPORTED GLOBAL)
    include("${LAPLACE_ENGINE_BUILD}/laplace-core-static-dependencies.cmake")
    set_target_properties(laplace_core PROPERTIES
        IMPORTED_LOCATION "${LAPLACE_ENGINE_BUILD}/core/laplace_core_static.lib"
        INTERFACE_INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/../engine/core/include;${LAPLACE_CORE_STATIC_INCLUDE_DIRECTORIES}"
        INTERFACE_LINK_LIBRARIES "${LAPLACE_CORE_STATIC_DEPENDENCIES}"
        INTERFACE_LINK_OPTIONS "${LAPLACE_CORE_STATIC_LINK_OPTIONS}")

    add_library(laplace_dynamics STATIC IMPORTED GLOBAL)
    set_target_properties(laplace_dynamics PROPERTIES
        IMPORTED_LOCATION "${LAPLACE_ENGINE_BUILD}/dynamics/laplace_dynamics_pg_static.lib"
        INTERFACE_INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/../engine/dynamics/include"
        INTERFACE_LINK_LIBRARIES "${LAPLACE_MKL_STATIC}")
endif()

add_subdirectory(laplace_geom)
add_subdirectory(laplace_substrate)
