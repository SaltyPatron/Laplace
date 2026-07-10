# Laplace geom — Windows builds liblwgeom from external/postgis with real GEOS+PROJ
# from LAPLACE_DEPS_PREFIX (scripts/win/build-deps.cmd). Linux links the prebuilt
# liblwgeom.a from just build-deps (/opt/laplace). Same external/ sources either way.

if(WIN32)
    if(NOT LAPLACE_DEPS_PREFIX)
        if(DEFINED ENV{LAPLACE_DEPS_PREFIX})
            set(LAPLACE_DEPS_PREFIX "$ENV{LAPLACE_DEPS_PREFIX}")
        else()
            set(LAPLACE_DEPS_PREFIX "D:/Data/Laplace/deps")
        endif()
    endif()
    set(LAPLACE_DEPS_PREFIX "${LAPLACE_DEPS_PREFIX}" CACHE PATH
        "Windows install prefix for geos/proj/gdal from scripts/win/build-deps.cmd")

    set(_geos_inc "${LAPLACE_DEPS_PREFIX}/geos/include")
    set(_geos_libdir "${LAPLACE_DEPS_PREFIX}/geos/lib")
    set(_proj_inc "${LAPLACE_DEPS_PREFIX}/proj/include")
    set(_proj_libdir "${LAPLACE_DEPS_PREFIX}/proj/lib")

    if(NOT EXISTS "${_geos_inc}/geos_c.h")
        message(FATAL_ERROR
            "laplace_geom: missing ${_geos_inc}/geos_c.h — run scripts\\win\\build-deps.cmd "
            "(builds external/geos into ${LAPLACE_DEPS_PREFIX}).")
    endif()
    if(NOT EXISTS "${_proj_inc}/proj.h")
        message(FATAL_ERROR
            "laplace_geom: missing ${_proj_inc}/proj.h — run scripts\\win\\build-deps.cmd "
            "(builds external/proj into ${LAPLACE_DEPS_PREFIX}).")
    endif()

    find_library(LAPLACE_LIBGEOS_C NAMES geos_c HINTS "${_geos_libdir}" NO_DEFAULT_PATH REQUIRED)
    find_library(LAPLACE_LIBGEOS NAMES geos HINTS "${_geos_libdir}" NO_DEFAULT_PATH REQUIRED)
    find_library(LAPLACE_LIBPROJ NAMES proj HINTS "${_proj_libdir}" NO_DEFAULT_PATH REQUIRED)
    find_library(LAPLACE_LIBSQLITE3 NAMES sqlite3 HINTS "${LAPLACE_DEPS_PREFIX}/sqlite/lib" NO_DEFAULT_PATH REQUIRED)

    set(POSTGIS_SRCDIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/postgis")
    set(LWGEOM_DIR "${POSTGIS_SRCDIR}/liblwgeom")

    set(POSTGIS_MAJOR_VERSION 3)
    set(POSTGIS_MINOR_VERSION 6)
    set(POSTGIS_MICRO_VERSION 3)
    set(POSTGIS_LIB_VERSION "3.6.3")
    # external/geos Version.txt = 3.12.2 → 31202; external/proj = 9.4.1 → 90401
    set(POSTGIS_GEOS_VERSION 31202)
    set(POSTGIS_PROJ_VERSION 90401)
    set(POSTGIS_PGSQL_VERSION 180)
    set(SRID_MAX 999999)
    set(SRID_USR_MAX 998999)

    configure_file("${LWGEOM_DIR}/liblwgeom.h.in"
                   "${CMAKE_CURRENT_BINARY_DIR}/lwgeom_inc/liblwgeom.h" @ONLY)
    file(COPY "${LWGEOM_DIR}/lwinline.h"
         DESTINATION "${CMAKE_CURRENT_BINARY_DIR}/lwgeom_inc")
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../../cmake/postgis_config_win.h.in"
                   "${CMAKE_CURRENT_BINARY_DIR}/postgis_config.h" @ONLY)
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../../cmake/postgis_config_win.h.in"
                   "${POSTGIS_SRCDIR}/postgis_config.h" @ONLY)

    set(LWGEOM_CORE_SOURCES
        stringbuffer.c optionlist.c stringlist.c bytebuffer.c
        measures.c measures3d.c ptarray.c lookup3.c
        lwgeom_api.c lwgeom.c lwpoint.c lwline.c lwpoly.c lwtriangle.c
        lwmpoint.c lwmline.c lwmpoly.c lwboundingcircle.c lwcollection.c
        lwcircstring.c lwcompound.c lwcurvepoly.c lwmcurve.c lwmsurface.c
        lwpsurface.c lwtin.c
        lwout_wkb.c lwin_wkb.c lwin_twkb.c lwiterator.c lwgeom_median.c
        lwout_wkt.c lwout_twkb.c lwin_wkt_parse.c lwin_wkt_lex.c lwin_wkt.c
        lwin_encoded_polyline.c lwutil.c lwhomogenize.c intervaltree.c
        lwalgorithm.c lwstroke.c lwlinearreferencing.c lwprint.c
        gbox.c gserialized.c gserialized1.c gserialized2.c
        lwgeodetic.c lwgeodetic_measures.c lwgeodetic_tree.c
        lwrandom.c lwtree.c
        lwout_gml.c lwout_kml.c lwout_geojson.c lwout_svg.c lwout_x3d.c
        lwout_encoded_polyline.c lwgeom_debug.c
        lwunionfind.c effectivearea.c lwchaikins.c lwmval.c lwkmeans.c
        varint.c lwgeom_remove_irrelevant_points_for_view.c
        # GEOS + PROJ — same objects PostGIS liblwgeom Makefile.in builds
        lwgeom_geos.c lwgeom_geos_clean.c lwgeom_geos_cluster.c
        lwgeom_geos_node.c lwgeom_geos_split.c
        lwspheroid.c lwgeom_transform.c
    )
    set(_lwgeom_srcs "")
    foreach(_s IN LISTS LWGEOM_CORE_SOURCES)
        if(EXISTS "${LWGEOM_DIR}/${_s}")
            list(APPEND _lwgeom_srcs "${LWGEOM_DIR}/${_s}")
        else()
            message(FATAL_ERROR "laplace_geom: missing liblwgeom source ${LWGEOM_DIR}/${_s}")
        endif()
    endforeach()
    list(APPEND _lwgeom_srcs "${POSTGIS_SRCDIR}/deps/ryu/d2s.c")

    add_library(lwgeom_static OBJECT ${_lwgeom_srcs})
    set_source_files_properties(
        "${LWGEOM_DIR}/lwin_wkt_parse.c"
        "${LWGEOM_DIR}/lwin_wkt_lex.c"
        "${POSTGIS_SRCDIR}/deps/ryu/d2s.c"
        PROPERTIES SKIP_UNITY_BUILD_INCLUSION ON)
    set_target_properties(lwgeom_static PROPERTIES
        UNITY_BUILD ON
        UNITY_BUILD_BATCH_SIZE 16)
    target_include_directories(lwgeom_static PUBLIC
        "${CMAKE_CURRENT_BINARY_DIR}/lwgeom_inc"
        "${CMAKE_CURRENT_BINARY_DIR}"
        "${LWGEOM_DIR}"
        "${POSTGIS_SRCDIR}"
        "${POSTGIS_SRCDIR}/deps"
        "${POSTGIS_SRCDIR}/deps/ryu"
        "${_proj_inc}"
        "${_geos_inc}"
    )
    target_compile_options(lwgeom_static PRIVATE /w
        "/FI${CMAKE_CURRENT_SOURCE_DIR}/../../cmake/win32_lwgeom_compat.h")
    # icx MSVC-mode + ryu: avoid broken mmintrin.h path (RYU_ONLY_64_BIT_OPS).
    # PROJ_DLL= : proj.h defaults to dllimport on MSVC; we link static proj.lib.
    target_compile_definitions(lwgeom_static PRIVATE
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS WIN32
        RYU_ONLY_64_BIT_OPS
        "PROJ_DLL=")

    add_library(laplace_geom MODULE src/laplace_geom.c $<TARGET_OBJECTS:lwgeom_static>)
    set_target_properties(laplace_geom PROPERTIES
        PREFIX "" OUTPUT_NAME laplace_geom SUFFIX ".dll"
        WINDOWS_EXPORT_ALL_SYMBOLS TRUE
        LINKER_LANGUAGE CXX)
    target_include_directories(laplace_geom SYSTEM PRIVATE
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32_msvc"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}"
        "${LAPLACE_PG_INCLUDEDIR}"
    )
    target_include_directories(laplace_geom PRIVATE
        "${CMAKE_CURRENT_BINARY_DIR}/lwgeom_inc"
        "${CMAKE_CURRENT_BINARY_DIR}"
        "${LWGEOM_DIR}"
        "${POSTGIS_SRCDIR}"
        "${_proj_inc}"
        "${_geos_inc}"
    )
    target_compile_definitions(laplace_geom PRIVATE
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS WIN32
        "PROJ_DLL=")
    target_link_libraries(laplace_geom PRIVATE
        laplace_core
        ${LAPLACE_LIBGEOS_C}
        ${LAPLACE_LIBGEOS}
        ${LAPLACE_LIBPROJ}
        ${LAPLACE_LIBSQLITE3}
        "${LAPLACE_PG_LIBDIR}/postgres.lib"
    )

    set(EXT_NAME laplace_geom)
    set(EXT_VERSION ${PROJECT_VERSION})

