#!/usr/bin/env python3
"""Static rules, findings, and exact/near-clone clustering for sql-audit.py."""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
import re
import sys
from typing import Sequence

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from sql_audit_scanners import AuditConfig, SEVERITY_RANK, Statement, digest


@dataclass
class Finding:
    rule: str
    severity: str
    category: str
    title: str
    statement: Statement
    evidence: str
    recommendation: str
    id: str = ""
    baseline: bool = False

    def finish(self) -> "Finding":
        self.id = "finding-" + digest(
            "|".join((self.rule, self.statement.path, self.statement.exact_hash)), 16
        )
        return self


@dataclass
class CloneCluster:
    kind: str
    severity: str
    members: list[Statement]
    score: float
    id: str = ""
    baseline: bool = False

    def finish(self) -> "CloneCluster":
        keys = sorted(f"{m.path}|{m.kind}|{m.exact_hash}" for m in self.members)
        self.id = f"{self.kind}-" + digest("\n".join(keys), 16)
        return self


def flat(statement: Statement) -> str:
    return " ".join(statement.structural_tokens)


def qualified_call_pattern(name: str) -> str:
    return r"\s*\.\s*".join(re.escape(part) for part in name.lower().split(".")) + r"\s*\("


def null_predicate_comparison(tokens: Sequence[str]) -> bool:
    clauses = {"where", "on", "having", "when", "set", "values", "select", "returning"}
    predicate_clauses = {"where", "on", "having", "when"}
    current = ""
    for index, token in enumerate(tokens):
        if token in clauses:
            current = token
        if (
            token in {"=", "<>", "!="}
            and index + 1 < len(tokens)
            and tokens[index + 1] == "null"
            and current in predicate_clauses
        ):
            return True
        if (
            token == "null"
            and index + 1 < len(tokens)
            and tokens[index + 1] in {"=", "<>", "!="}
            and current in predicate_clauses
        ):
            return True
    return False


def downgrade(severity: str, role: str) -> str:
    if role not in {"test", "generated"}:
        return severity
    return {"high": "medium", "medium": "low", "low": "info", "info": "info"}[severity]


def add_finding(
    output: list[Finding],
    statement: Statement,
    rule: str,
    severity: str,
    category: str,
    title: str,
    evidence: str,
    recommendation: str,
) -> None:
    output.append(Finding(
        rule=rule,
        severity=downgrade(severity, statement.role),
        category=category,
        title=title,
        statement=statement,
        evidence=evidence,
        recommendation=recommendation,
    ).finish())


def definition_findings(statement: Statement, config: AuditConfig) -> list[Finding]:
    output: list[Finding] = []
    value = flat(statement)
    if "security definer" in value and not re.search(r"\bset search_path\b", value):
        add_finding(
            output, statement, "LPSQL001", "high", "security",
            "SECURITY DEFINER function has no fixed search_path",
            "SECURITY DEFINER appears without a function-level SET search_path clause.",
            "Set a minimal trusted search_path and schema-qualify referenced objects.",
        )
    if re.search(r"\breturns (?:setof|table)\b", value) and not re.search(r"\brows \?number\b", value):
        add_finding(
            output, statement, "LPSQL002", "medium", "planner",
            "Set-returning function uses PostgreSQL's default row estimate",
            "RETURNS SETOF/TABLE is present but no ROWS declaration was found.",
            "Measure or derive cardinality and declare ROWS; use planner support for input-dependent cardinality.",
        )
    if re.search(
        r"\bp_(?:limit|k|max(?:imum)?(?:_[a-z_]+)?|cap|count)\b[^,)]*\bdefault \?number\b",
        value,
    ):
        add_finding(
            output, statement, "LPSQL003", "medium", "correctness",
            "Function interface contains a silent numeric cap",
            "A limit/k/max/cap parameter has a numeric default.",
            "Prove the bound is part of the contract and expose truncation, or default to an unbounded/null form.",
        )
    language_sql = re.search(r"\blanguage sql\b", value)
    volatility = re.search(r"\b(?:immutable|stable|volatile)\b", value)
    if language_sql and not volatility:
        add_finding(
            output, statement, "LPSQL004", "low", "contract",
            "SQL function leaves volatility implicit",
            "LANGUAGE sql is present without IMMUTABLE, STABLE, or VOLATILE.",
            "Declare the narrowest correct volatility class after reviewing all callees.",
        )
    return output


