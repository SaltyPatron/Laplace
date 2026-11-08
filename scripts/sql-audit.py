#!/usr/bin/env python3
"""Corpus-scale PostgreSQL clone and static-quality audit.

The audit is deliberately dependency-free.  It understands enough PostgreSQL
lexical structure to keep semicolons inside dollar-quoted function bodies from
splitting statements, and recursively fingerprints those bodies.  This is not
a substitute for PostgreSQL's parser or EXPLAIN: it is the cheap first stage
that finds repeated implementation and high-signal review candidates across
the entire repository.

Outputs are deterministic apart from ``generated_at``.  Finding identifiers
are based on rule, path and normalized statement content so a baseline remains
useful when unrelated lines move.
"""

from __future__ import annotations

import argparse
from collections import Counter
import datetime as dt
import json
import os
from pathlib import Path
import sys
import tokenize  # noqa: F401 - re-exported for test-sql-audit.py
from typing import Sequence

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from sql_audit_rules import (  # noqa: F401
    CloneCluster,
    Finding,
    UnionFind,
    add_finding,
    clone_severity,
    definition_findings,
    downgrade,
    exact_clones,
    flat,
    near_clones,
    null_predicate_comparison,
    qualified_call_pattern,
    query_findings,
    shingles,
    static_findings,
)
from sql_audit_scanners import (  # noqa: F401
    AuditConfig,
    CPP_RAW_OPEN,
    EMBEDDED_SUFFIXES,
    IGNORED_PARTS,
    SEVERITY_RANK,
    SQL_LEADS,
    SQL_SUFFIXES,
    Statement,
    Token,
    VERSION,
    decode_quoted,
    digest,
    dollar_body,
    excluded,
    excluded_directory,
    is_sql_path,
    line_at,
    normalize_tokens,
    python_string_chunks,
    role_for,
    scan_sql,
    source_string_literals,
    split_sql,
    statement_kind,
    string_chunks,
)


def make_statement(
    path: str,
    role: str,
    origin: str,
    raw: str,
    start_line: int,
    end_line: int,
    ordinal: int,
    forced_kind: str | None = None,
) -> Statement | None:
    exact = normalize_tokens(raw, structural=False)
    structural = normalize_tokens(raw, structural=True)
    if not exact:
        return None
    statement = Statement(
        path=path,
        role=role,
        origin=origin,
        kind=statement_kind(exact, forced_kind),
        start_line=start_line,
        end_line=end_line,
        raw=raw,
        exact_tokens=exact,
        structural_tokens=structural,
        ordinal=ordinal,
    )
    statement.id = "statement-" + digest(
        "|".join((path, statement.kind, statement.exact_hash, str(ordinal))), 16
    )
    return statement


def statements_from_sql(
    path: str, role: str, origin: str, text: str, base_line: int = 1
) -> list[Statement]:
    result: list[Statement] = []
    ordinal = 0
    for raw, relative_start, relative_end in split_sql(text):
        ordinal += 1
        start = base_line + relative_start - 1
        end = base_line + relative_end - 1
        statement = make_statement(path, role, origin, raw, start, end, ordinal)
        if statement is None:
            continue
        result.append(statement)
        if statement.kind != "definition":
            continue
        for token in scan_sql(raw):
            if token.kind != "dollar":
                continue
            body = dollar_body(token.text)
            body_base = start + token.line - 1
            for body_raw, body_start, body_end in split_sql(body):
                body_exact = normalize_tokens(body_raw, structural=False)
                if len(body_exact) < 5:
                    continue
                ordinal += 1
                child = make_statement(
                    path,
                    role,
                    origin,
                    body_raw,
                    body_base + body_start - 1,
                    body_base + body_end - 1,
                    ordinal,
                    forced_kind="body",
                )
                if child is not None:
                    result.append(child)
            break
    return result


