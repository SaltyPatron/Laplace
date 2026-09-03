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
    # Include the opening parenthesis so prefix-related function names such as
    # forward_route_trace and forward_route_trace_ids cannot alias each other.
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
        "generation.forward_route_trace",
    )
    route_text = function_slice(
        frontier,
        "generation.forward_route_trace",
        "generation.forward_frontier_ids",
    )
    frontier_ids = function_slice(
        frontier,
        "generation.forward_frontier_ids",
        "generation.forward_frontier",
    )
    frontier_text = function_slice(
        frontier,
        "generation.forward_frontier",
        None,
    )

    # The native crawl has one owner. ID-facing helpers consume resolved operands;
    # they must never re-enter text analysis.
    assert count(frontier, "consensus.explore_web(") == 1, \
        "forward route/frontier must own exactly one explore_web crawl body"
    assert "converse.prompt_" not in route_ids, \
        "forward_route_trace_ids must not analyze prompt text"
    assert "converse.prompt_" not in frontier_ids, \
        "forward_frontier_ids must not analyze prompt text"
    assert "generation.forward_route_trace_ids(" in frontier_ids
    assert "generation.forward_route_trace(" not in frontier_ids

    # Text compatibility surfaces may analyze their prompt once each, but must
    # immediately delegate to the ID-facing execution owner.
    for label, body, delegate in (
        ("forward_route_trace", route_text, "generation.forward_route_trace_ids("),
        ("forward_frontier", frontier_text, "generation.forward_frontier_ids("),
    ):
        assert count(body, "converse.prompt_state(p_prompt)") == 1, \
            f"{label} must evaluate prompt_state exactly once"
        assert count(body, "converse.prompt_coherence(p_prompt)") == 1, \
            f"{label} must evaluate prompt_coherence exactly once"
        assert delegate in body, f"{label} does not delegate to ID-facing owner"

    # The hot path is the important one: one state + one coherence evaluation,
    # then forward_frontier_ids. Calling the text wrapper here would recreate the
    # previous nested analysis multiplier.
    assert count(walk, "converse.prompt_state(p_prompt)") == 1, \
        "forward_text must evaluate prompt_state exactly once"
    assert count(walk, "converse.prompt_coherence(p_prompt)") == 1, \
        "forward_text must evaluate prompt_coherence exactly once"
    assert "generation.forward_frontier_ids(" in walk
    assert "generation.forward_frontier(p_prompt" not in walk

    # Performance work must not masquerade as a smaller generation request. Both
    # default dynamic branches in converse.chat still request the established
    # forty-step S6 -> S7 -> S8 pass.
    assert count(chat, "p_prompt, 40, 5, 0.6, 10") == 2, \
        "default converse.chat forward-pass length changed from 40 steps"

    print(
        "FORWARD_PROMPT_ANALYSIS_OK "
        "forward_text=state1/coherence1 route_owner=ids chat_steps=40"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
