/*
 * trajectory_generate_couple.c — Trace tuplestore emitters and the initial
 * RESOLVE / COUPLE / ORIENT phase of forward trajectory generation.
 */
#include "trajectory_generate_internal.h"

static void
put_cognition_receipt(Datum *values, bool *nulls, int base,
                      const LaplaceCognitionProgram *program)
{
    LaplaceCognitionProgramReceipt receipt;
    laplace_cognition_program_receipt(program, &receipt);
    values[base + 0] = hash128_to_datum(&receipt.program_id);
    values[base + 1] = Int32GetDatum(receipt.required_obligations);
    values[base + 2] = Int32GetDatum(receipt.satisfied_obligations);
    values[base + 3] = Int32GetDatum(receipt.remaining_required);
    values[base + 4] = BoolGetDatum(receipt.complete);
    values[base + 5] = CStringGetTextDatum(
        laplace_cognition_disposition_name(receipt.disposition));
    if (receipt.output_present)
        values[base + 6] = hash128_to_datum(&receipt.output_fingerprint);
    else
        nulls[base + 6] = true;
    if (receipt.semantic_act_present)
        values[base + 7] = hash128_to_datum(&receipt.semantic_act_id);
    else
        nulls[base + 7] = true;
    values[base + 8] = Int32GetDatum(receipt.output_count);
}

static void
free_cognition_receipt(Datum *values, bool *nulls, int base)
{
    pfree(DatumGetPointer(values[base + 0]));
    pfree(DatumGetPointer(values[base + 5]));
    if (!nulls[base + 6]) pfree(DatumGetPointer(values[base + 6]));
    if (!nulls[base + 7]) pfree(DatumGetPointer(values[base + 7]));
}

void
laplace_tg_emit_stage(ReturnSetInfo *result, int32 step, const char *event,
                      const LaplacePromptInput *input, int operand_count,
                      int candidate_count, int proposal_channels, int exact_channels,
                      int routing_round, const LaplaceCognitionProgram *program)
{
    Datum values[33] = {0};
    bool nulls[33];

    for (int i = 0; i < 33; ++i) nulls[i] = true;
    values[0] = Int32GetDatum(step); nulls[0] = false;
    if (input)
    {
        values[4] = hash128_to_datum(&input->root);
        nulls[4] = false;
    }
    values[5] = Int32GetDatum(candidate_count); nulls[5] = false;
    values[6] = Int32GetDatum(operand_count); nulls[6] = false;
    values[7] = Int32GetDatum(proposal_channels); nulls[7] = false;
    values[8] = Int32GetDatum(exact_channels); nulls[8] = false;
    values[9] = Int64GetDatum(0); nulls[9] = false;
    values[10] = Int32GetDatum(0); nulls[10] = false;
    values[11] = Int32GetDatum(0); nulls[11] = false;
    values[12] = Int32GetDatum(0); nulls[12] = false;
    values[19] = Int32GetDatum(0); nulls[19] = false;
    values[20] = Int32GetDatum(0); nulls[20] = false;
    values[21] = BoolGetDatum(false); nulls[21] = false;
    values[22] = CStringGetTextDatum(event); nulls[22] = false;
    values[23] = Int32GetDatum(routing_round); nulls[23] = false;

    if (program)
    {
        for (int i = 24; i < 33; ++i) nulls[i] = false;
        put_cognition_receipt(values, nulls, 24, program);
    }

    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    if (!nulls[4]) pfree(DatumGetPointer(values[4]));
    pfree(DatumGetPointer(values[22]));
    if (program) free_cognition_receipt(values, nulls, 24);
}

