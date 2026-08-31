#!/usr/bin/env python3
"""Read or deliberately accept per-source ingest throughput baselines.

The authority is the substrate, not Actions logs and not a repository JSON file.
Every IngestRunner entry point closes the same ingest_run_journal row; the journal
trigger records the rows/s observation and comparison there. This script is only
the CI/operator reader over that state.

Compatibility: existing workflow calls pass a detail-log path. The file contents are
never parsed; the basename is used only to resolve the source key. This keeps CI on
the same verdict used by CLI/UI/API/MCP without requiring an entry-point-specific path.

    check  <source-or-log>...  fail on slow/unmeasured/unbaselined or zero comparisons
    record <source-or-log>...  explicitly accept the latest measured clean run
    show                       display accepted substrate baselines
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GATES = ROOT / "scripts" / "decomposer-gates.json"
LOG_BASENAME_RE = re.compile(r"^laplace-ingest-(?P<key>.+)\.log$")


def _manifest() -> dict:
    try:
        return json.loads(GATES.read_text()).get("sources", {})
    except (OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"ingest-baseline: cannot read {GATES}: {exc}") from exc


def resolve_source(hint: str) -> str:
    """Resolve a CLI key/log path to the journal's declared decomposer source name."""
    raw = Path(hint).name
    m = LOG_BASENAME_RE.match(raw)
    key = m.group("key") if m else hint
    entry = _manifest().get(key)
    if entry and entry.get("decomposer"):
        return str(entry["decomposer"])
    # Direct operator use may already name the journal source.
    return key


def _psql(sql: str, *, source: str | None = None) -> str:
    host = os.environ.get("PGHOST", "/var/run/postgresql")
    user = os.environ.get("PGUSER", "laplace_admin")
    db = os.environ.get("LAPLACE_DBNAME") or os.environ.get("PGDATABASE") or "laplace"
    cmd = [
        "psql", "-X", "-qAt", "-F", "\t", "-v", "ON_ERROR_STOP=1",
        "-h", host, "-U", user, "-d", db,
    ]
    if source is not None:
        cmd += ["-v", f"source={source}"]
    proc = subprocess.run(cmd, input=sql, text=True, capture_output=True)
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout).strip()
        raise RuntimeError(f"psql failed for {db}@{host}: {detail}")
    return proc.stdout.strip()


def _status(source: str) -> dict[str, str | float | bool | None]:
    row = _psql(
        """
SELECT source,
       coalesce(last_run_status, ''),
       coalesce(throughput_status, ''),
       throughput_compared::text,
       coalesce(throughput_rows_per_s::text, ''),
       coalesce(throughput_baseline_rows_per_s::text, ''),
       coalesce(throughput_slowdown_ratio::text, '')
FROM ops.source_status(:'source');
""",
        source=source,
    )
    if not row:
        raise RuntimeError(f"ops.source_status returned no row for {source}")
    fields = row.split("\t")
    if len(fields) != 7:
        raise RuntimeError(f"unexpected ops.source_status row for {source}: {row!r}")
    src, run_status, verdict, compared, rate, baseline, ratio = fields
    return {
        "source": src,
        "last_run_status": run_status or None,
        "throughput_status": verdict or None,
        "throughput_compared": compared == "t",
        "rows_per_s": float(rate) if rate else None,
        "baseline_rows_per_s": float(baseline) if baseline else None,
        "slowdown_ratio": float(ratio) if ratio else None,
    }


def _fmt(value: float | None) -> str:
    return "-" if value is None else f"{value:,.1f}"


