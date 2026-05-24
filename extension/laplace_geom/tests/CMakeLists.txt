# extension/laplace_geom/tests/CMakeLists.txt
#
# pg_regress wiring for laplace_geom (per B′.9 #196 + ADR 0042 / 0034).
#
# Drives the SQL smoke tests under `tests/sql/<name>.sql` against an
# isolated per-extension test database (`laplace_regress_geom`) so the
# user's `laplace` DB is never touched. Output is diffed against
# `tests/expected/<name>.out`; mismatches show up as a failed CTest case
# with a `regression.diffs` file under build/.../regress_output/.
#
# CTest fixtures sequence the setup/teardown around the regress run:
#   regress_setup_laplace_geom (FIXTURES_SETUP regress_db_geom)
#     → drops + recreates laplace_regress_geom DB as laplace_admin
#   regress_laplace_geom (FIXTURES_REQUIRED regress_db_geom)
#     → pg_regress --use-existing against laplace_regress_geom
#   regress_teardown_laplace_geom (FIXTURES_CLEANUP regress_db_geom)
#     → drops laplace_regress_geom DB
#
# Per-extension DBs (vs one shared `laplace_regress`) keep ctest -j safe.

# Find the pg_regress that matches the PG we built against. pgxs ships
# pg_regress under <pkglibdir>/pgxs/src/test/regress/.
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

# Fixture: create the per-extension test DB.
# dropdb + createdb (not `psql -c`) because psql wraps multi-statement -c
# invocations in a transaction, and DROP DATABASE / CREATE DATABASE cannot
# run inside a transaction.
add_test(NAME regress_setup_laplace_geom
    COMMAND bash -c
        "dropdb -U laplace_admin --if-exists ${REGRESS_DB} && createdb -U laplace_admin -O laplace_admin ${REGRESS_DB}"
)
set_tests_properties(regress_setup_laplace_geom PROPERTIES
    FIXTURES_SETUP regress_db_geom
    LABELS "regress;regress_fixture"
)

# Fixture: drop the per-extension test DB after the test run.
add_test(NAME regress_teardown_laplace_geom
    COMMAND dropdb -U laplace_admin --if-exists ${REGRESS_DB}
)
set_tests_properties(regress_teardown_laplace_geom PROPERTIES
    FIXTURES_CLEANUP regress_db_geom
    LABELS "regress;regress_fixture"
)

# The regress run itself. `--use-existing` tells pg_regress not to
# initdb a temp instance; we use the running system PG with the test DB
# the fixture just set up. `--inputdir=tests` is the dir containing
# sql/ + expected/.
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
