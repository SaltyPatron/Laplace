#!/usr/bin/env python3
"""Benchmark the installed canonical Laplace forward program with work receipts.

This is deliberately database-backed and refuses to measure a runtime whose
installed extension/execution-module identities do not match the exact source
revision built by the benchmark workflow.

One measured iteration has three distinct phases:
  1. PRE-FLIGHT: EXPLAIN (FORMAT JSON), no execution.
  2. MEASURE: EXPLAIN (ANALYZE, BUFFERS, WAL, FORMAT JSON) of the exact same
     generation.forward_program invocation.
  3. RECEIPT REPLAY: execute the same deterministic seeded read once to retain
     the program's typed route/emit/completion receipt.  The world write epoch
     must stay unchanged and the measured row count must equal the replay row
     count. Replay time is recorded separately and is not counted as measured
     execution time.

The benchmark does not turn PostgreSQL planner cost into currency and does not
pretend output text length describes the work.  It records declared hops/fanout,
planner rows/cost, actual function rows/buffers/WAL, routing/candidate/evidence
receipt fields, final semantic fingerprint, and plan-vs-actual row error.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CASES = ROOT / "scripts/benchmark-forward-cases.json"
DEFAULT_CONTROL = ROOT / "build/extension/laplace_substrate/laplace_substrate.control"
DEFAULT_EXEC_MODULE = ROOT / "build/extension/laplace_substrate/laplace_execution_module.txt"


def git_sha() -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, check=True,
        text=True, capture_output=True,
    ).stdout.strip()


def sql_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def psql_args(database: str) -> list[str]:
    return [
        "psql", "-X",
        "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
        "-p", os.environ.get("PGPORT", "5432"),
        "-U", os.environ.get("PGUSER", "laplace_admin"),
        "-d", database,
        "-v", "ON_ERROR_STOP=1",
        "-A", "-t", "-q",
    ]


def run_psql(database: str, sql: str, timeout_seconds: int = 180) -> tuple[str, int]:
    wrapped = (
        "SET default_transaction_read_only=on;\n"
        f"SET statement_timeout='{timeout_seconds}s';\n"
        + sql.strip()
    )
    started = time.perf_counter_ns()
    try:
        proc = subprocess.run(
            psql_args(database) + ["-c", wrapped], cwd=ROOT,
            text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=timeout_seconds + 30,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"psql timed out after {timeout_seconds}s") from exc
    wall_ns = time.perf_counter_ns() - started
    if proc.returncode != 0:
        raise RuntimeError(
            f"psql exited {proc.returncode}\nstdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"
        )
    return proc.stdout.strip(), wall_ns


def run_json(database: str, sql: str, timeout_seconds: int = 180) -> tuple[Any, int]:
    text, wall_ns = run_psql(database, sql, timeout_seconds)
    if not text:
        raise RuntimeError("psql returned no JSON")
    try:
        return json.loads(text), wall_ns
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"invalid JSON from psql: {text[:1000]!r}") from exc


def load_cases(path: Path) -> list[dict[str, Any]]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("schema_version") != 1:
        raise ValueError("forward benchmark case schema_version must be 1")
    cases = document.get("cases")
    if not isinstance(cases, list) or not cases:
        raise ValueError("forward benchmark requires at least one case")
    ids: set[str] = set()
    required = {"id", "prompt", "steps", "max_stride", "spread", "top_k", "seed", "hops", "fanout"}
    for case in cases:
        if not isinstance(case, dict) or not required <= set(case):
            raise ValueError(f"invalid forward benchmark case: {case!r}")
        case_id = case["id"]
        if not isinstance(case_id, str) or not case_id or case_id in ids:
            raise ValueError(f"duplicate/invalid forward benchmark case id {case_id!r}")
        ids.add(case_id)
        if not isinstance(case["prompt"], str) or not case["prompt"]:
            raise ValueError(f"case {case_id}: prompt must be non-empty")
        for field in ("steps", "max_stride", "top_k", "hops", "fanout"):
            if not isinstance(case[field], int) or case[field] < 0:
                raise ValueError(f"case {case_id}: {field} must be a non-negative integer")
        if case["steps"] <= 0 or case["top_k"] <= 0:
            raise ValueError(f"case {case_id}: steps/top_k must be positive")
        if not isinstance(case["spread"], (int, float)) or float(case["spread"]) < 0:
            raise ValueError(f"case {case_id}: spread must be non-negative")
        if not isinstance(case["seed"], int):
            raise ValueError(f"case {case_id}: seed must be an integer")
    return cases


def parse_built_binding(control: Path, execution_module_file: Path) -> dict[str, str]:
    if not control.is_file():
        raise FileNotFoundError(f"built substrate control missing: {control}")
    if not execution_module_file.is_file():
        raise FileNotFoundError(f"built execution module identity missing: {execution_module_file}")
    control_text = control.read_text(encoding="utf-8")
    match = re.search(r"^default_version\s*=\s*'([^']+)'\s*$", control_text, re.MULTILINE)
    if not match:
        raise ValueError(f"could not parse default_version from {control}")
    module = execution_module_file.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"laplace_execution_[0-9a-f]{16}", module):
        raise ValueError(f"unexpected execution-module identity {module!r}")
    return {"extension_version": match.group(1), "execution_module": module}


def runtime_binding_sql() -> str:
    return """