def discover(root: Path, config: AuditConfig) -> tuple[list[Statement], dict[str, int]]:
    statements: list[Statement] = []
    inventory = Counter()
    for directory, dirnames, filenames in os.walk(root):
        directory_path = Path(directory)
        kept_dirs: list[str] = []
        for name in sorted(dirnames):
            child = directory_path / name
            relative_dir = child.relative_to(root).as_posix()
            if not excluded_directory(relative_dir, config):
                kept_dirs.append(name)
        dirnames[:] = kept_dirs
        for name in sorted(filenames):
            path = directory_path / name
            relative = path.relative_to(root).as_posix()
            if excluded(relative, config):
                continue
            suffix = path.suffix.lower()
            if not is_sql_path(path) and suffix not in EMBEDDED_SUFFIXES:
                continue
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError as exc:
                print(f"sql-audit: cannot read {relative}: {exc}", file=sys.stderr)
                continue
            role = role_for(relative)
            if is_sql_path(path):
                inventory["sql_files"] += 1
                inventory["sql_lines"] += text.count("\n") + (1 if text else 0)
                found = statements_from_sql(relative, role, "file", text)
                inventory["sql_statements"] += sum(s.kind != "body" for s in found)
                inventory["body_statements"] += sum(s.kind == "body" for s in found)
                statements.extend(found)
                continue
            chunks = string_chunks(text, suffix)
            if not chunks:
                continue
            inventory["embedded_source_files"] += 1
            inventory["embedded_fragments"] += len(chunks)
            for index, (sql, start, _) in enumerate(chunks, 1):
                virtual = f"{relative}#sql-{index}"
                found = statements_from_sql(virtual, role, "embedded", sql, start)
                inventory["embedded_statements"] += sum(s.kind != "body" for s in found)
                inventory["body_statements"] += sum(s.kind == "body" for s in found)
                statements.extend(found)
    inventory["audit_units"] = len(statements)
    return statements, dict(inventory)


def read_config(path: Path | None) -> AuditConfig:
    config = AuditConfig()
    if path is None or not path.exists():
        return config
    raw = json.loads(path.read_text(encoding="utf-8"))
    for key, value in raw.items():
        if not hasattr(config, key):
            raise SystemExit(f"sql-audit: unknown config key {key!r}")
        setattr(config, key, value)
    return config


def read_baseline(path: Path | None) -> set[str]:
    if path is None or not path.exists():
        return set()
    raw = json.loads(path.read_text(encoding="utf-8"))
    return set(raw.get("ids", []))


def apply_baseline(
    findings: Sequence[Finding], clusters: Sequence[CloneCluster], ids: set[str]
) -> None:
    for item in [*findings, *clusters]:
        item.baseline = item.id in ids


def statement_json(statement: Statement) -> dict[str, object]:
    return {
        "id": statement.id,
        "path": statement.path,
        "start_line": statement.start_line,
        "end_line": statement.end_line,
        "role": statement.role,
        "origin": statement.origin,
        "kind": statement.kind,
        "token_count": statement.token_count,
        "exact_hash": statement.exact_hash,
        "structural_hash": statement.structural_hash,
        "excerpt": statement.excerpt,
    }


def build_report(
    root: Path,
    inventory: dict[str, int],
    statements: Sequence[Statement],
    findings: Sequence[Finding],
    exact: Sequence[CloneCluster],
    near: Sequence[CloneCluster],
) -> dict[str, object]:
    roles = Counter(statement.role for statement in statements)
    severities = Counter(finding.severity for finding in findings)
    for cluster in [*exact, *near]:
        severities[cluster.severity] += 1
    new_severities = Counter(f.severity for f in findings if not f.baseline)
    for cluster in [*exact, *near]:
        if not cluster.baseline:
            new_severities[cluster.severity] += 1
    return {
        "schema_version": VERSION,
        "generated_at": dt.datetime.now(dt.timezone.utc).isoformat(),
        "root": str(root),
        "inventory": inventory,
        "roles": dict(sorted(roles.items())),
        "summary": {
            "findings": len(findings),
            "exact_clone_clusters": len(exact),
            "near_clone_clusters": len(near),
            "severity": dict(sorted(severities.items())),
            "new_severity": dict(sorted(new_severities.items())),
        },
        "findings": [
            {
                "id": finding.id,
                "baseline": finding.baseline,
                "rule": finding.rule,
                "severity": finding.severity,
                "category": finding.category,
                "title": finding.title,
                "evidence": finding.evidence,
                "recommendation": finding.recommendation,
                "statement": statement_json(finding.statement),
            }
            for finding in findings
        ],
        "exact_clones": [
            {
                "id": cluster.id,
                "baseline": cluster.baseline,
                "severity": cluster.severity,
                "score": cluster.score,
                "members": [statement_json(member) for member in cluster.members],
            }
            for cluster in exact
        ],
        "near_clones": [
            {
                "id": cluster.id,
                "baseline": cluster.baseline,
                "severity": cluster.severity,
                "score": cluster.score,
                "members": [statement_json(member) for member in cluster.members],
            }
            for cluster in near
        ],
    }


