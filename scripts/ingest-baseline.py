#!/usr/bin/env python3
"""Record and compare per-source ingest throughput.

The signal has always existed and nothing ever read it. IngestRunner emits
INGEST_COMPLETE (source, elapsed_s, rows_new, status) per run and INGEST_BATCH
(rate_rows_s) per batch, and both land in $LAPLACE_OPS_LOG_DIR/laplace-cli.csv --
queryable as ops.app_log through file_fdw. ingest-source.sh now emits INGEST_TIMING
with the wall clock at the shell boundary. But no script parsed any of it, no
baseline was written down, and no gate compared against one, so "the seed is slow"
was an opinion for fourteen months.

IngestBaselineGates declares MinWriterRowsPerSecond=500000 and MaxSecondsPerGigabyte=30,
but those are enforced only by Tier=perf tests that CI excludes, and they measure a
4 MiB synthetic buffer -- not a corpus. This measures corpora.

    record   parse logs, write/merge scripts/ingest-baselines.json
    check    parse logs, compare against the baseline, exit 1 on regression
    show     print the current baseline

Input is any mix of files and stdin: a tee'd ingest log, laplace-cli.csv, or the
output of `gh run view --log`. Lines are matched by content, not by file format.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASELINE_PATH = ROOT / "scripts" / "ingest-baselines.json"

# Tolerated slowdown before `check` fails. Wide on purpose: this box is shared, an
# autovacuum or a concurrent CI job perturbs a seed, and a flaky gate teaches people
# to ignore it. It is here to catch a 2x regression, not a 10% one.
DEFAULT_TOLERANCE = 1.5

# Sentinel in the failures list — distinct from the slow (>0) and content-drift (==0)
# buckets so "no baseline" is not reported as "ingested different content".
UNBASELINED = -1.0

# IngestBaselineGates.MinWriterRowsPerSecond. Reported on every line, never enforced
# here: every recorded corpus is already 5x-195x under it (framenet 2,559/s ... unicode
# 94,783/s, measured 2026-08-16), so enforcing would redden the whole seed pipeline in
# one commit. Printing the ratio is what stops the gap from being invisible — a check
# that only compares a source to its own past can catch a regression from slow, never
# slow itself.
ABSOLUTE_FLOOR_ROWS_PER_S = 500_000

COMPLETE_RE = re.compile(
    r"INGEST_COMPLETE\s+source=(?P<source>\S+).*?"
    r"rows_new=(?P<ent>\d+)e\+(?P<phys>\d+)p\+(?P<att>\d+)a\s+"
    r"elapsed_s=(?P<elapsed>[\d.]+).*?status=(?P<status>\S+)"
)
TIMING_RE = re.compile(
    r"INGEST_TIMING\s+source=(?P<source>\S+)\s+elapsed_s=(?P<elapsed>\d+)\s+rc=(?P<rc>\d+)"
)


def normalize(source: str) -> str:
    """The two emitters name the same run differently: ingest-source.sh uses the CLI
    source key ('wiktionary'), IngestRunner uses the decomposer class
    ('WiktionaryDecomposer'). Without folding them, one run produces two baseline
    entries and neither carries both numbers. Where the fold does not apply the keys
    simply stay distinct, which is honest -- better than guessing them equal."""
    s = source[:-len("Decomposer")] if source.endswith("Decomposer") else source
    return s.lower()


def parse(streams) -> dict[str, dict]:
    """Collect the last observation per source. Later lines win: a re-ingest in the
    same log is the fresher measurement, and the idempotency job deliberately runs
    one, so taking the max or the mean would blend a cold seed with a warm no-op."""
    seen: dict[str, dict] = {}
    for stream in streams:
        for line in stream:
            m = COMPLETE_RE.search(line)
            if m:
                rows = int(m["ent"]) + int(m["phys"]) + int(m["att"])
                elapsed = float(m["elapsed"])
                seen.setdefault(normalize(m["source"]), {}).update(
                    source=normalize(m["source"]),
                    decomposer=m["source"],
                    elapsed_s=elapsed,
                    rows_new=rows,
                    status=m["status"],
                    rows_per_s=round(rows / elapsed, 1) if elapsed > 0 else None,
                )
                continue
            m = TIMING_RE.search(line)
            if m:
                # Shell wall clock includes the CLI build and process start, so it is
                # always >= the runner's own elapsed_s. Keep both; they answer different
                # questions ("how long did the seed take" vs "how fast is the writer").
                seen.setdefault(normalize(m["source"]), {}).update(
                    source=normalize(m["source"]),
                    wall_s=int(m["elapsed"]),
                    rc=int(m["rc"]),
                )
    return seen


def load_baseline() -> dict:
    if not BASELINE_PATH.is_file():
        return {}
    return json.loads(BASELINE_PATH.read_text())


def open_inputs(paths):
    if not paths:
        return [sys.stdin]
    streams = []
    for p in paths:
        fp = Path(p)
        if not fp.is_file():
            sys.stderr.write(f"ingest-baseline: no such file: {p}\n")
            raise SystemExit(2)
        streams.append(fp.open(errors="replace"))
    return streams


def cmd_record(args) -> int:
    observed = parse(open_inputs(args.logs))
    if not observed:
        sys.stderr.write("ingest-baseline: no INGEST_COMPLETE or INGEST_TIMING lines found\n")
        return 1
    baseline = load_baseline()
    for source, obs in sorted(observed.items()):
        if obs.get("status") not in (None, "ok"):
            print(f"  skip {source}: status={obs['status']} (not a clean run)")
            continue
        prev = baseline.get(source)
        baseline[source] = obs
        if prev and prev.get("elapsed_s"):
            delta = obs.get("elapsed_s", 0) / prev["elapsed_s"]
            print(f"  {source}: {obs.get('elapsed_s')}s ({delta:.2f}x previous {prev['elapsed_s']}s)")
        elif obs.get("elapsed_s") is not None:
            print(f"  {source}: {obs['elapsed_s']}s (new)")
        else:
            print(f"  {source}: {obs.get('wall_s')}s wall only (no INGEST_COMPLETE in input)")
    BASELINE_PATH.write_text(json.dumps(baseline, indent=2, sort_keys=True) + "\n")
    print(f"wrote {BASELINE_PATH.relative_to(ROOT)} ({len(baseline)} sources)")
    return 0


def cmd_check(args) -> int:
    observed = parse(open_inputs(args.logs))
    baseline = load_baseline()
    if not observed:
        sys.stderr.write("ingest-baseline: no timing lines found in input\n")
        return 1
    if not baseline:
        sys.stderr.write(
            "ingest-baseline: no baseline recorded yet — run `record` first. "
            "Refusing to pass a check with nothing to compare against.\n")
        return 1

    failures = []
    for source, obs in sorted(observed.items()):
        elapsed = obs.get("elapsed_s")
        prev = baseline.get(source)
        if elapsed is None:
            continue
        if not prev or not prev.get("elapsed_s"):
            # WAS A NOTICE, NOW A FAILURE. An empty baseline FILE already refused to pass
            # (above); a missing ENTRY printed a line and continued, so the largest corpus
            # in the manifest was the one source the gate could not fail on. Measured
            # 2026-08-16: conceptnet ran 43m26s at ~2,753 rows/s with no baseline recorded,
            # and this step went green. --allow-unbaselined is the deliberate first-run
            # escape hatch; it must be typed, not defaulted.
            if args.allow_unbaselined:
                print(f"  {source}: {elapsed}s (no baseline — allowed by --allow-unbaselined)")
                continue
            print(f"  NEW  {source}: {elapsed}s with no recorded baseline — "
                  f"run `ingest-baseline.py record` or pass --allow-unbaselined")
            failures.append((source, UNBASELINED))
            continue
        ratio = elapsed / prev["elapsed_s"]
        mark = "ok " if ratio <= args.tolerance else "SLOW"
        rows = obs.get("rows_new")
        floor = ""
        if rows and elapsed:
            rate = rows / elapsed
            floor = (f"  [{rate:,.0f} rows/s = "
                     f"{ABSOLUTE_FLOOR_ROWS_PER_S / rate:.0f}x under the "
                     f"{ABSOLUTE_FLOOR_ROWS_PER_S:,}/s IngestBaselineGates floor]")
        print(f"  {mark} {source}: {elapsed}s vs baseline {prev['elapsed_s']}s "
              f"({ratio:.2f}x){floor}")
        if ratio > args.tolerance:
            failures.append((source, ratio))

    # ROW COUNTS ARE THE CORRECTNESS CHECK, and they matter more than the timings.
    # Ids are content hashes, so re-seeding the same files from scratch must produce the
    # same rows_new. A changed count means the run ingested DIFFERENT CONTENT -- which is
    # exactly what a refactor of per-source file RESOLUTION can silently cause, and no
    # timing comparison would ever show it.
    for source, obs in sorted(observed.items()):
        rows = obs.get("rows_new")
        prev = baseline.get(source)
        if rows is None or not prev or not prev.get("rows_new"):
            continue
        if rows != prev["rows_new"]:
            delta = rows - prev["rows_new"]
            print(f"  ROWS {source}: {rows:,} vs baseline {prev['rows_new']:,} "
                  f"({delta:+,}) — different content ingested, not a timing difference")
            failures.append((source, 0.0))

    if args.max_seconds:
        for source, obs in sorted(observed.items()):
            elapsed = obs.get("elapsed_s")
            if elapsed is not None and elapsed > args.max_seconds:
                print(f"  OVER {source}: {elapsed}s exceeds --max-seconds {args.max_seconds}")
                failures.append((source, elapsed / args.max_seconds))

    if failures:
        slow = [f for f in failures if f[1] > 0.0]
        drift = [f for f in failures if f[1] == 0.0]
        unbaselined = [f for f in failures if f[1] == UNBASELINED]
        parts = []
        if slow:
            parts.append(f"{len(slow)} slower than {args.tolerance}x")
        if drift:
            parts.append(f"{len(drift)} ingested different content")
        if unbaselined:
            parts.append(f"{len(unbaselined)} with no recorded baseline")
        sys.stderr.write("ingest-baseline: " + "; ".join(parts) + "\n")
        return 1
    print("ingest-baseline: OK")
    return 0


def cmd_show(_args) -> int:
    baseline = load_baseline()
    if not baseline:
        print("no baseline recorded")
        return 0
    for source, obs in sorted(baseline.items()):
        elapsed = obs.get("elapsed_s")
        wall = obs.get("wall_s")
        rate = obs.get("rows_per_s")
        shown = f"{elapsed:.1f}s" if elapsed is not None else (
            f"{wall}s wall" if wall is not None else "no timing")
        print(f"  {source:16} {shown:>14}  {obs.get('rows_new', 0):>12} rows  "
              f"{rate if rate is not None else '-'} rows/s")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    rec = sub.add_parser("record", help="write/merge the baseline from logs")
    rec.add_argument("logs", nargs="*", help="log files (default: stdin)")
    rec.set_defaults(fn=cmd_record)

    chk = sub.add_parser("check", help="compare logs against the baseline")
    chk.add_argument("logs", nargs="*", help="log files (default: stdin)")
    chk.add_argument("--tolerance", type=float, default=DEFAULT_TOLERANCE,
                     help=f"allowed slowdown ratio (default {DEFAULT_TOLERANCE})")
    chk.add_argument("--max-seconds", type=float, default=None,
                     help="also fail any source slower than this many seconds")
    chk.add_argument("--allow-unbaselined", action="store_true",
                     help="pass a source that has no recorded baseline (first run of a "
                          "new corpus); without it, an unbaselined source FAILS")
    chk.set_defaults(fn=cmd_check)

    show = sub.add_parser("show", help="print the recorded baseline")
    show.set_defaults(fn=cmd_show)

    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
