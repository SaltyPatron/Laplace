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

    route_ids = function_slice(
        frontier,
        "generation.forward_route_trace_ids",
        "generation.forward_frontier_ids",
    )
    frontier_ids = function_slice(
        frontier,
        "generation.forward_frontier_ids",
        None,
    )

    # The native crawl has one owner. ID-facing helpers consume resolved operands;
    # they must never re-enter text analysis.
    assert count(frontier, "consensus.explore_web(") == 1, \
        "forward route/frontier must own exactly one explore_web crawl body"
    assert "converse.prompt_" not in frontier, \
        "ID-facing routing module must not analyze prompt text"
    assert "generation.forward_route_trace_ids(" in frontier_ids
    assert "generation.forward_route_trace(" not in frontier
    assert "generation.forward_frontier(" not in frontier
    assert count(route_ids, "consensus.explore_web(") == 1

    # Exact observation identity precedes routing. The retired prompt_state and
    # coherence heuristics must not rewrite the native prompt-tree operand.
    assert count(walk, "converse.prompt_operands(p_prompt)") == 1, \
        "forward_text must resolve the exact prompt tree exactly once"
    assert "converse.prompt_state(" not in walk
    assert "converse.prompt_coherence(" not in walk
    assert "context_ids AS ids FROM observation" in walk
    assert "seed_ids AS ids FROM observation" in walk
    native = (ROOT / "extension/laplace_substrate/src/content_resolve.c").read_text()
    operands = native.split("pg_laplace_prompt_operands(PG_FUNCTION_ARGS)", 1)[1].split(
        "pg_laplace_prompt_tree(PG_FUNCTION_ARGS)", 1)[0]
    assert count(operands, "laplace_content_tree_build_public(") == 1
    assert "seed_ids[0] = root" in operands
    assert "content_witness_tree_root_id(tree, &root)" in operands
    assert "generation.forward_frontier_ids(" in walk
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
        f"forward_text=exact_tree1 route_owner=ids retired_text_wrappers=2 chat_steps={steps}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
