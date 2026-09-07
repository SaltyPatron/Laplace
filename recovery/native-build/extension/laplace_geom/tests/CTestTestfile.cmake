# CMake generated Testfile for 
# Source directory: /home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests
# Build directory: /home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_geom/tests
# 
# This file includes the relevant testing commands required for 
# testing this directory and lists subdirectories to be tested as well.
add_test([=[regress_setup_laplace_geom]=] "/usr/bin/bash" "-c" "\"/opt/laplace/pgsql-18/bin/dropdb\" -U laplace_admin --if-exists laplace_regress_geom && \"/opt/laplace/pgsql-18/bin/createdb\" -U laplace_admin -O laplace_admin laplace_regress_geom")
set_tests_properties([=[regress_setup_laplace_geom]=] PROPERTIES  FIXTURES_SETUP "regress_db_geom" LABELS "regress;regress_fixture" _BACKTRACE_TRIPLES "/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests/CMakeLists.txt;17;add_test;/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests/CMakeLists.txt;0;")
add_test([=[regress_teardown_laplace_geom]=] "/opt/laplace/pgsql-18/bin/dropdb" "-U" "laplace_admin" "--if-exists" "laplace_regress_geom")
set_tests_properties([=[regress_teardown_laplace_geom]=] PROPERTIES  FIXTURES_CLEANUP "regress_db_geom" LABELS "regress;regress_fixture" _BACKTRACE_TRIPLES "/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests/CMakeLists.txt;26;add_test;/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests/CMakeLists.txt;0;")
add_test([=[regress_laplace_geom]=] "/opt/laplace/pgsql-18/lib/pgxs/src/test/regress/pg_regress" "--bindir=/usr/lib/postgresql/18/bin" "--inputdir=/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests" "--outputdir=/home/ahart/Projects/Laplace-Legacy/recovery/native-build/extension/laplace_geom/tests/regress_output" "--dbname=laplace_regress_geom" "--user=laplace_admin" "--use-existing" "hash128" "st_4d" "rle_trajectory")
set_tests_properties([=[regress_laplace_geom]=] PROPERTIES  FIXTURES_REQUIRED "regress_db_geom" LABELS "regress" _BACKTRACE_TRIPLES "/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests/CMakeLists.txt;34;add_test;/home/ahart/Projects/Laplace-Legacy/extension/laplace_geom/tests/CMakeLists.txt;0;")
