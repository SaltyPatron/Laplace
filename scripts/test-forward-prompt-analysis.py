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

    # The hot path owns one text analysis: one state + one coherence evaluation,
    # then forward_frontier_ids. Calling a text routing wrapper here would recreate
    # the previous nested analysis multiplier.
    assert count(walk, "converse.prompt_state(p_prompt)") == 1, \
        "forward_text must evaluate prompt_state exactly once"
    assert count(walk, "converse.prompt_coherence(p_prompt)") == 1, \
        "forward_text must evaluate prompt_coherence exactly once"
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

    # Performance work must not masquerade as a smaller generation request. Both
    # default dynamic branches in converse.chat still request the established
    # forty-step S6 -> S7 -> S8 pass.
    assert count(chat, "p_prompt, 40, 5, 0.6, 10") == 2, \
        "default converse.chat forward-pass length changed from 40 steps"

    print(
        "FORWARD_PROMPT_ANALYSIS_OK "
        "forward_text=state1/coherence1 route_owner=ids retired_text_wrappers=2 chat_steps=40"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