SELECT json_build_object(
  'extension_version', (SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'),
  'forward_function_count', (
      SELECT count(*) FROM pg_proc p
      JOIN pg_namespace n ON n.oid=p.pronamespace
      WHERE n.nspname='generation' AND p.proname='forward_program'
        AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='c')
  ),
  'forward_libraries', COALESCE((
      SELECT json_agg(DISTINCT p.probin ORDER BY p.probin)
      FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
      WHERE n.nspname='generation' AND p.proname='forward_program'
        AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='c')
  ), '[]'::json)
)::text;
"""


def verify_runtime_binding(database: str, expected: dict[str, str], timeout: int) -> dict[str, Any]:
    actual, wall_ns = run_json(database, runtime_binding_sql(), timeout)
    actual["probe_wall_nanoseconds"] = wall_ns
    if actual.get("extension_version") != expected["extension_version"]:
        raise RuntimeError(
            "installed laplace_substrate version does not match exact built target: "
            f"installed={actual.get('extension_version')!r} built={expected['extension_version']!r}"
        )
    libraries = actual.get("forward_libraries") or []
    if int(actual.get("forward_function_count") or 0) != 1 or libraries != [expected["execution_module"]]:
        raise RuntimeError(
            "generation.forward_program is not bound to the exact built execution module: "
            f"count={actual.get('forward_function_count')} libraries={libraries!r} "
            f"expected={expected['execution_module']!r}"
        )
    return actual


def world_snapshot_sql() -> str:
    return """
