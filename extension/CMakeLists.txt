if(WIN32 AND CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    cmake_minimum_required(VERSION 3.20)
    project(laplace_extensions VERSION 0.1.0 LANGUAGES C CXX)

    find_program(BASH bash
        HINTS "C:/Program Files/Git/bin"
        DOC "Git bash for SQL postprocess (grep/perl)")
    if(NOT BASH)
        message(FATAL_ERROR "Git bash not found — required for extension SQL postprocess")
    endif()

    # build-extensions.cmd sets CMAKE_C_COMPILER=icx; use the same tool for .sql.in (PostGIS/PGXS model).
    get_filename_component(LAPLACE_SQLPP "${CMAKE_C_COMPILER}" ABSOLUTE)
    set(LAPLACE_SQLPP_FLAGS -E -traditional-cpp -w -Upixel -Ubool)
    message(STATUS "Laplace SQLPP: ${LAPLACE_SQLPP}")
    message(STATUS "Laplace SQL bash: ${BASH}")

    find_program(LAPLACE_PG_CONFIG pg_config
        HINTS "C:/Program Files/PostgreSQL/18/bin")
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
    message(STATUS "PG server includes: ${LAPLACE_PG_INCLUDEDIR_SERVER}")
    message(STATUS "PG pkglibdir:       ${LAPLACE_PG_PKGLIBDIR}")

    set(LAPLACE_ENGINE_BUILD "${CMAKE_CURRENT_SOURCE_DIR}/../build-win"
        CACHE PATH "Engine build tree holding laplace_core/dynamics static libs")
    foreach(_eng core dynamics)
        if(NOT EXISTS "${LAPLACE_ENGINE_BUILD}/${_eng}/laplace_${_eng}_static.lib")
            message(FATAL_ERROR "Missing ${LAPLACE_ENGINE_BUILD}/${_eng}/laplace_${_eng}_static.lib — build the engine first (scripts\\win\\build-engine.cmd)")
        endif()
    endforeach()
    if(NOT EXISTS "${LAPLACE_ENGINE_BUILD}/dynamics/laplace_dynamics_pg_static.lib")
        message(FATAL_ERROR "Missing ${LAPLACE_ENGINE_BUILD}/dynamics/laplace_dynamics_pg_static.lib — build the engine first (scripts\\win\\build-engine.cmd)")
    endif()

    if(NOT DEFINED ENV{MKLROOT})
        message(FATAL_ERROR "MKLROOT not set — run via scripts\\win\\build-extensions.cmd (env.cmd chain). "
            "Do NOT cmake --install to C:\\Program Files\\PostgreSQL; deploy with scripts\\win\\install-extensions.cmd "
            "(see .github/instructions/build-environment.instructions.md)")
    endif()
    set(LAPLACE_MKL_STATIC
        "$ENV{MKLROOT}/lib/mkl_intel_lp64.lib"
        "$ENV{MKLROOT}/lib/mkl_sequential.lib"
        "$ENV{MKLROOT}/lib/mkl_core.lib")

    add_library(laplace_core STATIC IMPORTED GLOBAL)
    set_target_properties(laplace_core PROPERTIES
        IMPORTED_LOCATION "${LAPLACE_ENGINE_BUILD}/core/laplace_core_static.lib"
        INTERFACE_INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/../engine/core/include"
        INTERFACE_LINK_LIBRARIES "${LAPLACE_ENGINE_BUILD}/_deps/blake3/blake3.lib;C:/Program Files/PostgreSQL/18/lib/libxml2.lib")

    add_library(laplace_dynamics STATIC IMPORTED GLOBAL)
    set_target_properties(laplace_dynamics PROPERTIES
        IMPORTED_LOCATION "${LAPLACE_ENGINE_BUILD}/dynamics/laplace_dynamics_pg_static.lib"
        INTERFACE_INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/../engine/dynamics/include"
        INTERFACE_LINK_LIBRARIES "${LAPLACE_MKL_STATIC}")
endif()

add_subdirectory(laplace_geom)
add_subdirectory(laplace_substrate)