else()

set(EXT_NAME    laplace_geom)
set(EXT_VERSION ${PROJECT_VERSION})

set(EXT_C_SOURCES
    src/laplace_geom.c
)

add_library(${EXT_NAME} MODULE ${EXT_C_SOURCES})

set_target_properties(${EXT_NAME} PROPERTIES
    PREFIX ""
    OUTPUT_NAME ${EXT_NAME}
)

target_include_directories(${EXT_NAME} SYSTEM PRIVATE
    ${LAPLACE_PG_INCLUDEDIR_SERVER}
)

if(NOT LAPLACE_DEPS_PREFIX)
    set(LAPLACE_DEPS_PREFIX "/opt/laplace")
endif()
set(LAPLACE_DEPS_LWGEOM_PREFIX "${LAPLACE_DEPS_PREFIX}/pgsql-18"
    CACHE PATH "Prefix where liblwgeom.{h,a} are installed by external/CMakeLists.txt's postgis step")
find_path(LAPLACE_LIBLWGEOM_INCLUDE_DIR
    NAMES liblwgeom.h
    HINTS "${LAPLACE_DEPS_LWGEOM_PREFIX}/include"
    NO_DEFAULT_PATH
)
find_library(LAPLACE_LIBLWGEOM_STATIC
    NAMES liblwgeom.a
    HINTS "${LAPLACE_DEPS_LWGEOM_PREFIX}/lib"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_LIBLWGEOM_INCLUDE_DIR OR NOT LAPLACE_LIBLWGEOM_STATIC)
    message(FATAL_ERROR
        "laplace_geom requires liblwgeom from the PostGIS submodule build. "
        "Expected: ${LAPLACE_DEPS_LWGEOM_PREFIX}/include/liblwgeom.h + "
        "${LAPLACE_DEPS_LWGEOM_PREFIX}/lib/liblwgeom.a. "
        "Run `just build-deps` first (it produces both as part of the "
        "PostGIS ExternalProject install step in external/CMakeLists.txt). "
        "Override the lookup prefix via -DLAPLACE_DEPS_LWGEOM_PREFIX=<path>.")
