#!/usr/bin/env python3
"""Source contract for the dynamic forward-pass prompt-analysis topology."""
from __future__ import annotations

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
FRONTIER_PATH = ROOT / "extension/laplace_substrate/sql/functions/generation/forward_frontier.sql.in"
WALK_PATH = ROOT / "extension/laplace_substrate/sql/functions/generation/walk_text.sql.in"
CHAT_PATH = ROOT / "extension/laplace_substrate/sql/functions/converse/chat.sql.in"


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
    chat = strip_sql_comments(CHAT_PATH.read_text())

    # Native execution owns the crawl; obsolete SQL wrappers must not remain
    # installed as disconnected alternatives.
    assert "CREATE OR REPLACE FUNCTION" not in frontier
    for retired in (
        "generation.forward_frontier_ids(bytea[], integer, integer, integer)",
        "generation.forward_route_trace_ids(bytea[], integer, integer, integer)",
        "converse.prompt_operands(text)",
    ):
        assert f"DROP FUNCTION IF EXISTS {retired};" in walk

    # Exact observation identity precedes routing. The retired prompt_state and
    # coherence heuristics must not rewrite the native prompt-tree operand.
    assert count(walk, "generation.forward_prompt(") == 1, \
        "forward_text must invoke the shared native whole-prompt program once"
    assert "converse.prompt_state(" not in walk
    assert "converse.prompt_coherence(" not in walk
    assert "converse.prompt_operands(" not in walk.split("DROP FUNCTION IF EXISTS generation.forward_frontier(")[0]
    native = (ROOT / "extension/laplace_substrate/src/content_resolve.c").read_text()
    operands = native.split("laplace_prompt_input(text *input)", 1)[1].split(
        "pg_laplace_prompt_tree(PG_FUNCTION_ARGS)", 1)[0]
    assert count(operands, "laplace_content_tree_build_public(") == 1
    assert "seed_ids[0] = root" in operands
    assert "content_witness_tree_root_id(tree, &root)" in operands
    program = (ROOT / "extension/laplace_substrate/src/trajectory_generate.c").read_text()
    entry = program.split("pg_laplace_forward_prompt(PG_FUNCTION_ARGS)", 1)[1]
    assert count(entry, "laplace_prompt_input(") == 1
    assert count(entry, "laplace_explore_web(") == 1
    assert "walk_continuations(walk_call, input)" in entry
    assert "laplace_trajectory_scope_bind_input(trajectory_scope, input)" in program
    assert "generation.forward_frontier_ids(" not in walk.split("DROP FUNCTION IF EXISTS generation.forward_frontier(")[0]
    assert "generation.forward_frontier(p_prompt" not in walk

    # The old zero-caller text routing functions are not allowed to survive an
    # upgrade as hidden installed API. forward_text must be rebound first, then
    # retire frontier before route-trace so recorded BEGIN ATOMIC dependencies are
    # removed in RESTRICT-safe order.
    drop_frontier = "DROP FUNCTION IF EXISTS generation.forward_frontier(text, integer, integer, integer);"
    drop_trace = "DROP FUNCTION IF EXISTS generation.forward_route_trace(text, integer, integer, integer);"
    assert drop_frontier in walk, "retired text forward_frontier is not dropped"
    assert drop_trace in walk, "retired text forward_route_trace is not dropped"
    assert walk.index(drop_frontier) < walk.index(drop_trace), \
        "retired route functions are not dropped in dependency order"

    # Natural chat must carry its session into the same program as HTTP/MCP.
    # It returns before the explicit inspection shapes elect a topic. Keep the
    # established minimum output budget and agree with the program's default;
    # dead legacy branches must not be counted as implemented generation paths.
    natural = re.search(r"IF shape IS NULL THEN(.*?)END IF;", chat, re.S)
    assert natural is not None and "RETURN out;" in natural.group(1)
    call = re.search(r"converse\.forward_turn\(\s*p_prompt,\s*p_session,\s*(\d+),", natural.group(1))
    assert call is not None, "natural chat must preserve session context"
    program = function_slice(walk, "converse.forward_turn", "generation.walk_text")
    default_steps = re.search(r"p_steps int DEFAULT (\d+)", program)
    assert default_steps is not None
    steps = int(call.group(1))
    assert steps >= 40 and steps == int(default_steps.group(1)), \
        "natural chat must use the shared program's full default output budget"
    assert count(chat, "converse.forward_turn(") == 1
    assert "generation.forward_text(" not in chat
    assert "chat_scaffold" not in natural.group(1)

    print(
        "FORWARD_PROMPT_ANALYSIS_OK "
        f"forward_text=exact_tree1 route_owner=native retired_wrappers=5 chat_steps={steps}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
