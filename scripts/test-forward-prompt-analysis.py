#!/usr/bin/env python3
"""Source contract for the canonical query-relative cognition program."""
from __future__ import annotations

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
FRONTIER_PATH = ROOT / "extension/laplace_substrate/sql/functions/generation/forward_frontier.sql.in"
WALK_PATH = ROOT / "extension/laplace_substrate/sql/functions/generation/walk_text.sql.in"
WALK_CONTINUATIONS_PATH = ROOT / "extension/laplace_substrate/sql/functions/generation/walk_continuations.sql.in"
CHAT_PATH = ROOT / "extension/laplace_substrate/sql/functions/converse/chat.sql.in"
COGNITION_COMPLETION_PATH = ROOT / "extension/laplace_substrate/tests/sql/cognition_completion.sql"


def strip_sql_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"--[^\n]*", " ", text)


def function_slice(text: str, name: str, next_name: str | None) -> str:
    start_marker = f"CREATE OR REPLACE FUNCTION {name}("
    start = text.find(start_marker)
    assert start >= 0, f"missing function {name}"
    if next_name is None:
        return text[start:]
    end_marker = f"CREATE OR REPLACE FUNCTION {next_name}("
    end = text.find(end_marker, start + len(start_marker))
    assert end >= 0, f"missing function following {name}: {next_name}"
    return text[start:end]


def count(haystack: str, needle: str) -> int:
    return haystack.count(needle)


