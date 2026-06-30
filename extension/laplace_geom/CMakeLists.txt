if(WIN32)
    set(POSTGIS_SRCDIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/postgis")
    set(LWGEOM_DIR "${POSTGIS_SRCDIR}/liblwgeom")
    set(PROJ_SRCDIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/proj")

    set(POSTGIS_MAJOR_VERSION 3)
    set(POSTGIS_MINOR_VERSION 6)
    set(POSTGIS_MICRO_VERSION 3)
    set(POSTGIS_LIB_VERSION "3.6.3")
    set(POSTGIS_GEOS_VERSION "")
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
    )
    set(_lwgeom_srcs "")
    foreach(_s IN LISTS LWGEOM_CORE_SOURCES)
        if(EXISTS "${LWGEOM_DIR}/${_s}")
            list(APPEND _lwgeom_srcs "${LWGEOM_DIR}/${_s}")
        endif()
    endforeach()
    list(APPEND _lwgeom_srcs "${POSTGIS_SRCDIR}/deps/ryu/d2s.c")
    list(APPEND _lwgeom_srcs "${CMAKE_CURRENT_SOURCE_DIR}/src/lwgeom_win32_stubs.c")

    add_library(lwgeom_static STATIC ${_lwgeom_srcs})
    target_include_directories(lwgeom_static PUBLIC
        "${CMAKE_CURRENT_BINARY_DIR}/lwgeom_inc"
        "${CMAKE_CURRENT_BINARY_DIR}"
        "${LWGEOM_DIR}"
        "${POSTGIS_SRCDIR}"
        "${POSTGIS_SRCDIR}/deps"
        "${POSTGIS_SRCDIR}/deps/ryu"
        "${PROJ_SRCDIR}/src"
    )
    target_compile_options(lwgeom_static PRIVATE /w
        "/FI${CMAKE_CURRENT_SOURCE_DIR}/../../cmake/win32_lwgeom_compat.h")
    target_compile_definitions(lwgeom_static PRIVATE
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS WIN32)

    add_library(laplace_geom MODULE src/laplace_geom.c)
    set_target_properties(laplace_geom PROPERTIES
        PREFIX "" OUTPUT_NAME laplace_geom SUFFIX ".dll"
        WINDOWS_EXPORT_ALL_SYMBOLS TRUE)
    target_include_directories(laplace_geom PRIVATE
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32_msvc"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}/port/win32"
        "${LAPLACE_PG_INCLUDEDIR_SERVER}"
        "${LAPLACE_PG_INCLUDEDIR}"
    )
    target_compile_definitions(laplace_geom PRIVATE
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS WIN32)
    target_link_libraries(laplace_geom PRIVATE
        laplace_core
        lwgeom_static
        "${LAPLACE_PG_LIBDIR}/postgres.lib"
    )
    return()
endif()

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

target_include_directories(${EXT_NAME} PRIVATE
    ${LAPLACE_PG_INCLUDEDIR_SERVER}
)

set(LAPLACE_DEPS_LWGEOM_PREFIX "/opt/laplace/pgsql-18"
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
    HINTS "/opt/laplace/proj/include"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_PROJ_INCLUDE_DIR)
    message(FATAL_ERROR
        "laplace_geom needs proj.h (transitively pulled in by liblwgeom.h). "
        "Expected /opt/laplace/proj/include/proj.h. Run `just build-deps`.")
endif()
target_include_directories(${EXT_NAME} PRIVATE ${LAPLACE_PROJ_INCLUDE_DIR})

find_library(LAPLACE_LIBGEOS_C geos_c
    HINTS "/opt/laplace/geos/lib"
    NO_DEFAULT_PATH)
find_library(LAPLACE_LIBGEOS geos
    HINTS "/opt/laplace/geos/lib"
    NO_DEFAULT_PATH)
find_library(LAPLACE_LIBPROJ proj
    HINTS "/opt/laplace/proj/lib"
    NO_DEFAULT_PATH)
if(NOT LAPLACE_LIBGEOS_C OR NOT LAPLACE_LIBGEOS OR NOT LAPLACE_LIBPROJ)
    message(FATAL_ERROR
        "laplace_geom needs libgeos_c.so + libgeos.so (transitive: "
        "liblwgeom GEOS algorithms) + libproj.so (transitive: liblwgeom "
        "spheroid math). Expected libgeos*.so in /opt/laplace/geos/lib/ "
        "and libproj.so in /opt/laplace/proj/lib/. Run `just build-deps`.")
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
    COMMAND ${LAPLACE_SQLPP} -traditional-cpp -w -P -Upixel -Ubool
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            -I${CMAKE_CURRENT_BINARY_DIR}/sql
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/${EXT_NAME}.sql.in
            > ${EXT_TMP_SQL}
    COMMAND bash -c "grep -v '^#' ${EXT_TMP_SQL} \
            | perl -lpe \"s'MODULE_PATHNAME'\\\$libdir/${EXT_NAME}'g\" \
            | perl -lpe \"s'\@extschema\@\\\\.''g\" \
            > ${EXT_BUILT_SQL}"
    COMMAND ${CMAKE_COMMAND} -E remove ${EXT_TMP_SQL}
    COMMENT "Preprocessing ${EXT_NAME} .sql.in modules → ${EXT_NAME}--${EXT_VERSION}.sql"
    VERBATIM
)

add_custom_target(${EXT_NAME}_sql ALL DEPENDS ${EXT_BUILT_SQL})
add_dependencies(${EXT_NAME} ${EXT_NAME}_sql)

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

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
