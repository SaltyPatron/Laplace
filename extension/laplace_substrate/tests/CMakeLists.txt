add_executable(generated_stage_sink_pg_native_probe generated_stage_sink_native_probe.c)
target_include_directories(generated_stage_sink_pg_native_probe SYSTEM PRIVATE ${LAPLACE_PG_INCLUDEDIR_SERVER})
target_link_libraries(generated_stage_sink_pg_native_probe PRIVATE laplace_core m)
target_compile_options(generated_stage_sink_pg_native_probe PRIVATE
    -ffunction-sections -fdata-sections -Wno-unused-parameter)
target_link_options(generated_stage_sink_pg_native_probe PRIVATE "LINKER:--gc-sections")
add_test(NAME generated_stage_sink_pg_native_helpers COMMAND generated_stage_sink_pg_native_probe)
set_tests_properties(generated_stage_sink_pg_native_helpers PROPERTIES LABELS "native;persistence;physicality")

add_executable(physicality_descriptor_pg_native_probe physicality_descriptor_native_probe.c)
target_include_directories(physicality_descriptor_pg_native_probe SYSTEM PRIVATE ${LAPLACE_PG_INCLUDEDIR_SERVER})
target_link_libraries(physicality_descriptor_pg_native_probe PRIVATE laplace_core m)
target_compile_options(physicality_descriptor_pg_native_probe PRIVATE
    -ffunction-sections -fdata-sections -Wno-unused-parameter)
target_link_options(physicality_descriptor_pg_native_probe PRIVATE "LINKER:--gc-sections")
add_test(NAME physicality_descriptor_pg_native_helpers COMMAND physicality_descriptor_pg_native_probe)
set_tests_properties(physicality_descriptor_pg_native_helpers PROPERTIES LABELS "native;provider;physicality")

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

set(REGRESS_TESTS bootstrap glicko2_aggregate entities_exist_bitmap physicalities_exist_bitmap consensus_signed consensus_upsert consensus_bulk_write explore_web_neighbors attestation_merge generation_corpus cognition_completion operational_memory task_shapes steer_semantic_precedence converse relation_bands_summary chat_loop word_law identity_law schema_law structural_surface walk_richer_forward_pass chess_read chess_ranked_scope chess_player_identity_scope chess_fold_debt_repair ops_reads ops_control ingest_throughput walk_edge_weight_parity belief_distribution realize_ladder_parity perfcache_read display_label search_path_independence evict_source prompt_election apply_epoch walk_symmetry highway_mask_deposit highway_mask_recovery)
set(REGRESS_OUTPUT_DIR "${CMAKE_CURRENT_BINARY_DIR}/regress_output")
list(APPEND REGRESS_TESTS text_root_placements content_carrier_vertices physicality_descriptor_admission physicality_readback physicality_readback_cold)
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