endif()
message(STATUS "laplace_geom: liblwgeom.h = ${LAPLACE_LIBLWGEOM_INCLUDE_DIR}/liblwgeom.h")
message(STATUS "laplace_geom: liblwgeom.a = ${LAPLACE_LIBLWGEOM_STATIC}")
target_include_directories(${EXT_NAME} PRIVATE ${LAPLACE_LIBLWGEOM_INCLUDE_DIR})

find_path(LAPLACE_PROJ_INCLUDE_DIR
    NAMES proj.h
    HINTS "${LAPLACE_DEPS_PREFIX}/proj/include"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_PROJ_INCLUDE_DIR)
    message(FATAL_ERROR
        "laplace_geom needs proj.h (transitively pulled in by liblwgeom.h). "
        "Expected ${LAPLACE_DEPS_PREFIX}/proj/include/proj.h. Run `just build-deps`.")
endif()
target_include_directories(${EXT_NAME} PRIVATE ${LAPLACE_PROJ_INCLUDE_DIR})

find_library(LAPLACE_LIBGEOS_C geos_c
    HINTS "${LAPLACE_DEPS_PREFIX}/geos/lib"
    NO_DEFAULT_PATH)
find_library(LAPLACE_LIBGEOS geos
    HINTS "${LAPLACE_DEPS_PREFIX}/geos/lib"
    NO_DEFAULT_PATH)