SELECT json_build_object(
  'observed_at', clock_timestamp(),
  'apply_write_epoch', CASE
      WHEN to_regclass('laplace.apply_write_epoch') IS NULL THEN NULL
      ELSE (SELECT last_value FROM laplace.apply_write_epoch) END,
  'entities', (SELECT count(*) FROM laplace.entities),
  'physicalities', (SELECT count(*) FROM laplace.physicalities),
  'attestations', (SELECT count(*) FROM laplace.attestations),
  'consensus', (SELECT count(*) FROM laplace.consensus)
)::text;
"""


def snapshot_world(database: str, timeout: int) -> dict[str, Any]:
    value, wall_ns = run_json(database, world_snapshot_sql(), timeout)
    value["probe_wall_nanoseconds"] = wall_ns
    return value


def stable_world(before: dict[str, Any], after: dict[str, Any]) -> bool:
    keys = ("apply_write_epoch", "entities", "physicalities", "attestations", "consensus")
    return all(before.get(key) == after.get(key) for key in keys)


def forward_call(case: dict[str, Any]) -> str:
    return (
        "generation.forward_program("
        f"{sql_literal(case['prompt'])},"
        f"{int(case['steps'])},"
        f"{int(case['max_stride'])},"
        f"{float(case['spread']):.17g},"
        f"{int(case['top_k'])},"
        f"{int(case['seed'])}::bigint,"
        f"{int(case['hops'])},"
        f"{int(case['fanout'])},"
        "NULL::bytea[],NULL::bytea[])"
    )


def explain(database: str, call: str, analyze: bool, timeout: int) -> tuple[dict[str, Any], int]:
    opts = "ANALYZE TRUE, BUFFERS TRUE, WAL TRUE, TIMING TRUE, SUMMARY TRUE, FORMAT JSON" if analyze \
        else "COSTS TRUE, VERBOSE FALSE, FORMAT JSON"
    document, wall_ns = run_json(database, f"EXPLAIN ({opts}) SELECT * FROM {call};", timeout)
    if not isinstance(document, list) or len(document) != 1 or not isinstance(document[0], dict):
        raise RuntimeError("unexpected EXPLAIN JSON shape")
    return document[0], wall_ns


def plan_metrics(explain_doc: dict[str, Any], analyzed: bool) -> dict[str, Any]:
    plan = explain_doc.get("Plan") or {}
    result: dict[str, Any] = {
        "node_type": plan.get("Node Type"),
        "startup_cost": plan.get("Startup Cost"),
        "total_cost": plan.get("Total Cost"),
        "plan_rows": plan.get("Plan Rows"),
        "plan_width": plan.get("Plan Width"),
    }
    if analyzed:
        for key in (
            "Actual Startup Time", "Actual Total Time", "Actual Rows", "Actual Loops",
            "Shared Hit Blocks", "Shared Read Blocks", "Shared Dirtied Blocks", "Shared Written Blocks",
            "Local Hit Blocks", "Local Read Blocks", "Local Dirtied Blocks", "Local Written Blocks",
            "Temp Read Blocks", "Temp Written Blocks", "I/O Read Time", "I/O Write Time",
            "WAL Records", "WAL FPI", "WAL Bytes",
        ):
            if key in plan:
                result[key.lower().replace(" ", "_").replace("/", "_")] = plan[key]
        for key in ("Planning Time", "Execution Time"):
            if key in explain_doc:
                result[key.lower().replace(" ", "_")] = explain_doc[key]
    return result


def trace_sql(call: str) -> str:
    return f"SELECT COALESCE(json_agg(to_jsonb(p)), '[]'::json)::text FROM {call} p;"


def trace_fingerprint(terminal: dict[str, Any] | None) -> dict[str, Any]:
    if terminal is None:
        return {
            "program_id": None, "output_fingerprint": None, "semantic_act_id": None,
            "completion": None, "disposition": None, "output_count": None,
        }
    return {
        "program_id": terminal.get("program_id"),
        "output_fingerprint": terminal.get("output_fingerprint"),
        "semantic_act_id": terminal.get("semantic_act_id"),
        "completion": terminal.get("completion"),
        "disposition": terminal.get("disposition"),
        "output_count": terminal.get("output_count"),
    }


def summarize_trace(rows: list[dict[str, Any]]) -> dict[str, Any]:
    events = Counter(str(row.get("event")) for row in rows)
    terminal = next((row for row in reversed(rows) if row.get("event") in {"complete", "unresolved"}), None)
    by_round: dict[int, dict[str, int]] = defaultdict(lambda: {
        "rows": 0,
        "route_rows": 0,
        "emit_rows": 0,
        "max_candidates": 0,
        "max_ordered_context": 0,
        "max_proposal_channels": 0,
        "max_exact_channels": 0,
        "sequence_occurrences_sum": 0,
        "max_covered_occurrences": 0,
        "max_relation_families": 0,
        "max_opposed_occurrences": 0,
        "max_support_sources": 0,
        "max_support_contexts": 0,
    })
    for row in rows:
        rr = int(row.get("routing_round") or 0)
        bucket = by_round[rr]
        bucket["rows"] += 1
        event = row.get("event")
        bucket["route_rows"] += 1 if event == "route" else 0
        bucket["emit_rows"] += 1 if event == "emit" else 0
        bucket["max_candidates"] = max(bucket["max_candidates"], int(row.get("candidate_count") or 0))
        bucket["max_ordered_context"] = max(bucket["max_ordered_context"], int(row.get("ordered_context_count") or 0))
        bucket["max_proposal_channels"] = max(bucket["max_proposal_channels"], int(row.get("proposal_channel_count") or 0))
        bucket["max_exact_channels"] = max(bucket["max_exact_channels"], int(row.get("exact_channel_count") or 0))
        bucket["sequence_occurrences_sum"] += int(row.get("sequence_occurrences") or 0)
        bucket["max_covered_occurrences"] = max(bucket["max_covered_occurrences"], int(row.get("covered_occurrences") or 0))
        bucket["max_relation_families"] = max(bucket["max_relation_families"], int(row.get("relation_families") or 0))
        bucket["max_opposed_occurrences"] = max(bucket["max_opposed_occurrences"], int(row.get("opposed_occurrences") or 0))
        bucket["max_support_sources"] = max(bucket["max_support_sources"], int(row.get("support_sources") or 0))
        bucket["max_support_contexts"] = max(bucket["max_support_contexts"], int(row.get("support_contexts") or 0))
    return {
        "trace_rows": len(rows),
        "events": dict(sorted(events.items())),
        "routing_rounds": [dict({"routing_round": rr}, **by_round[rr]) for rr in sorted(by_round)],
        "semantic_fingerprint": trace_fingerprint(terminal),
        "terminal_event": terminal.get("event") if terminal else None,
        "required_obligations": terminal.get("required_obligations") if terminal else None,
        "satisfied_obligations": terminal.get("satisfied_obligations") if terminal else None,
        "remaining_required": terminal.get("remaining_required") if terminal else None,
    }


def row_estimate_error(plan_rows: Any, actual_rows: Any) -> dict[str, Any]:
    planned = float(plan_rows or 0)
    actual = float(actual_rows or 0)
    absolute = actual - planned
    relative = abs(absolute) / max(abs(actual), 1.0)
    return {"planned_rows": planned, "actual_rows": actual, "signed_error": absolute, "relative_absolute_error": relative}


def run_case(database: str, case: dict[str, Any], repeats: int, timeout: int) -> dict[str, Any]:
    call = forward_call(case)
    preflight_doc, preflight_wall = explain(database, call, False, timeout)
    preflight = plan_metrics(preflight_doc, False)
    runs: list[dict[str, Any]] = []
    expected_fingerprint: dict[str, Any] | None = None

    for repeat in range(1, repeats + 1):
        world_before = snapshot_world(database, timeout)
        measured_doc, measured_wall = explain(database, call, True, timeout)
        measured = plan_metrics(measured_doc, True)
        rows, replay_wall = run_json(database, trace_sql(call), timeout)
        if not isinstance(rows, list) or any(not isinstance(row, dict) for row in rows):
            raise RuntimeError(f"case {case['id']}: forward receipt replay did not return row objects")
        world_after = snapshot_world(database, timeout)
        if not stable_world(world_before, world_after):
            raise RuntimeError(
                f"case {case['id']} repeat {repeat}: substrate changed during measurement; "
                f"before={world_before} after={world_after}"
            )
        actual_rows = int(measured.get("actual_rows") or 0)
        if actual_rows != len(rows):
            raise RuntimeError(
                f"case {case['id']} repeat {repeat}: measured Actual Rows={actual_rows} "
                f"but deterministic receipt replay returned {len(rows)}"
            )
        summary = summarize_trace(rows)
        fingerprint = summary["semantic_fingerprint"]
        if expected_fingerprint is None:
            expected_fingerprint = fingerprint
        elif fingerprint != expected_fingerprint:
            raise RuntimeError(
                f"case {case['id']}: semantic fingerprint changed across repeats: "
                f"first={expected_fingerprint!r} repeat={fingerprint!r}"
            )
        execution_ms = float(measured.get("execution_time") or 0.0)
        output_count = int(fingerprint.get("output_count") or 0)
        runs.append({
            "repeat": repeat,
            "world_before": world_before,
            "world_after": world_after,
            "measured_execution": measured,
            "measured_psql_wall_nanoseconds": measured_wall,
            "semantic_receipt_replay_wall_nanoseconds": replay_wall,
            "semantic_receipt_replay": summary,
            "raw_trace": rows,
            "planner_row_estimate_error": row_estimate_error(preflight.get("plan_rows"), actual_rows),
            "semantic_outputs_per_second": (output_count * 1000.0 / execution_ms) if execution_ms > 0 else None,
        })

    return {
        "case": case,
        "preflight": {
            "declared_work_ceiling": {
                "steps": case["steps"], "hops": case["hops"], "fanout": case["fanout"],
                "top_k": case["top_k"], "max_stride": case["max_stride"],
                "spread": case["spread"], "seed": case["seed"],
                "prior_frontier": None, "output_relation_types": None,
            },
            "postgres_planner": preflight,
            "psql_wall_nanoseconds": preflight_wall,
            "planner_cost_is_billing_unit": False,
        },
        "runs": runs,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", default=os.environ.get("PGDATABASE", "laplace"))
    parser.add_argument("--cases", default=str(DEFAULT_CASES))
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--json", required=True)
    parser.add_argument("--control", default=str(DEFAULT_CONTROL))
    parser.add_argument("--execution-module-file", default=str(DEFAULT_EXEC_MODULE))
    parser.add_argument("--statement-timeout", type=int, default=180)
    parser.add_argument("--skip-quiet-check", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.repeats <= 20:
        raise SystemExit("--repeats must be in 1..20")
    if args.statement_timeout < 10:
        raise SystemExit("--statement-timeout must be >= 10")

    if not args.skip_quiet_check:
        subprocess.run(
            ["bash", "scripts/wait-for-quiet-substrate.sh", args.database],
            cwd=ROOT, check=True,
        )

    expected = parse_built_binding(Path(args.control), Path(args.execution_module_file))
    runtime = verify_runtime_binding(args.database, expected, args.statement_timeout)
    cases = load_cases(Path(args.cases))
    estate_before = snapshot_world(args.database, args.statement_timeout)
    started = time.time_ns()
    results = [run_case(args.database, case, args.repeats, args.statement_timeout) for case in cases]
    estate_after = snapshot_world(args.database, args.statement_timeout)
    if not stable_world(estate_before, estate_after):
        raise RuntimeError(f"substrate changed across query benchmark suite: before={estate_before} after={estate_after}")

    receipt = {
        "schema": "laplace.benchmark.forward-program/v1",
        "source_sha": git_sha(),
        "database": args.database,
        "expected_runtime_binding": expected,
        "installed_runtime_binding": runtime,
        "estate_before": estate_before,
        "estate_after": estate_after,
        "measurement_law": {
            "preflight_executes_program": False,
            "measured_execution": "EXPLAIN ANALYZE BUFFERS WAL of exact forward_program call",
            "semantic_receipt": "deterministic read-only replay after measured execution",
            "replay_in_measured_execution_time": False,
            "world_must_remain_stable": True,
            "postgres_planner_cost_is_currency": False,
        },
        "started_unix_nanoseconds": started,
        "finished_unix_nanoseconds": time.time_ns(),
        "results": results,
    }
    output = Path(args.json).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(receipt, indent=2, sort_keys=True, default=str) + "\n", encoding="utf-8")

    best_ms = min(
        float(run["measured_execution"].get("execution_time") or float("inf"))
        for case in results for run in case["runs"]
    )
    print(
        "FORWARD_BENCHMARK_OK "
        f"cases={len(results)} repeats={args.repeats} "
        f"extension={expected['extension_version']} execution={expected['execution_module']} "
        f"best_execution_ms={best_ms:.3f} receipt={output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