def cmd_check(args: argparse.Namespace) -> int:
    if not args.sources:
        print("ingest-baseline: check requires a source or compatibility log path", file=sys.stderr)
        return 2

    failed = 0
    observed = 0
    compared = 0
    for hint in args.sources:
        source = resolve_source(hint)
        try:
            s = _status(source)
        except RuntimeError as exc:
            print(f"ingest-baseline: {exc}", file=sys.stderr)
            failed += 1
            continue

        observed += 1
        verdict = s["throughput_status"]
        ratio = s["slowdown_ratio"]
        did_compare = bool(s["throughput_compared"])
        if did_compare:
            compared += 1

        detail = (
            f"{source}: rate={_fmt(s['rows_per_s'])} rows/s "
            f"baseline={_fmt(s['baseline_rows_per_s'])} rows/s "
            f"slowdown={('-' if ratio is None else f'{ratio:.2f}x')} "
            f"last_run={s['last_run_status'] or '-'}"
        )

        if verdict == "slow":
            print(f"  SLOW {detail}")
            failed += 1
        elif verdict == "unmeasured" or verdict is None:
            print(f"  MISS {detail}")
            failed += 1
        elif verdict in ("unbaselined", "baseline-established"):
            # `baseline-established` is retained only as a defensive read of an
            # intermediate-schema journal row. Current substrate code emits
            # `unbaselined` and never lets a measurement accept itself.
            print(
                f"  MISS {detail} (no accepted baseline; run `ingest-baseline.py record` "
                "deliberately, then measure again)"
            )
            failed += 1
        elif verdict == "ok":
            if not did_compare:
                print(f"  MISS {detail} (ok verdict without a comparison)")
                failed += 1
            else:
                print(f"  ok   {detail}")
        else:
            print(f"  MISS {detail} (unknown verdict {verdict!r})")
            failed += 1

    # The original false-green was exactly a successful command that compared
    # nothing. Keep this aggregate invariant even though every unbaselined or
    # unmeasured source is already a per-source failure above.
    if failed == 0 and compared == 0:
        print(
            f"ingest-baseline: FAILED (observed={observed} compared=0 failed=0)",
            file=sys.stderr,
        )
        return 1
    if failed:
        print(
            f"ingest-baseline: FAILED (observed={observed} compared={compared} failed={failed})",
            file=sys.stderr,
        )
        return 1
    print(f"ingest-baseline: OK (observed={observed} compared={compared})")
    return 0


def cmd_record(args: argparse.Namespace) -> int:
    if not args.sources:
        print("ingest-baseline: record requires a source or compatibility log path", file=sys.stderr)
        return 2
    failed = 0
    for hint in args.sources:
        source = resolve_source(hint)
        try:
            row = _psql(
                "SELECT source_name, baseline_rows_per_s, accepted_run_id "
                "FROM ops.ingest_throughput_accept(:'source');\n",
                source=source,
            )
        except RuntimeError as exc:
            print(f"ingest-baseline: {exc}", file=sys.stderr)
            failed += 1
            continue
        print(f"accepted {row}")
    return 1 if failed else 0


def cmd_show(_args: argparse.Namespace) -> int:
    try:
        rows = _psql(
            """
SELECT source_name,
       round(baseline_rows_per_s::numeric, 1),
       baseline_rows,
       baseline_elapsed_ms,
       coalesce(round(last_rows_per_s::numeric, 1)::text, '-'),
       coalesce(last_status, '-')
FROM laplace.ingest_throughput_baseline
ORDER BY source_name;
"""
        )
    except RuntimeError as exc:
        print(f"ingest-baseline: {exc}", file=sys.stderr)
        return 1
    print(rows if rows else "no substrate throughput baselines")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    check = sub.add_parser("check", help="read the substrate verdict and fail on regression")
    check.add_argument("sources", nargs="*", help="source key/name or compatibility detail-log path")
    # Retain historical flags as parser compatibility. The substrate owns tolerance
    # and elapsed boundaries now; CI cannot override them from a shell command.
    check.add_argument("--tolerance", type=float, default=None, help=argparse.SUPPRESS)
    check.add_argument("--max-seconds", type=float, default=None, help=argparse.SUPPRESS)
    check.add_argument("--require-comparison", action="store_true", help=argparse.SUPPRESS)
    check.set_defaults(fn=cmd_check)

    record = sub.add_parser("record", help="explicitly accept the latest measured clean run")
    record.add_argument("sources", nargs="*", help="source key/name or compatibility detail-log path")
    record.set_defaults(fn=cmd_record)

    show = sub.add_parser("show", help="show substrate-owned accepted baselines")
    show.set_defaults(fn=cmd_show)

    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