void
laplace_tg_emit_result(ReturnSetInfo *result, int32 step, const Candidate *candidate,
                       bool trace, const LaplacePromptInput *input, int candidate_count,
                       int operand_count, int query_channels, int exact_channels,
                       bool routing, int routing_round,
                       const LaplaceCognitionProgram *program)
{
    Datum values[33] = {0};
    bool nulls[33] = {false, false, false, true};
    Datum id = hash128_to_datum(&candidate->id);
    values[0] = Int32GetDatum(step);
    values[1] = id;
    values[2] = Int32GetDatum(candidate->stride);
    values[3] = (Datum) 0;
    if (trace)
    {
        const EvidenceSummary *support = candidate->projection.has_positive
            ? &candidate->projection : candidate->query_traversal.has_positive
            ? &candidate->query_traversal : &candidate->query;
        const LaplaceQueryChannel *channel = &support->positive;
        values[4] = hash128_to_datum(&input->root);
        values[5] = Int32GetDatum(candidate_count);
        values[6] = Int32GetDatum(operand_count);
        values[7] = Int32GetDatum(query_channels);
        values[8] = Int32GetDatum(exact_channels);
        values[9] = Int64GetDatum(candidate->sequence_occurrences);
        values[10] = Int32GetDatum(candidate->query.positive_covered_occurrences);
        values[11] = Int32GetDatum(candidate->query.positive_relation_families);
        values[12] = Int32GetDatum(candidate->query.negative_covered_occurrences);
        if (support->has_positive)
        {
            values[13] = hash128_to_datum(&channel->anchor);
            values[14] = hash128_to_datum(&channel->relation);
            values[15] = BoolGetDatum(channel->outbound);
            values[16] = Int64GetDatum(channel->rating);
            values[17] = Int64GetDatum(channel->rd);
            values[18] = Int64GetDatum(channel->witnesses);
            values[19] = Int32GetDatum(channel->distinct_sources);
            values[20] = Int32GetDatum(channel->distinct_contexts);
        }
        else
            for (int i = 13; i <= 20; ++i) nulls[i] = true;
        values[21] = BoolGetDatum(candidate->projection.has_positive);
        values[22] = CStringGetTextDatum(routing ? "route" : "emit");
        values[23] = Int32GetDatum(routing_round);
        put_cognition_receipt(values, nulls, 24, program);
    }
    else
    {
        for (int i = 4; i < 33; ++i) nulls[i] = true;
    }
    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    pfree(DatumGetPointer(id));
    if (trace)
    {
        pfree(DatumGetPointer(values[4]));
        if (!nulls[13]) pfree(DatumGetPointer(values[13]));
        if (!nulls[14]) pfree(DatumGetPointer(values[14]));
        pfree(DatumGetPointer(values[22]));
        free_cognition_receipt(values, nulls, 24);
    }
}

void
laplace_tg_emit_terminal(ReturnSetInfo *result, int32 step, const LaplacePromptInput *input,
                         int operand_count, int routing_round,
                         const LaplaceCognitionProgram *program)
{
    Datum values[33] = {0};
    bool nulls[33] = {false};
    LaplaceCognitionProgramReceipt receipt;

    for (int i = 0; i < 33; ++i) nulls[i] = true;
    laplace_cognition_program_receipt(program, &receipt);
    values[0] = Int32GetDatum(step); nulls[0] = false;
    values[4] = hash128_to_datum(&input->root); nulls[4] = false;
    values[5] = Int32GetDatum(0); nulls[5] = false;
    values[6] = Int32GetDatum(operand_count); nulls[6] = false;
    values[7] = Int32GetDatum(0); nulls[7] = false;
    values[8] = Int32GetDatum(0); nulls[8] = false;
    values[9] = Int64GetDatum(0); nulls[9] = false;
    values[10] = Int32GetDatum(0); nulls[10] = false;
    values[11] = Int32GetDatum(0); nulls[11] = false;
    values[12] = Int32GetDatum(0); nulls[12] = false;
    values[19] = Int32GetDatum(0); nulls[19] = false;
    values[20] = Int32GetDatum(0); nulls[20] = false;
    values[21] = BoolGetDatum(false); nulls[21] = false;
    values[22] = CStringGetTextDatum(receipt.complete ? "complete" : "unresolved");
    nulls[22] = false;
    values[23] = Int32GetDatum(routing_round); nulls[23] = false;
    for (int i = 24; i < 33; ++i) nulls[i] = false;
    put_cognition_receipt(values, nulls, 24, program);
    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    pfree(DatumGetPointer(values[4]));
    pfree(DatumGetPointer(values[22]));
    free_cognition_receipt(values, nulls, 24);
}