def markdown_report(report: dict[str, object], config: AuditConfig) -> str:
    inventory = report["inventory"]
    summary = report["summary"]
    findings = report["findings"]
    exact = report["exact_clones"]
    near = report["near_clones"]
    assert isinstance(inventory, dict) and isinstance(summary, dict)
    assert isinstance(findings, list) and isinstance(exact, list) and isinstance(near, list)
    severity = summary["severity"]
    assert isinstance(severity, dict)
    lines = [
        "# SQL corpus audit",
        "",
        f"Generated `{report['generated_at']}` by `scripts/sql-audit.py` (schema v{VERSION}).",
        "",
        "This report is a static triage surface. A finding is a review candidate, not proof of a defect; "
        "planner and performance findings require representative `EXPLAIN (ANALYZE, BUFFERS)` evidence.",
        "",
        "## Corpus",
        "",
        "| measure | count |",
        "|---|---:|",
    ]
    for key in (
        "sql_files", "sql_lines", "sql_statements", "body_statements",
        "embedded_source_files", "embedded_fragments", "embedded_statements", "audit_units",
    ):
        lines.append(f"| {key.replace('_', ' ')} | {inventory.get(key, 0):,} |")
    lines.extend([
        "",
        "## Triage summary",
        "",
        f"- Static findings: **{summary['findings']:,}**",
        f"- Exact-clone clusters: **{summary['exact_clone_clusters']:,}**",
        f"- Near-clone clusters: **{summary['near_clone_clusters']:,}**",
        f"- Severity: high {severity.get('high', 0):,}, medium {severity.get('medium', 0):,}, "
        f"low {severity.get('low', 0):,}, info {severity.get('info', 0):,}",
        "",
        "## Findings by rule",
        "",
        "| rule | severity | count | title |",
        "|---|---|---:|---|",
    ])
    by_rule: dict[tuple[str, str, str], int] = Counter(
        (str(item["rule"]), str(item["severity"]), str(item["title"])) for item in findings
    )
    for (rule, item_severity, title), count in sorted(
        by_rule.items(), key=lambda item: (-SEVERITY_RANK[item[0][1]], -item[1], item[0][0])
    ):
        lines.append(f"| {rule} | {item_severity} | {count:,} | {title} |")

    lines.extend(["", "## Highest-priority static findings", ""])
    for item in findings[:config.report_limit]:
        statement = item["statement"]
        assert isinstance(statement, dict)
        marker = " *(baseline)*" if item["baseline"] else ""
        lines.extend([
            f"- **{item['severity']} {item['rule']}** `{statement['path']}:{statement['start_line']}` — "
            f"{item['title']}{marker}",
            f"  - Evidence: {item['evidence']}",
            f"  - Action: {item['recommendation']}",
        ])
    if len(findings) > config.report_limit:
        lines.append(f"\nFull JSON contains {len(findings) - config.report_limit:,} additional findings.")

    def clone_section(title: str, clusters: list[dict[str, object]]) -> None:
        lines.extend(["", f"## {title}", ""])
        for cluster in clusters[:config.report_limit]:
            members = cluster["members"]
            assert isinstance(members, list)
            token_max = max(int(member["token_count"]) for member in members)
            marker = " *(baseline)*" if cluster["baseline"] else ""
            lines.append(
                f"- **{cluster['severity']}** `{cluster['id']}` — {len(members)} members, "
                f"score {float(cluster['score']):.3f}, up to {token_max:,} tokens{marker}"
            )
            for member in members[:12]:
                lines.append(f"  - `{member['path']}:{member['start_line']}` ({member['kind']}, {member['role']})")
            if len(members) > 12:
                lines.append(f"  - ... {len(members) - 12} more")
        if len(clusters) > config.report_limit:
            lines.append(f"\nFull JSON contains {len(clusters) - config.report_limit:,} additional clusters.")

    clone_section("Exact clone clusters", exact)
    clone_section("Near clone clusters", near)
    lines.extend([
        "",
        "## Interpretation",
        "",
        "Exact clones should be checked for intentional test fixtures before consolidation. Near clones are ranked "
        "structural matches: literals and parameters are normalized, and token shingles tolerate small predicate, "
        "projection, alias, and ordering drift. Consolidate only after result parity and caller-contract checks.",
        "",
    ])
    return "\n".join(lines)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def write_text(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8")


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    default_root = Path(__file__).resolve().parent.parent
    parser.add_argument("--root", type=Path, default=default_root)
    parser.add_argument("--config", type=Path, default=default_root / "scripts/sql-audit-config.json")
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--write-baseline", type=Path)
    parser.add_argument("--json", type=Path)
    parser.add_argument("--markdown", type=Path)
    parser.add_argument(
        "--skip-near-clones", action="store_true",
        help="skip the expensive structural near-clone pass (useful for a fast static CI ratchet)",
    )
    parser.add_argument(
        "--fail-on", choices=("never", "info", "low", "medium", "high"), default="never",
        help="fail for unbaselined items at this severity or above",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    root = args.root.resolve()
    config_path = args.config if args.config.is_absolute() else root / args.config
    config = read_config(config_path)
    baseline_path = args.baseline
    if baseline_path is not None and not baseline_path.is_absolute():
        baseline_path = root / baseline_path

    statements, inventory = discover(root, config)
    findings = static_findings(statements, config)
    exact = exact_clones(statements, config)
    near = [] if args.skip_near_clones else near_clones(statements, exact, config)
    baseline_ids = read_baseline(baseline_path)
    apply_baseline(findings, [*exact, *near], baseline_ids)
    report = build_report(root, inventory, statements, findings, exact, near)

    if args.json:
        output = args.json if args.json.is_absolute() else root / args.json
        write_json(output, report)
    if args.markdown:
        output = args.markdown if args.markdown.is_absolute() else root / args.markdown
        write_text(output, markdown_report(report, config))
    if args.write_baseline:
        output = args.write_baseline if args.write_baseline.is_absolute() else root / args.write_baseline
        ids = sorted([item.id for item in findings] + [item.id for item in exact] + [item.id for item in near])
        write_json(output, {"schema_version": VERSION, "ids": ids})

    summary = report["summary"]
    assert isinstance(summary, dict)
    severity = summary["severity"]
    assert isinstance(severity, dict)
    print(
        "sql-audit: "
        f"{inventory.get('sql_files', 0)} SQL files / {inventory.get('sql_lines', 0):,} lines; "
        f"{inventory.get('audit_units', 0):,} units; {len(findings):,} findings; "
        f"{len(exact):,} exact + {len(near):,} near clone clusters; "
        f"severity high={severity.get('high', 0)} medium={severity.get('medium', 0)} "
        f"low={severity.get('low', 0)} info={severity.get('info', 0)}"
    )

    if args.fail_on == "never":
        return 0
    threshold = SEVERITY_RANK[args.fail_on]
    unbaselined: list[tuple[str, str]] = [
        (item.id, item.severity) for item in [*findings, *exact, *near] if not item.baseline
    ]
    failures = [item for item in unbaselined if SEVERITY_RANK[item[1]] >= threshold]
    if failures:
        print(
            f"sql-audit: FAIL — {len(failures)} unbaselined item(s) at {args.fail_on} or above",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
