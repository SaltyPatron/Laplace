/*
 * trajectory_generate.c — Forward pass step loop (PROPOSE -> SCAN -> COMPOSE
 * -> STEER / SELECT) and PostgreSQL entry points for trajectory generation.
 */
#include "trajectory_generate_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);
PG_FUNCTION_INFO_V1(pg_laplace_forward_prompt);
PG_FUNCTION_INFO_V1(pg_laplace_forward_trace);

Datum
laplace_tg_walk_continuations(FunctionCallInfo fcinfo, const LaplacePromptInput *input,
                              int semantic_hop_limit, bool trace,
                              const LaplacePromptIntent *intent,
                              const hash128_t *invocation_context,
                              ArrayType *discourse)
{
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *context_array, *frontier_array = NULL, *relation_types = NULL, *output_relations = NULL;
    int32 steps, max_stride, top_k, fanout;
    float8 spread;
    uint64 rng;
    Datum *context_values, *context;
    bool *context_nulls, exhausted = false, explicit_observation_scope = false;
    int context_count, context_length = 0, context_capacity;
    int next_origin = 0, coupling_hops = 0, routing_hops = 0;
    MemoryContext walk_context, step_context, old;
    LaplaceTrajectoryScope *trajectory_scope = NULL;
    LaplaceQueryState *query_state = NULL, *output_state = NULL;
    LaplaceCognitionProgram *cognition = NULL;
    LaplacePromptIntent coupled_intent = {0};
    HTAB *route_seen, *geometry_routed, *origins;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("forward execution: context must not be NULL")));
    context_array = PG_GETARG_ARRAYTYPE_P(0);
    steps = PG_ARGISNULL(1) ? 24 : PG_GETARG_INT32(1);
    max_stride = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);
    spread = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    top_k = PG_ARGISNULL(4) ? 10 : PG_GETARG_INT32(4);
    rng = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D) :
                            (uint64) PG_GETARG_INT64(5);
    if (PG_NARGS() > 6 && !PG_ARGISNULL(6))
        frontier_array = PG_GETARG_ARRAYTYPE_P(6);
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        relation_types = PG_GETARG_ARRAYTYPE_P(7);
    fanout = PG_NARGS() > 8 && !PG_ARGISNULL(8) ? PG_GETARG_INT32(8) : 8;
    if (PG_NARGS() > 11 && !PG_ARGISNULL(11))
        output_relations = PG_GETARG_ARRAYTYPE_P(11);

    if (steps < 0 || max_stride < 0 || top_k < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("forward execution: steps, stride, top-k and fanout must not be negative")));
    if (!isfinite(spread) || spread < 0.0)
        ereport(ERROR,
                (errmsg("forward execution: spread must be finite and not negative")));
    laplace_tg_validate_id_array(context_array, "context", true);
    laplace_tg_validate_id_array(frontier_array, "frontier", true);
    laplace_tg_validate_id_array(relation_types, "relation types", false);
    laplace_tg_validate_id_array(output_relations, "output relation types", false);

    InitMaterializedSRF(fcinfo, 0);
    if (steps == 0 || top_k == 0)
        return (Datum) 0;

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "forward execution: SPI_connect failed");

    walk_context = CurrentMemoryContext;
    deconstruct_array(context_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &context_values, &context_nulls, &context_count);
    if ((uint64) context_count + (uint64) steps > INT_MAX ||
        (uint64) (context_count + steps) > MaxAllocSize / sizeof(Datum))
        ereport(ERROR,
                (errmsg("forward execution: retained ordered context exceeds allocation capacity")));
    context_capacity = context_count + steps;
    context = palloc(sizeof(Datum) * Max(context_capacity, 1));
    for (int i = 0; i < context_count; ++i)
    {
        if (context_nulls[i])
            continue;
        context[context_length++] = PointerGetDatum(PG_DETOAST_DATUM_COPY(context_values[i]));
    }
    if (context_count > 0)
    {
        pfree(context_values);
        pfree(context_nulls);
    }
    if (context_length == 0)
    {
        SPI_finish();
        return (Datum) 0;
    }

    old = MemoryContextSwitchTo(walk_context);
    uint32 *operand_roles = NULL;
    int operand_role_count = 0;
    ArrayType *operands = laplace_tg_query_operands(
        context_array, frontier_array, discourse, walk_context,
        &operand_roles, &operand_role_count);
    query_state = laplace_query_state_create(operands, relation_types, fanout, NULL);
    laplace_query_state_set_operand_roles(
        query_state, operand_roles, operand_role_count);
    pfree(operand_roles);
    origins = laplace_tg_new_id_index("forward occurrence provenance", 128,
                                      walk_context, sizeof(OriginEntry));
    route_seen = laplace_tg_new_id_index("forward admitted routing identities", 128,
                                         walk_context, sizeof(hash128_t));
    geometry_routed = laplace_tg_new_id_index("forward geometry promoted routes", 128,
                                              walk_context, sizeof(hash128_t));
    ArrayIterator route_iterator = array_create_iterator(operands, 0, NULL);
    Datum route_value;
    bool route_null;
    while (array_iterate(route_iterator, &route_value, &route_null))
    {
        if (route_null) continue;
        hash128_t id = datum_to_hash128(route_value);
        hash_search(route_seen, &id, HASH_ENTER, NULL);
        OriginEntry *origin = laplace_tg_origin_get(origins, &id, true);
        origin->occurrences = bms_add_member(origin->occurrences, next_origin++);
    }
    array_free_iterator(route_iterator);

    laplace_tg_couple_and_orient(
        fcinfo, result, input, walk_context,
        operands, relation_types, output_relations, discourse,
        invocation_context, max_stride, fanout, semantic_hop_limit,
        context_length, trace, routing_hops, origins, route_seen,
        query_state, &output_state, &trajectory_scope,
        &coupled_intent, &intent, &cognition,
        &coupling_hops, &explicit_observation_scope);
    (void) coupling_hops;
    pfree(operands);
    MemoryContextSwitchTo(old);

    step_context = AllocSetContextCreate(walk_context, "forward query election",
                                         ALLOCSET_DEFAULT_SIZES);
    HTAB *geometry_table = laplace_tg_geometry_summaries(intent, walk_context);
    ArrayType *carried = NULL;
    const bool open_turn =
        !(intent && (intent->explicit_invocation || intent->relation_count > 0)) &&
        !(output_relations &&
          ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) > 0);

    for (int32 step = 1; step <= steps &&
         !(intent && (intent->ambiguous || intent->budget_exhausted ||
                      (intent->explicit_invocation && intent->relation_count == 0)));)
    {
        Candidate *candidates = NULL;
        int candidate_count = 0, candidate_capacity = 0;
        HTAB *candidate_index;
        HTAB *query_traversal_proposals = NULL;
        HTAB *projection_proposals = NULL;
        HTAB *query_evidence_table;
        HTAB *query_traversal_table;
        HTAB *projection_table = NULL;
        LaplaceQueryChannel *projection_channels = NULL;
        int projection_channel_count = 0;
        int pick = -1;

        MemoryContextReset(step_context);
        MemoryContextSwitchTo(step_context);
        CHECK_FOR_INTERRUPTS();
        Bitmapset *obligations = laplace_cognition_program_remaining(cognition);
        candidate_index = laplace_tg_new_id_index("forward candidate index", 128,
                                                  step_context, sizeof(CandidateIndex));

        if (max_stride > 0)
        {
            int depth = input && step == 1 ? context_length : Min(context_length, max_stride);
            if (depth > 0)
            {
                ArrayType *tail = construct_array(context + context_length - depth, depth,
                                                  BYTEAOID, -1, false, TYPALIGN_INT);
                int count = 0;
                LaplaceContinuation *continuations =
                    laplace_trajectory_continuations_scoped(tail, true,
                                                            trajectory_scope, &count);
                for (int i = 0; i < count; ++i)
                {
                    laplace_tg_candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                             candidate_index, &continuations[i].id,
                                             continuations[i].occurrences,
                                             continuations[i].stride);
                    laplace_tg_origin_inherit_sequence(origins, &continuations[i].id,
                                                       context, context_length,
                                                       continuations[i].stride, walk_context);
                }
                if (continuations)
                    pfree(continuations);
                pfree(tail);
            }
        }

        if (input && intent && intent->geometry_count > 0)
        {
            HASH_SEQ_STATUS geometry_scan;
            GeometryEntry *geometry;
            hash_seq_init(&geometry_scan, geometry_table);
            while ((geometry = hash_seq_search(&geometry_scan)) != NULL)
            {
                if (hash_search(geometry_routed, &geometry->id, HASH_FIND, NULL))
                    continue;
                int index = laplace_tg_candidate_add(
                    &candidates, &candidate_count, &candidate_capacity,
                    candidate_index, &geometry->id, 0, 0);
                candidates[index].geometry = geometry->summary;
            }
        }

        if (input && ((intent && intent->relation_count > 0) ||
                      semantic_hop_limit < 0 || routing_hops < semantic_hop_limit))
        {
            query_traversal_proposals = laplace_tg_evidence_summaries(
                query_state, true, origins, obligations, cognition, step_context);
            HASH_SEQ_STATUS sequence;
            EvidenceEntry *nomination;
            hash_seq_init(&sequence, query_traversal_proposals);
            while ((nomination = hash_seq_search(&sequence)) != NULL)
            {
                if (!nomination->summary.has_positive ||
                    (nomination->summary.positive_covered_occurrences <= 0 &&
                     nomination->summary.positive_required_occurrences <= 0) ||
                    hash_search(route_seen, &nomination->id, HASH_FIND, NULL))
                    continue;
                laplace_tg_candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                         candidate_index, &nomination->id, 0, 0);
            }
        }

        if (output_state)
        {
            projection_proposals = laplace_tg_evidence_summaries(
                output_state, true, origins, obligations, cognition, step_context);
            HASH_SEQ_STATUS sequence;
            EvidenceEntry *projection;
            hash_seq_init(&sequence, projection_proposals);
            while ((projection = hash_seq_search(&sequence)) != NULL)
            {
                if (!projection->summary.has_positive)
                    continue;
                CandidateIndex *existing = hash_search(candidate_index,
                    &projection->id, HASH_FIND, NULL);
                if (!existing && laplace_tg_context_contains_id(context, context_length, &projection->id))
                    continue;
                laplace_tg_candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                         candidate_index, &projection->id, 0, 0);
            }
        }

        if (carried)
        {
            ArrayIterator carried_iterator = array_create_iterator(carried, 0, NULL);
            Datum carried_value;
            bool carried_null;
            while (array_iterate(carried_iterator, &carried_value, &carried_null))
            {
                if (carried_null) continue;
                hash128_t id = datum_to_hash128(carried_value);
                if (laplace_tg_context_contains_id(context, context_length, &id))
                    continue;
                laplace_tg_candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                         candidate_index, &id, 0, 0);
            }
            array_free_iterator(carried_iterator);
        }

        (void) projection_proposals;
        if (trace)
            laplace_tg_emit_stage(result, step, "propose", input, context_length,
                                  candidate_count, laplace_query_state_channel_count(query_state), 0,
                                  routing_hops, cognition);
        if (candidate_count == 0)
        {
            exhausted = true;
            break;
        }

        ArrayType *candidate_ids = laplace_tg_candidate_id_array(candidates, candidate_count);
        int query_channel_count = 0;
        LaplaceQueryChannel *query_channels = trace
            ? laplace_query_state_candidate_evidence(query_state, candidate_ids,
                                                     &query_channel_count, NULL)
            : laplace_query_state_candidate_evidence_unbound(query_state, candidate_ids,
                                                             &query_channel_count);

        for (int i = 0; intent && i < query_channel_count; ++i)
        {
            const LaplaceQueryChannel *channel = &query_channels[i];
            CandidateIndex *candidate = hash_search(candidate_index,
                &channel->candidate, HASH_FIND, NULL);
            if (candidate && laplace_prompt_intent_bound_operation(intent, channel) &&
                laplace_tg_relation_allowed_by_output_scope(&channel->relation, output_relations) &&
                (channel->outbound || laplace_tg_relation_can_traverse_reverse(&channel->relation)) &&
                laplace_walk_edge_weight(channel->rating, channel->rd) > 0.0)
                candidates[candidate->index].intent_result = true;
        }

        if (output_state)
        {
            projection_channels = laplace_query_state_candidate_evidence(
                output_state, candidate_ids, &projection_channel_count, NULL);
            projection_table = laplace_tg_evidence_summaries_from_channels(
                projection_channels, projection_channel_count, true, origins, obligations, cognition, step_context);
            for (int i = 0; i < candidate_count; ++i)
            {
                EvidenceEntry *projection = hash_search(projection_table,
                    &candidates[i].id, HASH_FIND, NULL);
                if (projection)
                    candidates[i].projection = projection->summary;
            }
        }
        pfree(candidate_ids);

        if (!trace && query_channel_count > 0)
        {
            int *bind_idx = palloc(sizeof(int) * query_channel_count);
            LaplaceQueryChannel *bind_buf = palloc(sizeof(LaplaceQueryChannel) * query_channel_count);
            int bind_count = 0;
            for (int i = 0; i < query_channel_count; ++i)
            {
                double w = laplace_walk_edge_weight(query_channels[i].rating, query_channels[i].rd);
                bool neutral = !(w > 0.0 && isfinite(w)) && !(w < 0.0 && isfinite(w));
                CandidateIndex *ci = hash_search(candidate_index, &query_channels[i].candidate, HASH_FIND, NULL);
                if (neutral || (ci && laplace_tg_candidate_can_output(&candidates[ci->index], intent)))
                {
                    bind_idx[bind_count] = i;
                    bind_buf[bind_count] = query_channels[i];
                    bind_count++;
                }
            }
            if (bind_count > 0)
            {
                laplace_query_state_bind_channels(query_state, bind_buf, bind_count);
                for (int m = 0; m < bind_count; ++m)
                    query_channels[bind_idx[m]] = bind_buf[m];
            }
            pfree(bind_buf);
            pfree(bind_idx);
        }

        query_evidence_table = laplace_tg_evidence_summaries_from_channels(
            query_channels, query_channel_count, false, origins, obligations, cognition, step_context);
        query_traversal_table = laplace_tg_evidence_summaries_from_channels(
            query_channels, query_channel_count, true, origins, obligations, cognition, step_context);
        if (trace)
            laplace_tg_emit_stage(result, step, "scan", input, context_length,
                                  candidate_count, laplace_query_state_channel_count(query_state),
                                  query_channel_count + projection_channel_count,
                                  routing_hops, cognition);

        if (cognition)
        {
            laplace_cognition_program_note_semantic_channels(
                cognition, query_channels, query_channel_count);
            if (projection_channels)
                laplace_cognition_program_note_semantic_channels(
                    cognition, projection_channels, projection_channel_count);
        }

        laplace_tg_propagate_candidate_origins(origins, candidate_index,
                                               query_channels, query_channel_count,
                                               intent, walk_context);
        if (projection_channels)
            laplace_tg_propagate_candidate_origins(origins, candidate_index,
                                                   projection_channels,
                                                   projection_channel_count,
                                                   NULL, walk_context);

        int kept = 0;
        for (int i = 0; i < candidate_count; ++i)
        {
            EvidenceEntry *query = hash_search(query_evidence_table,
                                               &candidates[i].id, HASH_FIND, NULL);
            if (query)
                candidates[i].query = query->summary;
            EvidenceEntry *traversal = hash_search(query_traversal_table,
                                                   &candidates[i].id, HASH_FIND, NULL);
            if (traversal)
                candidates[i].query_traversal = traversal->summary;
            if (projection_table)
            {
                EvidenceEntry *projection = hash_search(projection_table,
                    &candidates[i].id, HASH_FIND, NULL);
                if (projection)
                    candidates[i].projection = projection->summary;
            }
            GeometryEntry *geometry = hash_search(
                geometry_table, &candidates[i].id, HASH_FIND, NULL);
            if (geometry)
                candidates[i].geometry = geometry->summary;

            if (candidates[i].sequence_occurrences == 0 &&
                !candidates[i].projection.has_positive &&
                !candidates[i].query.has_positive &&
                !(input && candidates[i].query_traversal.has_positive) &&
                !laplace_tg_geometry_summary_present(&candidates[i].geometry))
                continue;
            candidates[kept++] = candidates[i];
        }
        candidate_count = kept;
        if (trace)
            laplace_tg_emit_stage(result, step, "compose", input, context_length,
                                  candidate_count, laplace_query_state_channel_count(query_state),
                                  query_channel_count + projection_channel_count,
                                  routing_hops, cognition);
        if (candidate_count == 0)
        {
            exhausted = true;
            break;
        }

        if (explicit_observation_scope)
        {
            bool has_unopposed_scoped_sequence = false;
            for (int i = 0; i < candidate_count; ++i)
            {
                if (candidates[i].sequence_occurrences > 0 &&
                    !candidates[i].query.has_negative &&
                    !candidates[i].query_traversal.has_negative &&
                    !candidates[i].projection.has_negative)
                {
                    has_unopposed_scoped_sequence = true;
                    break;
                }
            }
            if (has_unopposed_scoped_sequence)
            {
                kept = 0;
                for (int i = 0; i < candidate_count; ++i)
                    if (candidates[i].sequence_occurrences > 0 &&
                        !candidates[i].query.has_negative &&
                        !candidates[i].query_traversal.has_negative &&
                        !candidates[i].projection.has_negative)
                        candidates[kept++] = candidates[i];
                candidate_count = kept;
            }
        }

        qsort(candidates, (size_t) candidate_count, sizeof(Candidate),
              laplace_tg_candidate_compare);
        int retained_channel_count = trace ? laplace_query_state_channel_count(query_state) : 0;

        int output_count = 0;
        for (int i = 0; i < candidate_count; ++i)
            if (laplace_tg_candidate_can_output(&candidates[i], intent))
                output_count++;
        if (trace)
            laplace_tg_emit_stage(result, step, "steer", input, context_length,
                                  candidate_count, retained_channel_count,
                                  query_channel_count + projection_channel_count,
                                  routing_hops, cognition);

        bool route_for_meeting = false;
        if (open_turn && output_count > 0 && !bms_is_empty(obligations))
        {
            static const uint8 space_b3[16] = {
                0x00, 0x26, 0x3c, 0xa9, 0xf5, 0x7f, 0x71, 0x77,
                0xf4, 0x95, 0xe3, 0x71, 0x1f, 0x8c, 0xdd, 0x59};
            const Candidate *first_output = NULL;
            int need = bms_num_members(obligations), best = 0;
            for (int i = 0; i < candidate_count; ++i)
            {
                if (!laplace_tg_candidate_can_output(&candidates[i], intent))
                    continue;
                if (!first_output) first_output = &candidates[i];
                best = Max(best, candidates[i].query.positive_required_occurrences);
                best = Max(best, candidates[i].query_traversal.positive_required_occurrences);
                best = Max(best, candidates[i].projection.positive_required_occurrences);
            }
            route_for_meeting = best < need &&
                !(first_output && first_output->stride > 0 &&
                  memcmp(&first_output->id, space_b3, sizeof(hash128_t)) == 0);
        }
        const bool routing_budget =
            input && !(semantic_hop_limit >= 0 && routing_hops >= semantic_hop_limit) &&
            !(open_turn && output_count == 0);
        if (output_count == 0 && !routing_budget)
        {
            exhausted = true;
            break;
        }
        if (output_count == 0 || (route_for_meeting && routing_budget))
        {
            ArrayBuildState *new_routed = NULL;
            int routed_count = 0, route_limit = Max(fanout, top_k);
            ++routing_hops;
            if (cognition)
                laplace_cognition_program_note_route(cognition);
            if (trace)
                for (int i = 0; i < candidate_count; ++i)
                    laplace_tg_emit_result(result, step, &candidates[i], true, input,
                                           candidate_count, context_length, retained_channel_count,
                                           query_channel_count, true, routing_hops, cognition);
            MemoryContextSwitchTo(walk_context);
            if (route_for_meeting)
            {
                ArrayBuildState *keep = NULL;
                for (int i = 0; i < candidate_count; ++i)
                {
                    if (!laplace_tg_candidate_can_output(&candidates[i], intent))
                        continue;
                    Datum id = hash128_to_datum(&candidates[i].id);
                    keep = accumArrayResult(keep, id, false, BYTEAOID, walk_context);
                    pfree(DatumGetPointer(id));
                }
                if (carried)
                    pfree(carried);
                carried = keep ? DatumGetArrayTypeP(makeArrayResult(keep, walk_context)) : NULL;
            }
            for (int i = 0; i < candidate_count && (route_limit <= 0 || routed_count < route_limit); ++i)
            {
                bool already_routed;
                if (candidates[i].query.positive_covered_occurrences <= 0 &&
                    candidates[i].query.positive_required_occurrences <= 0 &&
                    candidates[i].query_traversal.positive_covered_occurrences <= 0 &&
                    candidates[i].query_traversal.positive_required_occurrences <= 0 &&
                    !candidates[i].projection.has_positive &&
                    !laplace_tg_geometry_summary_present(&candidates[i].geometry))
                    continue;
                hash_search(route_seen, &candidates[i].id, HASH_ENTER, &already_routed);
                if (laplace_tg_geometry_summary_present(&candidates[i].geometry))
                    hash_search(geometry_routed, &candidates[i].id, HASH_ENTER, NULL);
                if (!already_routed)
                {
                    Datum id = hash128_to_datum(&candidates[i].id);
                    new_routed = accumArrayResult(new_routed, id, false, BYTEAOID, walk_context);
                    pfree(DatumGetPointer(id));
                    ++routed_count;
                }
            }
            if (new_routed)
            {
                ArrayType *routed = DatumGetArrayTypeP(makeArrayResult(new_routed, walk_context));
                laplace_query_state_extend_batch(query_state, routed, NULL);
                if (output_state)
                    laplace_query_state_extend_batch(output_state, routed, NULL);
                pfree(routed);
            }
            MemoryContextSwitchTo(step_context);
            continue;
        }
        kept = 0;
        for (int i = 0; i < candidate_count; ++i)
            if (laplace_tg_candidate_can_output(&candidates[i], intent))
                candidates[kept++] = candidates[i];
        candidate_count = kept;
        if (open_turn)
            laplace_tg_order_leading_by_sharing(candidates, candidate_count);
        int limit = Min(candidate_count, top_k);
        if (open_turn && limit > 1)
        {
            int32 top_g = laplace_tg_candidate_grounded(&candidates[0]);
            int filtered = 1;
            while (filtered < limit &&
                   laplace_tg_candidate_grounded(&candidates[filtered]) == top_g &&
                   candidates[filtered].stride == candidates[0].stride &&
                   (top_g > 0 || candidates[filtered].sequence_occurrences * 2 >= candidates[0].sequence_occurrences))
                ++filtered;
            limit = filtered;
        }
        if (spread == 0.0)
            pick = 0;
        else
        {
            double best = 0.0;
            for (int i = 0; i < limit; ++i)
            {
                double u = laplace_tg_rng_uniform(&rng);
                double key = -((double) i) / spread - log(-log(u));
                if (i == 0 || key > best)
                {
                    best = key;
                    pick = i;
                }
            }
        }
        if (pick < 0)
        {
            exhausted = true;
            break;
        }
        if (trace)
            laplace_tg_emit_stage(result, step, "select", input, context_length,
                                  candidate_count, retained_channel_count,
                                  query_channel_count + projection_channel_count,
                                  routing_hops, cognition);

        MemoryContextSwitchTo(walk_context);
        Datum selected = hash128_to_datum(&candidates[pick].id);
        context[context_length++] = selected;
        hash_search(route_seen, &candidates[pick].id, HASH_ENTER, NULL);
        OriginEntry *selected_origin = laplace_tg_origin_get(origins, &candidates[pick].id, true);
        if (next_origin == INT_MAX)
            elog(ERROR, "forward execution: emitted occurrence ordinal overflow");
        selected_origin->occurrences = bms_add_member(selected_origin->occurrences,
                                                      next_origin++);
        laplace_query_state_extend(query_state, selected, NULL);
        if (output_state)
            laplace_query_state_extend(output_state, selected, NULL);

        if (trajectory_scope)
        {
            bool ordered = candidates[pick].stride > 0;
            laplace_trajectory_scope_select(trajectory_scope, selected, ordered);
            if (!ordered)
            {
                ArrayType *selection = construct_array(&selected, 1, BYTEAOID, -1,
                                                       false, TYPALIGN_INT);
                laplace_trajectory_scope_extend(trajectory_scope, selection);
                pfree(selection);
            }
        }
        if (cognition)
            laplace_cognition_program_note_emit(
                cognition, &candidates[pick].id, selected_origin->occurrences,
                candidates[pick].projection.has_positive ||
                candidates[pick].query_traversal.has_positive ||
                candidates[pick].query.has_positive);

        MemoryContextSwitchTo(step_context);
        laplace_tg_emit_result(result, step, &candidates[pick], trace, input,
                               candidate_count, context_length, retained_channel_count,
                               query_channel_count, false, routing_hops, cognition);
        ++step;

        if (cognition)
        {
            LaplaceCognitionProgramReceipt receipt;
            laplace_cognition_program_receipt(cognition, &receipt);
            if (receipt.complete &&
                (!output_relations ||
                 ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) == 0))
                break;
        }
    }

    MemoryContextSwitchTo(walk_context);
    if (cognition)
    {
        LaplaceCognitionProgramReceipt receipt;
        laplace_cognition_program_receipt(cognition, &receipt);
        if (!receipt.complete && receipt.disposition == LAPLACE_COGNITION_OPEN)
            laplace_cognition_program_finalize(
                cognition,
                !exhausted && receipt.output_count >= steps
                    ? LAPLACE_COGNITION_BUDGET_EXHAUSTED
                    : LAPLACE_COGNITION_EXHAUSTED);
        if (trace)
        {
            MemoryContextSwitchTo(step_context);
            laplace_cognition_program_receipt(cognition, &receipt);
            laplace_tg_emit_terminal(result, receipt.output_count + 1, input,
                                     context_length, routing_hops, cognition);
            MemoryContextSwitchTo(walk_context);
        }
    }

    MemoryContextDelete(step_context);
    laplace_query_state_destroy(&output_state);
    laplace_query_state_destroy(&query_state);
    laplace_cognition_program_destroy(&cognition);
    hash_destroy(route_seen);
    HASH_SEQ_STATUS origin_sequence;
    OriginEntry *origin;
    hash_seq_init(&origin_sequence, origins);
    while ((origin = hash_seq_search(&origin_sequence)) != NULL)
        bms_free(origin->occurrences);
    hash_destroy(origins);
    for (int i = 0; i < context_length; ++i)
        pfree(DatumGetPointer(context[i]));
    pfree(context);
    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    return laplace_tg_walk_continuations(fcinfo, NULL, -1, false, NULL, NULL, NULL);
}

Datum
pg_laplace_forward_prompt(PG_FUNCTION_ARGS)
{
    return laplace_tg_forward_prompt(fcinfo, false);
}

Datum
pg_laplace_forward_trace(PG_FUNCTION_ARGS)
{
    return laplace_tg_forward_prompt(fcinfo, true);
}