def query_findings(statement: Statement, config: AuditConfig) -> list[Finding]:
    output: list[Finding] = []
    value = flat(statement)
    if null_predicate_comparison(statement.structural_tokens):
        add_finding(
            output, statement, "LPSQL101", "high", "correctness",
            "NULL is compared with an ordinary equality operator",
            "The statement contains = NULL, <> NULL, or != NULL.",
            "Use IS NULL / IS NOT NULL, or IS [NOT] DISTINCT FROM when null-safe equality is intended.",
        )
    if re.search(r"\bnot in \(", value):
        add_finding(
            output, statement, "LPSQL102", "medium", "correctness",
            "NOT IN requires proof that the right side cannot contain NULL",
            "NOT IN has three-valued semantics and may reject every row when its input contains NULL.",
            "Prove a NOT NULL input or use NOT EXISTS with an explicit correlation predicate.",
        )
    if re.match(r"^(?:update\b.+\bset|delete from)\b", value) and " where " not in f" {value} ":
        add_finding(
            output, statement, "LPSQL103", "high", "data-safety",
            "Data-changing statement has no visible WHERE clause",
            "UPDATE/DELETE was found without WHERE in the auditable statement unit.",
            "Confirm whole-relation mutation is intentional; otherwise add the predicate and a row-count assertion.",
        )
    if re.search(r"\blimit (?:\?number|all)\b", value) and not re.search(r"\border by\b", value):
        add_finding(
            output, statement, "LPSQL104", "medium", "correctness",
            "LIMIT has no visible deterministic ordering",
            "LIMIT appears without ORDER BY in the same statement unit.",
            "Add a total ORDER BY, or document that an arbitrary subset is explicitly acceptable.",
        )
    if re.search(r"\bselect \*\b", value):
        add_finding(
            output, statement, "LPSQL105", "low", "maintainability",
            "SELECT * couples the caller to relation shape",
            "The projection contains an unqualified wildcard.",
            "Name contract columns explicitly unless this is an intentional diagnostic surface.",
        )
    if re.search(r"\bunion\b", value) and not re.search(r"\bunion all\b", value):
        add_finding(
            output, statement, "LPSQL106", "low", "performance",
            "UNION pays for duplicate elimination",
            "UNION appears without ALL.",
            "Use UNION ALL when branches are disjoint or duplicates are meaningful; otherwise retain with proof.",
        )
    if re.search(r"\bcount \( \* \)\s*(?:>|<>|!=)\s*\?number", value):
        add_finding(
            output, statement, "LPSQL107", "low", "performance",
            "COUNT(*) appears to be used as an existence test",
            "A COUNT(*) result is compared to a number.",
            "Use EXISTS when the exact count is not part of the result contract.",
        )
    if re.search(r"\border by (?:pg_catalog\s*\.\s*)?random \(", value):
        add_finding(
            output, statement, "LPSQL108", "medium", "performance",
            "ORDER BY random() requires a full random-key sort",
            "The statement orders candidate rows by random().",
            "Use a bounded sampling strategy appropriate to relation size and required distribution.",
        )
    if re.search(r"\b(?:like|ilike) \?string", value):
        add_finding(
            output, statement, "LPSQL109", "info", "performance",
            "Pattern predicate needs indexability review",
            "LIKE/ILIKE with a string expression was found; normalization hides whether it has a leading wildcard.",
            "Check the real pattern and plan; use an appropriate prefix, trigram, or search index where needed.",
        )
    if " as materialized (" in value:
        add_finding(
            output, statement, "LPSQL110", "info", "planner",
            "MATERIALIZED CTE creates an optimization fence",
            "AS MATERIALIZED appears in the statement.",
            "Retain only when single evaluation or fencing is required and confirmed by plan evidence.",
        )
    for name in config.expensive_functions:
        if re.search(rf"\b{qualified_call_pattern(name)}", value):
            add_finding(
                output, statement, "LPSQL111", "medium", "performance",
                "Known expensive scalar or fan-out primitive is called",
                f"The repository-specific hot primitive {name}() appears in this statement unit.",
                "Verify call cardinality; batch, join directly, or compute once when multiple rows can reach it.",
            )
    for name in config.filter_key_functions:
        if re.search(
            rf"\b{qualified_call_pattern(name)}[^)]*\s*\.\s*(?:type_id|subject_id|object_id)\b",
            value,
        ):
            add_finding(
                output, statement, "LPSQL112", "medium", "planner",
                "Function wraps a known partition/index key",
                f"{name}() receives a qualified type/subject/object key in a predicate-bearing statement.",
                "Inspect the plan for pruning/index loss; rewrite only when measured against representative cardinalities.",
            )
    return output