def main() -> int:
    frontier = strip_sql_comments(FRONTIER_PATH.read_text())
    walk = strip_sql_comments(WALK_PATH.read_text())
    walk_continuations = strip_sql_comments(WALK_CONTINUATIONS_PATH.read_text())
    chat = strip_sql_comments(CHAT_PATH.read_text())
    cognition_completion = COGNITION_COMPLETION_PATH.read_text()

    assert "CREATE OR REPLACE FUNCTION" not in frontier
    for retired in (
        "generation.forward_frontier_ids(bytea[], integer, integer, integer)",
        "generation.forward_route_trace_ids(bytea[], integer, integer, integer)",
        "converse.prompt_operands(text)",
    ):
        assert f"DROP FUNCTION IF EXISTS {retired};" in walk

    # Exact observation identity precedes cognition. No text heuristic is
    # permitted to replace the canonical prompt tree.
    assert count(walk, "generation.forward_program(") == 1, \
        "forward_text must execute the native cognition program exactly once"
    assert "converse.prompt_state(" not in walk
    assert "converse.prompt_coherence(" not in walk
    assert "converse.prompt_operands(" not in walk.split("DROP FUNCTION IF EXISTS generation.forward_frontier(")[0]
    native = (ROOT / "extension/laplace_substrate/src/content_resolve.c").read_text()
    operands = native.split("laplace_prompt_input(text *input)", 1)[1].split(
        "pg_laplace_prompt_tree(PG_FUNCTION_ARGS)", 1)[0]
    assert count(operands, "laplace_content_tree_build_public(") == 1
    assert "seed_ids[0] = root" in operands
    assert "content_witness_tree_root_id(tree, &root)" in operands

    # Natural text has no hard-coded completion vocabulary. Its only output is
    # the completed semantic act from the shared program.
    forward_text = function_slice(walk, "generation.forward_text", "converse.forward_turn")
    assert "CONTINUATION_OUTPUT" not in forward_text
    assert "NULL::bytea[]" in forward_text
    assert "r.completion" in forward_text
    assert "r.semantic_act_id IS NOT NULL" in forward_text
    assert "realize.batch(entities)" in forward_text

    # Full program is the only C whole-prompt execution. Compatibility trace and
    # identity output are SQL projections of that exact invocation contract.
    full_program = function_slice(
        walk_continuations, "generation.forward_program", "generation.forward_trace")
    trace_sql = function_slice(
        walk_continuations, "generation.forward_trace", "generation.forward_prompt")
    prompt_sql = function_slice(
        walk_continuations, "generation.forward_prompt", "generation.forward_walk_continuations")
    assert "'pg_laplace_forward_trace'" in full_program
    assert "LANGUAGE C VOLATILE" in full_program
    for field in (
        "program_id bytea", "required_obligations int", "satisfied_obligations int",
        "remaining_required int", "completion boolean", "disposition text",
        "output_fingerprint bytea", "semantic_act_id bytea", "output_count int",
    ):
        assert field in full_program
    assert "generation.forward_program(" in trace_sql
    assert "LANGUAGE sql VOLATILE" in trace_sql
    assert "'pg_laplace_forward_trace'" not in trace_sql
    assert "generation.forward_program(" in prompt_sql
    assert re.search(r"WHERE\s+p\.event\s*=\s*'emit'", prompt_sql, re.I)

    program = (ROOT / "extension/laplace_substrate/src/trajectory_generate.c").read_text()
    entry = program.split("forward_prompt(FunctionCallInfo fcinfo, bool trace)", 1)[1]
    assert count(entry, "laplace_prompt_input(") == 1
    assert "laplace_explore_web(" not in program
    assert "laplace_steer_candidates(" not in program
    assert "laplace_query_state_create(" in program
    assert "laplace_query_state_extend(query_state, selected" in program
    assert "laplace_query_state_extend(output_state, selected" in program
    assert "walk_continuations(walk_call, input, hops, trace)" in entry
    assert "laplace_trajectory_scope_bind_input(trajectory_scope, input)" in program

    # Structural and semantic providers may contribute ancestry to the same
    # selected identity, but structural ancestry alone must never satisfy a
    # semantic requirement. Exact typed channels are folded into a separate
    # semantic-provenance state on every routed execution iteration.
    assert "origin_inherit_sequence(" in program
    assert "propagate_candidate_origins(" in program
    assert "laplace_cognition_program_create(" in program
    assert count(program, "laplace_cognition_program_note_semantic_channels(") == 2
    assert "laplace_cognition_program_note_route(" in program
    assert "laplace_cognition_program_note_emit(" in program
    assert "if (receipt.complete)" in program
    assert "laplace_cognition_program_finalize(" in program
    assert "emit_terminal(" in program

    completion = (ROOT / "extension/laplace_substrate/src/cognition_program.c").read_text()
    completion_header = (ROOT / "extension/laplace_substrate/src/cognition_program.h").read_text()
    assert "tiers[node] >= 2" in completion
    assert "program->required = bms_copy(eligible)" in completion
    assert "semantic_origins" in completion
    assert "semantic_origin_add(program, &id, i)" in completion
    assert "semantic_origin_get(program, &input->root, true)" in completion
    assert "record_semantic_channel(program, &initial_channels[i])" in completion
    assert "laplace_cognition_program_note_semantic_channels(" in completion
    assert "record_semantic_channel(program, &channels[i])" in completion
    assert "semantic_channel_traversable(" in completion
    assert "bms_intersect(origins, grounding->origins)" in completion
    assert "addressable ? bms_copy(addressable)" not in completion
    assert "program->semantic_output_count <= 0" in completion
    assert "laplace:semantic-act:v1" in completion
    assert "LAPLACE_COGNITION_BUDGET_EXHAUSTED" in completion_header
    assert "keyword" in completion_header.lower(), \
        "completion contract must explicitly reject prompt keyword classification"

    # Runtime regression is part of the source contract: the whole prompt trunk
    # must be able to satisfy the whole request, and routed typed state must carry
    # that grounding into a later declared result relation.
    assert "DO $whole_trunk_grounding$" in cognition_completion
    assert "FROM converse.prompt_tree(prompt)" in cognition_completion
    assert "ARRAY[causes_id]" in cognition_completion
    assert "DO $routed_semantic_grounding$" in cognition_completion
    assert "ARRAY[result_relation]" in cognition_completion
    assert "emitted IS DISTINCT FROM ARRAY[result_id]" in cognition_completion

    # Candidate proposal and exact candidate adjudication remain distinct.
    evidence_native = (ROOT / "extension/laplace_substrate/src/query_evidence.c").read_text()
    assert "laplace_query_state_candidate_evidence(" in evidence_native
    assert "laplace_consensus_scan(operand_ids, candidate_ids" in evidence_native
    assert "laplace_consensus_scan(candidate_ids, operand_ids" in evidence_native
    assert "state->operands = DatumGetArrayTypePCopy" in evidence_native
    assert "query_state_append_operands(state, selected)" in evidence_native
    assert count(program, "laplace_query_state_candidate_evidence(") == 2
    assert "proposal top-K cannot hide them" in program

    # No scalar may erase relation identity/evidence topology/structural recurrence.
    assert "typedef struct EvidenceSummary" in program
    assert "positive_covered_occurrences" in program
    assert "negative_covered_occurrences" in program
    assert "positive_relation_families" in program
    assert "negative_relation_families" in program
    assert "positive_channel_compare(" in program
    assert "opposition_summary_compare(" in program
    assert "evidence_summaries_from_channels(" in program
    assert "query_score" not in program
    assert "projection_score" not in program
    assert ".effective" not in program
    assert "candidate.effective" not in program

    assert "generation.forward_frontier_ids(" not in walk.split("DROP FUNCTION IF EXISTS generation.forward_frontier(")[0]
    assert "generation.forward_frontier(p_prompt" not in walk

    drop_frontier = "DROP FUNCTION IF EXISTS generation.forward_frontier(text, integer, integer, integer);"
    drop_trace = "DROP FUNCTION IF EXISTS generation.forward_route_trace(text, integer, integer, integer);"
    assert drop_frontier in walk
    assert drop_trace in walk
    assert walk.index(drop_frontier) < walk.index(drop_trace)

    # Natural chat carries exact session state into this same program.
    natural = re.search(r"IF shape IS NULL THEN(.*?)END IF;", chat, re.S)
    assert natural is not None and "RETURN out;" in natural.group(1)
    call = re.search(r"converse\.forward_turn\(\s*p_prompt,\s*p_session,\s*(\d+),", natural.group(1))
    assert call is not None
    program_sql = function_slice(walk, "converse.forward_turn", "generation.walk_text")
    default_steps = re.search(r"p_steps int DEFAULT (\d+)", program_sql)
    assert default_steps is not None
    steps = int(call.group(1))
    assert steps >= 40 and steps == int(default_steps.group(1))
    assert count(chat, "converse.forward_turn(") == 1
    assert "generation.forward_text(" not in chat
    assert "chat_scaffold" not in natural.group(1)

    print(
        "FORWARD_PROMPT_ANALYSIS_OK "
        f"obligations=native semantic_act=hash-bound realization=completion-gated "
        f"query_state=persistent candidate_evidence=exact evidence=typed-separate "
        f"execution=single-native-program route_owner=native chat_steps={steps}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
