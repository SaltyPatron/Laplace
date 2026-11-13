add_executable(physicality_readback_pg_native_probe physicality_readback_native_probe.c)
target_include_directories(physicality_readback_pg_native_probe SYSTEM PRIVATE ${LAPLACE_PG_INCLUDEDIR_SERVER})
target_link_libraries(physicality_readback_pg_native_probe PRIVATE laplace_core m)
target_compile_options(physicality_readback_pg_native_probe PRIVATE
    -ffunction-sections -fdata-sections -Wno-unused-parameter)
target_link_options(physicality_readback_pg_native_probe PRIVATE "LINKER:--gc-sections")
add_test(NAME physicality_readback_pg_native_helpers COMMAND physicality_readback_pg_native_probe)
set_tests_properties(physicality_readback_pg_native_helpers PROPERTIES LABELS "native;readback;physicality")

find_program(LAPLACE_PG_REGRESS pg_regress
    PATHS "${LAPLACE_PG_PKGLIBDIR}/pgxs/src/test/regress"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_PG_REGRESS)
    message(WARNING "pg_regress not found under ${LAPLACE_PG_PKGLIBDIR}/pgxs/src/test/regress — "
                    "regress tests will not be registered. Install postgresql-server-dev-${LAPLACE_PG_MAJOR}.")
    return()
endif()

# Database QA must be able to run an exact branch build without touching the
# canonical database or colliding with another proof. PR proof supplies a
# run-scoped stem before configure; ordinary local/main builds retain the stable
# historical regression database name.
if(DEFINED ENV{LAPLACE_REGRESS_DB} AND NOT "$ENV{LAPLACE_REGRESS_DB}" STREQUAL "")
    set(REGRESS_DB "$ENV{LAPLACE_REGRESS_DB}_substrate")
else()
    set(REGRESS_DB "laplace_regress_substrate")
endif()
if(NOT REGRESS_DB MATCHES "^laplace_[A-Za-z0-9_]+$")
    message(FATAL_ERROR "LAPLACE_REGRESS_DB must produce an isolated laplace_* database name")
endif()
# The role that owns the scratch regression database on the installed server (the runner's own role in CI).
if(DEFINED ENV{LAPLACE_REGRESS_USER} AND NOT "$ENV{LAPLACE_REGRESS_USER}" STREQUAL "")
    set(REGRESS_USER "$ENV{LAPLACE_REGRESS_USER}")
else()
    set(REGRESS_USER laplace_admin)
endif()

set(REGRESS_TESTS bootstrap journal_batch glicko2_aggregate entities_exist_bitmap entity_identity_storage physicalities_exist_bitmap consensus_signed consensus_upsert consensus_bulk_write explore_web_neighbors attestation_merge generation_corpus cognition_completion couple_ordered_continuation couple_standing_state couple_provenance_state operational_memory task_shapes steer_semantic_precedence converse chat_loop word_law identity_law identity_native schema_law structural_surface walk_richer_forward_pass ops_reads source_catalog native_claim_leaders ops_control ingest_throughput walk_edge_weight_parity belief_distribution realize_ladder_parity perfcache_read display_label search_path_independence evict_source prompt_election apply_epoch walk_symmetry highway_mask_deposit highway_mask_recovery concept_ref)
set(REGRESS_OUTPUT_DIR "${CMAKE_CURRENT_BINARY_DIR}/regress_output")
list(APPEND REGRESS_TESTS text_root_placements content_carrier_vertices forward_seed_parity physicality_readback physicality_readback_cold leaf_swap_routing relation_band_counts inspection_display)
file(MAKE_DIRECTORY "${REGRESS_OUTPUT_DIR}")
list(APPEND REGRESS_TESTS unicode_source_predicates consensus_evidence_batch render_depth)

add_test(NAME regress_setup_laplace_substrate
    COMMAND ${BASH} -c
        "\"${LAPLACE_PG_BINDIR}/dropdb\" -U ${REGRESS_USER} --if-exists ${REGRESS_DB} && \"${LAPLACE_PG_BINDIR}/createdb\" -U ${REGRESS_USER} -O ${REGRESS_USER} ${REGRESS_DB}"
)
set_tests_properties(regress_setup_laplace_substrate PROPERTIES
    FIXTURES_SETUP regress_db_substrate
    LABELS "regress;regress_fixture"
)

add_test(NAME regress_teardown_laplace_substrate
    COMMAND ${LAPLACE_PG_BINDIR}/dropdb -U ${REGRESS_USER} --if-exists ${REGRESS_DB}
)
set_tests_properties(regress_teardown_laplace_substrate PROPERTIES
    FIXTURES_CLEANUP regress_db_substrate
    LABELS "regress;regress_fixture"
)

add_test(NAME regress_laplace_substrate
    COMMAND ${LAPLACE_PG_REGRESS}
        --bindir=${LAPLACE_PG_PREFIX}/bin
        --inputdir=${CMAKE_CURRENT_SOURCE_DIR}
        --outputdir=${REGRESS_OUTPUT_DIR}
        --dbname=${REGRESS_DB}
        --user=${REGRESS_USER}
        --use-existing
        ${REGRESS_TESTS}
)
set_tests_properties(regress_laplace_substrate PROPERTIES
    FIXTURES_REQUIRED regress_db_substrate
    LABELS "regress"
)

# Closed-world psql cases: each builds its own fixture, asserts exact results over it and rolls back, so it needs
# a database holding only that fixture. They run in the same installed-server regression database as the suite above.
foreach(case native_consensus_endpoints prompt_segments session_identity declared_paths_input)
    add_test(NAME regress_psql_${case}
        COMMAND ${LAPLACE_PG_BINDIR}/psql -X --no-password -U ${REGRESS_USER} --dbname=${REGRESS_DB}
            --set=ON_ERROR_STOP=1 --file=${CMAKE_CURRENT_SOURCE_DIR}/sql/${case}.sql)
    set_tests_properties(regress_psql_${case} PROPERTIES FIXTURES_REQUIRED regress_db_substrate
        DEPENDS regress_laplace_substrate LABELS "regress")
endforeach()

# A satisfied runtime ACL declaration must replay without locking its tables: a holder keeps ACCESS EXCLUSIVE on
# public.entity while the declaration every delivery applies is replayed under a 2 s lock timeout. Run here, not on a
# live substrate, where the holder would queue every reader and load behind it.
set(_grants "${CMAKE_SOURCE_DIR}/db/schema/20_substrate_grants.sql")
add_test(NAME regress_grants_locked_replay
    COMMAND ${BASH} -c "set -euo pipefail
psql() { \"${LAPLACE_PG_BINDIR}/psql\" -X --no-password -U ${REGRESS_USER} --dbname=${REGRESS_DB} -v ON_ERROR_STOP=1 \"\$@\"; }
psql -q -f '${_grants}'
coproc holder { psql -Atq; }
echo \"BEGIN; LOCK TABLE public.entity IN ACCESS EXCLUSIVE MODE; SELECT 'locked';\" >&\${holder[1]}
read -r -t 30 line <&\${holder[0]}; [[ \$line == locked ]]
status=0; PGOPTIONS='-c lock_timeout=2s -c statement_timeout=10s' psql -q -f '${_grants}' || status=\$?
echo 'ROLLBACK;' >&\${holder[1]}; exec {holder[1]}>&-; wait \$holder_PID || true
exit \$status")
set_tests_properties(regress_grants_locked_replay PROPERTIES FIXTURES_REQUIRED regress_db_substrate
    DEPENDS regress_laplace_substrate LABELS "regress")