def static_findings(statements: Sequence[Statement], config: AuditConfig) -> list[Finding]:
    output: list[Finding] = []
    seen: set[tuple[str, str]] = set()
    for statement in statements:
        found = (
            definition_findings(statement, config)
            if statement.kind == "definition"
            else query_findings(statement, config)
            if statement.kind in {"query", "body"}
            else []
        )
        for finding in found:
            key = (finding.id, finding.rule)
            if key not in seen:
                seen.add(key)
                output.append(finding)
    return sorted(
        output,
        key=lambda item: (-SEVERITY_RANK[item.severity], item.rule, item.statement.path, item.statement.start_line),
    )


def clone_severity(members: Sequence[Statement], score: float) -> str:
    production = sum(member.role in {"production", "migration"} for member in members)
    if production >= 2 and score >= 0.95:
        return "medium"
    return "low" if production else "info"


def exact_clones(statements: Sequence[Statement], config: AuditConfig) -> list[CloneCluster]:
    groups: dict[tuple[str, ...], list[Statement]] = defaultdict(list)
    for statement in statements:
        if statement.token_count >= config.exact_min_tokens:
            groups[statement.exact_tokens].append(statement)
    clusters: list[CloneCluster] = []
    for members in groups.values():
        locations = {(member.path, member.start_line) for member in members}
        if len(locations) < 2:
            continue
        cluster = CloneCluster(
            kind="exact-clone",
            severity=clone_severity(members, 1.0),
            members=sorted(members, key=lambda item: (item.path, item.start_line)),
            score=1.0,
        ).finish()
        clusters.append(cluster)
    return sorted(clusters, key=lambda item: (-len(item.members), -max(m.token_count for m in item.members), item.id))


class UnionFind:
    def __init__(self, size: int) -> None:
        self.parent = list(range(size))

    def find(self, item: int) -> int:
        while self.parent[item] != item:
            self.parent[item] = self.parent[self.parent[item]]
            item = self.parent[item]
        return item

    def union(self, left: int, right: int) -> None:
        a, b = self.find(left), self.find(right)
        if a != b:
            self.parent[b] = a


def shingles(tokens: Sequence[str], size: int) -> set[tuple[str, ...]]:
    if len(tokens) < size:
        return {tuple(tokens)}
    return {tuple(tokens[index:index + size]) for index in range(len(tokens) - size + 1)}


def near_clones(
    statements: Sequence[Statement], exact: Sequence[CloneCluster], config: AuditConfig
) -> list[CloneCluster]:
    candidates = [s for s in statements if s.token_count >= config.near_min_tokens]
    if len(candidates) < 2:
        return []
    sets = [shingles(s.structural_tokens, config.shingle_size) for s in candidates]
    index: dict[tuple[str, ...], list[int]] = defaultdict(list)
    for number, values in enumerate(sets):
        for value in values:
            index[value].append(number)
    overlaps: Counter[tuple[int, int]] = Counter()
    for documents in index.values():
        if len(documents) > config.max_shingle_frequency:
            continue
        for left_pos, left in enumerate(documents):
            for right in documents[left_pos + 1:]:
                overlaps[(left, right)] += 1

    exact_pairs: set[frozenset[str]] = set()
    for cluster in exact:
        ids = [member.id for member in cluster.members]
        for left_pos, left in enumerate(ids):
            for right in ids[left_pos + 1:]:
                exact_pairs.add(frozenset((left, right)))

    union = UnionFind(len(candidates))
    accepted: dict[tuple[int, int], float] = {}
    for (left, right), intersection in overlaps.items():
        a, b = candidates[left], candidates[right]
        if a.path == b.path and not (a.end_line < b.start_line or b.end_line < a.start_line):
            continue
        if frozenset((a.id, b.id)) in exact_pairs:
            continue
        length_ratio = min(len(sets[left]), len(sets[right])) / max(len(sets[left]), len(sets[right]))
        if length_ratio < 0.65:
            continue
        actual_intersection = len(sets[left] & sets[right])
        similarity = actual_intersection / len(sets[left] | sets[right])
        if similarity >= config.near_similarity:
            accepted[(left, right)] = similarity
            union.union(left, right)

    components: dict[int, list[int]] = defaultdict(list)
    for number in {item for pair in accepted for item in pair}:
        components[union.find(number)].append(number)
    clusters: list[CloneCluster] = []
    for numbers in components.values():
        if len(numbers) < 2:
            continue
        number_set = set(numbers)
        scores = [score for pair, score in accepted.items() if set(pair) <= number_set]
        members = sorted((candidates[number] for number in numbers), key=lambda item: (item.path, item.start_line))
        score = min(scores) if scores else config.near_similarity
        clusters.append(CloneCluster(
            kind="near-clone",
            severity=clone_severity(members, score),
            members=members,
            score=score,
        ).finish())
    return sorted(clusters, key=lambda item: (-len(item.members), -item.score, item.id))
