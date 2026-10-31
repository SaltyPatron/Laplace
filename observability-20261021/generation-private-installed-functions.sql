CREATE OR REPLACE FUNCTION generation.forward_prompt(p_prompt text, p_steps integer, p_max_stride integer, p_spread double precision, p_top_k integer, p_seed bigint, p_hops integer, p_fanout integer, p_prior_frontier bytea[], p_output_relation_types bytea[])
 RETURNS TABLE(step integer, entity bytea, stride_used integer, sep_entity bytea)
 LANGUAGE sql
BEGIN ATOMIC
 SELECT p.step,
     p.entity,
     p.stride_used,
     p.sep_entity
    FROM generation.forward_program(forward_prompt.p_prompt, forward_prompt.p_steps, forward_prompt.p_max_stride, forward_prompt.p_spread, forward_prompt.p_top_k, forward_prompt.p_seed, forward_prompt.p_hops, forward_prompt.p_fanout, forward_prompt.p_prior_frontier, forward_prompt.p_output_relation_types) p(step, entity, stride_used, sep_entity, root_id, candidate_count, ordered_context_count, proposal_channel_count, exact_channel_count, sequence_occurrences, covered_occurrences, relation_families, opposed_occurrences, support_anchor, support_relation, support_outbound, support_rating, support_rd, support_witnesses, support_sources, support_contexts, declared_result, event, routing_round, program_id, required_obligations, satisfied_obligations, remaining_required, completion, disposition, output_fingerprint, semantic_act_id, output_count)
   WHERE (p.event = 'emit'::text);
END

CREATE OR REPLACE FUNCTION generation.forward_trace(p_prompt text, p_steps integer DEFAULT 24, p_max_stride integer DEFAULT 5, p_spread double precision DEFAULT 0.7, p_top_k integer DEFAULT 10, p_seed bigint DEFAULT NULL::bigint, p_hops integer DEFAULT 2, p_fanout integer DEFAULT 8, p_prior_frontier bytea[] DEFAULT NULL::bytea[], p_output_relation_types bytea[] DEFAULT NULL::bytea[])
 RETURNS TABLE(step integer, entity bytea, stride_used integer, sep_entity bytea, root_id bytea, candidate_count integer, ordered_context_count integer, proposal_channel_count integer, exact_channel_count integer, sequence_occurrences bigint, covered_occurrences integer, relation_families integer, opposed_occurrences integer, support_anchor bytea, support_relation bytea, support_outbound boolean, support_rating bigint, support_rd bigint, support_witnesses bigint, support_sources integer, support_contexts integer, declared_result boolean, event text, routing_round integer)
 LANGUAGE sql
BEGIN ATOMIC
 SELECT p.step,
     p.entity,
     p.stride_used,
     p.sep_entity,
     p.root_id,
     p.candidate_count,
     p.ordered_context_count,
     p.proposal_channel_count,
     p.exact_channel_count,
     p.sequence_occurrences,
     p.covered_occurrences,
     p.relation_families,
     p.opposed_occurrences,
     p.support_anchor,
     p.support_relation,
     p.support_outbound,
     p.support_rating,
     p.support_rd,
     p.support_witnesses,
     p.support_sources,
     p.support_contexts,
     p.declared_result,
     p.event,
     p.routing_round
    FROM generation.forward_program(forward_trace.p_prompt, forward_trace.p_steps, forward_trace.p_max_stride, forward_trace.p_spread, forward_trace.p_top_k, forward_trace.p_seed, forward_trace.p_hops, forward_trace.p_fanout, forward_trace.p_prior_frontier, forward_trace.p_output_relation_types) p(step, entity, stride_used, sep_entity, root_id, candidate_count, ordered_context_count, proposal_channel_count, exact_channel_count, sequence_occurrences, covered_occurrences, relation_families, opposed_occurrences, support_anchor, support_relation, support_outbound, support_rating, support_rd, support_witnesses, support_sources, support_contexts, declared_result, event, routing_round, program_id, required_obligations, satisfied_obligations, remaining_required, completion, disposition, output_fingerprint, semantic_act_id, output_count)
   WHERE (p.event = ANY (ARRAY['route'::text, 'emit'::text]));
END

generation|forward_program|laplace_execution_1a19d2558692d51c|pg_laplace_forward_trace
generation|forward_program|laplace_execution_1a19d2558692d51c|pg_laplace_forward_trace