find_library(LAPLACE_LIBPROJ proj
    HINTS "${LAPLACE_DEPS_PREFIX}/proj/lib"
    NO_DEFAULT_PATH)
if(NOT LAPLACE_LIBGEOS_C OR NOT LAPLACE_LIBGEOS OR NOT LAPLACE_LIBPROJ)
    message(FATAL_ERROR
        "laplace_geom needs libgeos_c + libgeos (transitive: "
        "liblwgeom GEOS algorithms) + libproj (transitive: liblwgeom "
        "spheroid math). Expected under ${LAPLACE_DEPS_PREFIX}/geos|proj. "
        "Run `just build-deps`.")
endif()

target_link_libraries(${EXT_NAME} PRIVATE
    laplace_core
    ${LAPLACE_LIBLWGEOM_STATIC}
    ${LAPLACE_LIBGEOS_C}
    ${LAPLACE_LIBGEOS}
    ${LAPLACE_LIBPROJ}
)
target_link_options(${EXT_NAME} PRIVATE
    "-Wl,-rpath,$<TARGET_FILE_DIR:laplace_core>"
)

endif()

if(NOT EXT_NAME)
    set(EXT_NAME laplace_geom)
endif()
if(NOT EXT_VERSION)
    set(EXT_VERSION ${PROJECT_VERSION})
endif()

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/${EXT_NAME}.control.in
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    @ONLY
)

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/sql/sqldefines.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    @ONLY
)

include(${CMAKE_CURRENT_SOURCE_DIR}/../laplace_substrate/cmake/SqlManifest.cmake)
laplace_write_sql_chain(
    "${CMAKE_CURRENT_BINARY_DIR}/sql/generated/install_chain.sql.in"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install"
    "${CMAKE_CURRENT_SOURCE_DIR}/sql")
set(_geom_install_deps ${SQL_CHAIN_DEPS})

set(EXT_SQL_MODULES
    sql/laplace_geom.sql.in
    sql/manifest.install
    ${_geom_install_deps}
)

set(EXT_BUILT_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}--${EXT_VERSION}.sql)
set(EXT_TMP_SQL ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.tmp)

add_custom_command(
    OUTPUT  ${EXT_BUILT_SQL}
    DEPENDS ${EXT_SQL_MODULES}
            ${CMAKE_CURRENT_BINARY_DIR}/sql/sqldefines.h
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND ${LAPLACE_SQLPP} ${LAPLACE_SQLPP_FLAGS}
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_BINARY_DIR}/sql
            -I${CMAKE_CURRENT_BINARY_DIR}/sql/generated
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/${EXT_NAME}.sql.in
            > ${EXT_TMP_SQL}
    COMMAND ${BASH} -c "grep -v '^#' ${EXT_TMP_SQL} \
            | perl -lpe \"s'MODULE_PATHNAME'\\\$libdir/${EXT_NAME}'g\" \
            | perl -lpe \"s'\@extschema\@\\\\.''g\" \
            > ${EXT_BUILT_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_TMP_SQL}
    COMMENT "Preprocessing ${EXT_NAME} .sql.in modules → ${EXT_NAME}--${EXT_VERSION}.sql"
    VERBATIM
)

add_custom_target(${EXT_NAME}_sql ALL DEPENDS ${EXT_BUILT_SQL})
add_dependencies(${EXT_NAME} ${EXT_NAME}_sql)

if(NOT WIN32)
install(TARGETS ${EXT_NAME}
    LIBRARY
        DESTINATION ${LAPLACE_EXT_LIBDIR}
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
)
install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/${EXT_NAME}.control
    ${EXT_BUILT_SQL}
    DESTINATION ${LAPLACE_EXT_SHAREDIR}
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

endif()

if(BUILD_TESTING AND NOT WIN32)
    add_subdirectory(tests)
endif()
