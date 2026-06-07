find_program(LAPLACE_PG_REGRESS pg_regress
    PATHS "${LAPLACE_PG_PKGLIBDIR}/pgxs/src/test/regress"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_PG_REGRESS)
    message(WARNING "pg_regress not found under ${LAPLACE_PG_PKGLIBDIR}/pgxs/src/test/regress — "
                    "regress tests will not be registered. Install postgresql-server-dev-${LAPLACE_PG_MAJOR}.")
    return()
endif()
message(STATUS "Laplace pg_regress: ${LAPLACE_PG_REGRESS}")

set(REGRESS_DB "laplace_regress_geom")
set(REGRESS_TESTS hash128 st_4d)
set(REGRESS_OUTPUT_DIR "${CMAKE_CURRENT_BINARY_DIR}/regress_output")
file(MAKE_DIRECTORY "${REGRESS_OUTPUT_DIR}")

add_test(NAME regress_setup_laplace_geom
    COMMAND bash -c
        "dropdb -U laplace_admin --if-exists ${REGRESS_DB} && createdb -U laplace_admin -O laplace_admin ${REGRESS_DB}"
)
set_tests_properties(regress_setup_laplace_geom PROPERTIES
    FIXTURES_SETUP regress_db_geom
    LABELS "regress;regress_fixture"
)

add_test(NAME regress_teardown_laplace_geom
    COMMAND dropdb -U laplace_admin --if-exists ${REGRESS_DB}
)
set_tests_properties(regress_teardown_laplace_geom PROPERTIES
    FIXTURES_CLEANUP regress_db_geom
    LABELS "regress;regress_fixture"
)

add_test(NAME regress_laplace_geom
    COMMAND ${LAPLACE_PG_REGRESS}
        --bindir=${LAPLACE_PG_PREFIX}/bin
        --inputdir=${CMAKE_CURRENT_SOURCE_DIR}
        --outputdir=${REGRESS_OUTPUT_DIR}
        --dbname=${REGRESS_DB}
        --user=laplace_admin
        --use-existing
        ${REGRESS_TESTS}
)
set_tests_properties(regress_laplace_geom PROPERTIES
    FIXTURES_REQUIRED regress_db_geom
    LABELS "regress"
)
