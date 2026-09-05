find_program(LAPLACE_PG_REGRESS pg_regress
    PATHS "${LAPLACE_PG_PKGLIBDIR}/pgxs/src/test/regress"
    NO_DEFAULT_PATH
)
if(NOT LAPLACE_PG_REGRESS)
    message(WARNING "pg_regress not found under ${LAPLACE_PG_PKGLIBDIR}/pgxs/src/test/regress — "
                    "regress tests will not be registered. Install postgresql-server-dev-${LAPLACE_PG_MAJOR}.")
    return()
endif()

set(REGRESS_DB "laplace_regress_substrate")
set(REGRESS_TESTS bootstrap glicko2_aggregate entities_exist_bitmap consensus_signed consensus_upsert explore_web_neighbors attestation_merge generation_corpus steer_semantic_precedence converse chat_loop word_law identity_law schema_law structural_surface walk_richer_forward_pass chess_read chess_player_identity_scope chess_fold_debt_repair ops_reads ops_control ingest_throughput walk_edge_weight_parity belief_distribution realize_ladder_parity display_label search_path_independence evict_source prompt_election apply_epoch walk_symmetry highway_mask_deposit)
set(REGRESS_OUTPUT_DIR "${CMAKE_CURRENT_BINARY_DIR}/regress_output")
file(MAKE_DIRECTORY "${REGRESS_OUTPUT_DIR}")

add_test(NAME regress_setup_laplace_substrate
    COMMAND ${BASH} -c
        "\"${LAPLACE_PG_BINDIR}/dropdb\" -U laplace_admin --if-exists ${REGRESS_DB} && \"${LAPLACE_PG_BINDIR}/createdb\" -U laplace_admin -O laplace_admin ${REGRESS_DB}"
)
set_tests_properties(regress_setup_laplace_substrate PROPERTIES
    FIXTURES_SETUP regress_db_substrate
    LABELS "regress;regress_fixture"
)

add_test(NAME regress_teardown_laplace_substrate
    COMMAND ${LAPLACE_PG_BINDIR}/dropdb -U laplace_admin --if-exists ${REGRESS_DB}
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
        --user=laplace_admin
        --use-existing
        ${REGRESS_TESTS}
)
set_tests_properties(regress_laplace_substrate PROPERTIES
    FIXTURES_REQUIRED regress_db_substrate
    LABELS "regress"
)