void
laplace_tg_couple_and_orient(
    FunctionCallInfo fcinfo, ReturnSetInfo *result,
    const LaplacePromptInput *input, MemoryContext walk_context,
    ArrayType *operands, ArrayType *relation_types, ArrayType *output_relations,
    ArrayType *discourse, const hash128_t *invocation_context,
    int32 max_stride, int32 fanout, int semantic_hop_limit,
    int context_length, bool trace, int routing_hops,
    HTAB *origins, HTAB *route_seen,
    LaplaceQueryState *query_state, LaplaceQueryState **output_state_out,
    LaplaceTrajectoryScope **trajectory_scope_out,
    LaplacePromptIntent *coupled_intent_out,
    const LaplacePromptIntent **intent_inout,
    LaplaceCognitionProgram **cognition_out,
    int *coupling_hops_out, bool *explicit_observation_scope_out)
{
    LaplaceTrajectoryScope *trajectory_scope = NULL;
    LaplaceQueryState *output_state = NULL;
    LaplaceCognitionProgram *cognition = NULL;
    const LaplacePromptIntent *intent = *intent_inout;
    int coupling_hops = 0;
    bool explicit_observation_scope = false;

    if (PG_NARGS() > 9 && !PG_ARGISNULL(9) && max_stride > 0)
    {
        explicit_observation_scope = true;
        trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend(trajectory_scope, PG_GETARG_ARRAYTYPE_P(9));
    }
    if (PG_NARGS() > 10 && !PG_ARGISNULL(10) && max_stride > 0)
    {
        explicit_observation_scope = true;
        if (!trajectory_scope)
            trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend_containing(trajectory_scope,
                                                   PG_GETARG_ARRAYTYPE_P(10));
    }
    if (input && max_stride > 0)
    {
        if (!trajectory_scope)
            trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_bind_input(trajectory_scope, input);
    }

    if (trace && input)
    {
        int resolved_channels = 0;
        (void) laplace_query_state_channels_unbound(query_state, &resolved_channels);
        laplace_tg_emit_stage(result, 0, "resolve", input, context_length, 0,
                              resolved_channels, 0, 0, NULL);
    }

    if (input)
    {
        *coupled_intent_out = laplace_prompt_intent_begin(
            input, walk_context, relation_types, fanout, trajectory_scope);
        laplace_prompt_intent_bind_discourse(coupled_intent_out, discourse);

        if (coupled_intent_out->structural_frontier &&
            ArrayGetNItems(ARR_NDIM(coupled_intent_out->structural_frontier),
                           ARR_DIMS(coupled_intent_out->structural_frontier)) > 0)
        {
            for (int i = 0; i < coupled_intent_out->structural_count; ++i)
            {
                const LaplaceStructuralCandidate *crossing =
                    &coupled_intent_out->structural[i];
                if (crossing->relation_mask == LAPLACE_STRUCTURAL_CONTINUATION)
                    continue;
                laplace_tg_origin_add_occurrences(
                    origins, &crossing->id,
                    laplace_prompt_intent_origins(coupled_intent_out, &crossing->source),
                    walk_context);
            }
            ArrayBuildState *structural_new = NULL;
            ArrayIterator structural_iterator =
                array_create_iterator(coupled_intent_out->structural_frontier, 0, NULL);
            Datum structural_value;
            bool structural_null;
            while (array_iterate(structural_iterator, &structural_value, &structural_null))
            {
                if (structural_null) continue;
                hash128_t id = datum_to_hash128(structural_value);
                bool found;
                hash_search(route_seen, &id, HASH_ENTER, &found);
                if (!found)
                    structural_new = accumArrayResult(
                        structural_new, structural_value, false, BYTEAOID, walk_context);
            }
            array_free_iterator(structural_iterator);
            if (structural_new)
            {
                ArrayType *structural_ids =
                    DatumGetArrayTypeP(makeArrayResult(structural_new, walk_context));
                laplace_query_state_extend_batch_role(
                    query_state, structural_ids,
                    LAPLACE_QUERY_OPERAND_PHYSICALITY, NULL);
                pfree(structural_ids);
            }
        }

        if (coupled_intent_out->geometry_frontier &&
            ArrayGetNItems(ARR_NDIM(coupled_intent_out->geometry_frontier),
                           ARR_DIMS(coupled_intent_out->geometry_frontier)) > 0)
        {
            ArrayBuildState *geometry_new = NULL;
            ArrayIterator geometry_iterator =
                array_create_iterator(coupled_intent_out->geometry_frontier, 0, NULL);
            Datum geometry_value;
            bool geometry_null;
            while (array_iterate(geometry_iterator, &geometry_value, &geometry_null))
            {
                if (geometry_null) continue;
                hash128_t id = datum_to_hash128(geometry_value);
                bool found;
                hash_search(route_seen, &id, HASH_ENTER, &found);
                laplace_tg_origin_add_occurrences(origins, &id,
                    laplace_prompt_intent_origins(
                        coupled_intent_out, &coupled_intent_out->root), walk_context);
                if (!found)
                    geometry_new = accumArrayResult(
                        geometry_new, geometry_value, false, BYTEAOID, walk_context);
            }
            array_free_iterator(geometry_iterator);
            if (geometry_new)
            {
                ArrayType *geometry_ids =
                    DatumGetArrayTypeP(makeArrayResult(geometry_new, walk_context));
                laplace_query_state_extend_batch_role(
                    query_state, geometry_ids,
                    LAPLACE_QUERY_OPERAND_GEOMETRY, NULL);
                pfree(geometry_ids);
            }
        }

        for (;;)
        {
            int channel_count = 0;
            const LaplaceQueryChannel *channels =
                laplace_query_state_channels_unbound(query_state, &channel_count);
            ArrayType *changed = laplace_prompt_intent_couple(
                coupled_intent_out, channels, channel_count);
            int changed_count = ArrayGetNItems(ARR_NDIM(changed), ARR_DIMS(changed));
            if (changed_count == 0 ||
                (semantic_hop_limit >= 0 && coupling_hops >= semantic_hop_limit))
            {
                pfree(changed);
                break;
            }
            ArrayBuildState *next = NULL;
            ArrayIterator changed_iterator = array_create_iterator(changed, 0, NULL);
            Datum changed_value;
            bool changed_null;
            while (array_iterate(changed_iterator, &changed_value, &changed_null))
            {
                hash128_t id = datum_to_hash128(changed_value);
                bool found;
                hash_search(route_seen, &id, HASH_ENTER, &found);
                laplace_tg_origin_add_occurrences(origins, &id,
                    laplace_prompt_intent_origins(coupled_intent_out, &id), walk_context);
                if (!found)
                    next = accumArrayResult(next, changed_value, false, BYTEAOID, walk_context);
            }
            array_free_iterator(changed_iterator);
            pfree(changed);
            ++coupling_hops;
            if (next)
            {
                ArrayType *next_ids = DatumGetArrayTypeP(makeArrayResult(next, walk_context));
                laplace_query_state_extend_batch(query_state, next_ids, NULL);
                pfree(next_ids);
            }
        }
        if (trace)
        {
            int coupled_channels = 0;
            (void) laplace_query_state_channels_unbound(query_state, &coupled_channels);
            laplace_tg_emit_stage(result, 0, "couple", input, context_length, 0,
                                  coupled_channels, 0, coupling_hops, NULL);
        }

        if (invocation_context)
            laplace_prompt_intent_compile(coupled_intent_out, invocation_context, fanout);
        else if (!output_relations ||
                 ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) == 0)
            laplace_task_shape_compile(coupled_intent_out, fanout);
        if (invocation_context || !output_relations ||
            ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) == 0)
            intent = coupled_intent_out;

        if (intent && !intent->ambiguous && !intent->budget_exhausted &&
            intent->relation_count > 0)
        {
            HTAB *selected_inputs = laplace_tg_new_id_index("forward bound inputs", 32,
                walk_context, sizeof(hash128_t));
            HTAB *selected_predicates = laplace_tg_new_id_index("forward bound predicates", 8,
                walk_context, sizeof(hash128_t));
            ArrayBuildState *input_ids = NULL, *predicate_ids = NULL, *route_ids = NULL;
            for (int operation_index = 0; operation_index < intent->relation_count; ++operation_index)
            {
                const LaplacePromptRelationRead *operation = &intent->operations[operation_index];
                bool found;
                if (!laplace_prompt_relation_allowed(intent, &operation->result_relation) ||
                    !laplace_tg_relation_allowed_by_output_scope(&operation->result_relation, output_relations))
                    continue;
                hash_search(selected_predicates, &operation->result_relation, HASH_ENTER, &found);
                if (!found)
                {
                    Datum id = hash128_to_datum(&operation->result_relation);
                    predicate_ids = accumArrayResult(predicate_ids, id, false, BYTEAOID, walk_context);
                    pfree(DatumGetPointer(id));
                }
                for (int input_index = 0; input_index < operation->input_count; ++input_index)
                {
                    const LaplacePromptOperand *operand = &operation->inputs[input_index];
                    bool routed;
                    laplace_tg_origin_add_occurrences(origins, &operand->id, operand->origins, walk_context);
                    hash_search(route_seen, &operand->id, HASH_ENTER, &routed);
                    if (!routed)
                    {
                        Datum id = hash128_to_datum(&operand->id);
                        route_ids = accumArrayResult(route_ids, id, false, BYTEAOID, walk_context);
                        pfree(DatumGetPointer(id));
                    }
                    hash_search(selected_inputs, &operand->id, HASH_ENTER, &found);
                    if (!found)
                    {
                        Datum id = hash128_to_datum(&operand->id);
                        input_ids = accumArrayResult(input_ids, id, false, BYTEAOID, walk_context);
                        pfree(DatumGetPointer(id));
                    }
                }
            }
            if (input_ids && predicate_ids)
            {
                ArrayType *inputs = DatumGetArrayTypeP(makeArrayResult(input_ids, walk_context));
                ArrayType *predicates = DatumGetArrayTypeP(makeArrayResult(predicate_ids, walk_context));
                if (route_ids)
                {
                    ArrayType *routed = DatumGetArrayTypeP(makeArrayResult(route_ids, walk_context));
                    laplace_query_state_extend_batch(query_state, routed, NULL);
                    pfree(routed);
                }
                output_state = laplace_query_state_create(inputs, predicates, fanout, NULL);
                pfree(inputs);
                pfree(predicates);
            }
            hash_destroy(selected_inputs);
            hash_destroy(selected_predicates);
        }

        OriginEntry *root_origin = laplace_tg_origin_get(origins, &input->root, true);
        for (int i = 0; i < context_length; ++i)
            root_origin->occurrences = bms_add_member(root_origin->occurrences, i);
        int initial_channel_count = 0;
        const LaplaceQueryChannel *initial_channels =
            laplace_query_state_channels_unbound(query_state, &initial_channel_count);
        cognition = laplace_cognition_program_create(
            input, context_length, initial_channels, initial_channel_count, intent);
        if (intent && intent->ambiguous)
            laplace_cognition_program_finalize(cognition, LAPLACE_COGNITION_AMBIGUOUS);
        else if (intent && intent->budget_exhausted)
            laplace_cognition_program_finalize(cognition, LAPLACE_COGNITION_BUDGET_EXHAUSTED);
        if (trace)
            laplace_tg_emit_stage(result, 0, "orient", input, context_length, 0,
                                  initial_channel_count, 0, routing_hops, cognition);
    }

    if (!output_state && !invocation_context && output_relations &&
        ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) > 0)
    {
        ArrayType *output_operands;
        if (input)
        {
            Datum root = hash128_to_datum(&input->root);
            output_operands = construct_array(&root, 1, BYTEAOID, -1, false, TYPALIGN_INT);
            pfree(DatumGetPointer(root));
        }
        else
        {
            output_operands = DatumGetArrayTypePCopy(PointerGetDatum(operands));
        }
        output_state = laplace_query_state_create(output_operands, output_relations,
                                                  fanout, NULL);
        pfree(output_operands);
    }

    *output_state_out = output_state;
    *trajectory_scope_out = trajectory_scope;
    *intent_inout = intent;
    *cognition_out = cognition;
    *coupling_hops_out = coupling_hops;
    *explicit_observation_scope_out = explicit_observation_scope;
}

Datum
laplace_tg_forward_prompt(FunctionCallInfo fcinfo, bool trace)
{
    int hops;
    int fanout;
    hash128_t active_invocation;
    const hash128_t *invocation_context = NULL;
    ArrayType *discourse = NULL;

    if (PG_NARGS() > 10)
    {
        if (PG_ARGISNULL(10))
            ereport(ERROR, (errmsg("bound program: invocation context must not be NULL")));
        if (VARSIZE_ANY_EXHDR(PG_GETARG_BYTEA_PP(10)) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("bound program: invocation context must be 16 bytes")));
        active_invocation = datum_to_hash128(PG_GETARG_DATUM(10));
        invocation_context = &active_invocation;
    }
    if (PG_ARGISNULL(0) || VARSIZE_ANY_EXHDR(PG_GETARG_TEXT_PP(0)) == 0)
    {
        InitMaterializedSRF(fcinfo, 0);
        return (Datum) 0;
    }

    hops = PG_ARGISNULL(6) ? 2 : PG_GETARG_INT32(6);
    fanout = PG_ARGISNULL(7) ? 8 : PG_GETARG_INT32(7);
    if (hops < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("forward prompt: hops and fanout must not be negative")));

    LaplacePromptInput *input = laplace_prompt_input(PG_GETARG_TEXT_PP(0));
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = CurrentMemoryContext;
    PromptFrontier frontier = {
        .owner = CurrentMemoryContext,
        .seen = hash_create("forward prompt supplemental operands", 128, &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT),
    };

    ArrayIterator iterator = array_create_iterator(input->seeds, 0, NULL);
    Datum value;
    bool isnull;
    while (array_iterate(iterator, &value, &isnull))
    {
        if (isnull)
            continue;
        hash128_t id = datum_to_hash128(value);
        laplace_tg_prompt_frontier_add(&frontier, &id);
    }
    array_free_iterator(iterator);

    if (!PG_ARGISNULL(8))
    {
        discourse = PG_GETARG_ARRAYTYPE_P(8);
        laplace_tg_validate_id_array(discourse, "discourse history", true);
    }

    LOCAL_FCINFO(walk_call, 12);
    InitFunctionCallInfoData(*walk_call, fcinfo->flinfo, 12, fcinfo->fncollation,
                             fcinfo->context, fcinfo->resultinfo);
    for (int i = 0; i < 12; ++i)
        walk_call->args[i].isnull = true;
    walk_call->args[0] = (NullableDatum) {PointerGetDatum(input->context), false};
    for (int i = 1; i <= 5; ++i)
        walk_call->args[i] = fcinfo->args[i];
    if (walk_call->args[5].isnull)
    {
        text *prompt = PG_GETARG_TEXT_PP(0);
        const char *source = VARDATA_ANY(prompt);
        int source_length = VARSIZE_ANY_EXHDR(prompt);
        char *utf8 = pg_server_to_any(source, source_length, PG_UTF8);
        Size utf8_length = utf8 == source ? (Size) source_length : strlen(utf8);
        hash128_t digest;
        const uint8 *bytes = (const uint8 *) &digest;
        uint64 seed_bits = 0;
        int64 seed;

        hash128_blake3((const uint8 *) utf8, utf8_length, &digest);
        for (int i = 0; i < 8; ++i)
            seed_bits = (seed_bits << 8) | bytes[i];
        memcpy(&seed, &seed_bits, sizeof(seed));
        if (utf8 != source)
            pfree(utf8);
        walk_call->args[5] = (NullableDatum) {Int64GetDatum(seed), false};
    }
    walk_call->args[6] = (NullableDatum) {
        makeArrayResult(frontier.ids, frontier.owner), false};
    walk_call->args[8] = (NullableDatum) {Int32GetDatum(fanout), false};
    walk_call->args[11] = fcinfo->args[9];

    return laplace_tg_walk_continuations(
        walk_call, input, hops, trace, NULL, invocation_context, discourse);
}
