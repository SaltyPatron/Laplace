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
import ast
import datetime as dt
import fnmatch
import hashlib
import json
import os
import re
import sys
import tokenize
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Iterator, Sequence


VERSION = 1
SQL_SUFFIXES = (".sql", ".sql.in", ".psql", ".pgsql")
EMBEDDED_SUFFIXES = {
    ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp",
    ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".mjs",
}
IGNORED_PARTS = {
    ".git", ".idea", ".vs", ".worktrees", "build", "bin", "obj", "node_modules",
    "external", "coverage", "dist", "out", "__pycache__",
}
SEVERITY_RANK = {"info": 0, "low": 1, "medium": 2, "high": 3}
SQL_LEADS = re.compile(
    r"(?is)\b(?:"
    r"select\b.{0,800}\bfrom\b|"
    r"with\b.{0,1200}\bselect\b|"
    r"insert\s+into\b|update\s+[a-z_\"@]|delete\s+from\b|"
    r"create\s+(?:or\s+replace\s+)?(?:function|procedure|view|table)\b|"
    r"alter\s+table\b|drop\s+(?:function|procedure|view|table)\b"
    r")"
)
CPP_RAW_OPEN = re.compile(r'R"([A-Za-z_0-9]{0,16})\(')


@dataclass(frozen=True)
class Token:
    kind: str
    text: str
    start: int
    end: int
    line: int


@dataclass
class Statement:
    path: str
    role: str
    origin: str
    kind: str
    start_line: int
    end_line: int
    raw: str
    exact_tokens: tuple[str, ...]
    structural_tokens: tuple[str, ...]
    ordinal: int = 0
    id: str = ""

    @property
    def location(self) -> str:
        return f"{self.path}:{self.start_line}"

    @property
    def exact_hash(self) -> str:
        return digest(" ".join(self.exact_tokens), 16)

    @property
    def structural_hash(self) -> str:
        return digest(" ".join(self.structural_tokens), 16)

    @property
    def token_count(self) -> int:
        return len(self.structural_tokens)

    @property
    def excerpt(self) -> str:
        value = re.sub(r"\s+", " ", self.raw).strip()
        return value[:237] + "..." if len(value) > 240 else value


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


@dataclass
class AuditConfig:
    exclude: list[str] = field(default_factory=list)
    expensive_functions: list[str] = field(default_factory=list)
    filter_key_functions: list[str] = field(default_factory=list)
    exact_min_tokens: int = 20
    near_min_tokens: int = 50
    shingle_size: int = 5
    near_similarity: float = 0.82
    max_shingle_frequency: int = 40
    report_limit: int = 200


def digest(value: str, length: int = 12) -> str:
    return hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()[:length]


def line_at(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def scan_sql(text: str) -> list[Token]:
    """Lex SQL while preserving PostgreSQL strings and nested comments."""
    tokens: list[Token] = []
    i = 0
    line = 1
    size = len(text)

    def add(kind: str, start: int, end: int, start_line: int) -> None:
        tokens.append(Token(kind, text[start:end], start, end, start_line))

    while i < size:
        ch = text[i]
        if ch.isspace():
            line += 1 if ch == "\n" else 0
            i += 1
            continue
        if text.startswith("--", i):
            end = text.find("\n", i + 2)
            if end < 0:
                break
            line += 1
            i = end + 1
            continue
        if text.startswith("/*", i):
            start = i
            depth = 1
            i += 2
            while i < size and depth:
                if text.startswith("/*", i):
                    depth += 1
                    i += 2
                elif text.startswith("*/", i):
                    depth -= 1
                    i += 2
                else:
                    line += 1 if text[i] == "\n" else 0
                    i += 1
            if depth:
                add("broken_comment", start, size, line_at(text, start))
            continue

        start = i
        start_line = line
        if ch == "$":
            match = re.match(r"\$[A-Za-z_][A-Za-z_0-9]*\$|\$\$", text[i:])
            if match:
                tag = match.group(0)
                close = text.find(tag, i + len(tag))
                if close >= 0:
                    i = close + len(tag)
                    line += text.count("\n", start, i)
                    add("dollar", start, i, start_line)
                    continue
            param = re.match(r"\$[0-9]+", text[i:])
            if param:
                i += len(param.group(0))
                add("parameter", start, i, start_line)
                continue
        if ch == "@":
            match = re.match(r"@[A-Za-z_][A-Za-z_0-9]*@", text[i:])
            if match:
                i += len(match.group(0))
                add("placeholder", start, i, start_line)
                continue
        if ch == "'":
            i += 1
            while i < size:
                if text[i] == "'":
                    if i + 1 < size and text[i + 1] == "'":
                        i += 2
                        continue
                    i += 1
                    break
                if text[i] == "\\" and i + 1 < size:
                    i += 2
                else:
                    line += 1 if text[i] == "\n" else 0
                    i += 1
            add("string", start, i, start_line)
            continue
        if ch == '"':
            i += 1
            while i < size:
                if text[i] == '"':
                    if i + 1 < size and text[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                line += 1 if text[i] == "\n" else 0
                i += 1
            add("quoted_identifier", start, i, start_line)
            continue
        if ch.isalpha() or ch == "_" or ord(ch) > 127:
            i += 1
            while i < size and (text[i].isalnum() or text[i] in "_$" or ord(text[i]) > 127):
                i += 1
            add("word", start, i, start_line)
            continue
        if ch.isdigit() or (ch == "." and i + 1 < size and text[i + 1].isdigit()):
            match = re.match(
                r"(?:0[xX][0-9A-Fa-f]+|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)",
                text[i:],
            )
            assert match
            i += len(match.group(0))
            add("number", start, i, start_line)
            continue

        operator = next(
            (op for op in ("->>", "#>>", "::", "<=", ">=", "<>", "!=", "||", "->", "#>", ":=", "=>", "&&", "@>", "<@")
             if text.startswith(op, i)),
            None,
        )
        i += len(operator) if operator else 1
        add("symbol", start, i, start_line)
    return tokens


def dollar_body(raw: str) -> str:
    match = re.match(r"(\$[A-Za-z_][A-Za-z_0-9]*\$|\$\$)", raw)
    if not match:
        return raw
    tag = match.group(1)
    return raw[len(tag):-len(tag)] if raw.endswith(tag) else raw[len(tag):]


def normalize_tokens(text: str, structural: bool, depth: int = 0) -> tuple[str, ...]:
    result: list[str] = []
    for token in scan_sql(text):
        if token.kind == "word":
            result.append(token.text.lower())
        elif token.kind == "quoted_identifier":
            result.append(token.text)
        elif token.kind == "string":
            result.append("?string" if structural else token.text)
        elif token.kind == "number":
            result.append("?number" if structural else token.text.lower())
        elif token.kind == "parameter":
            result.append("?parameter" if structural else token.text.lower())
        elif token.kind == "placeholder":
            result.append(token.text.lower())
        elif token.kind == "dollar" and depth < 3:
            result.append("$body")
            result.extend(normalize_tokens(dollar_body(token.text), structural, depth + 1))
            result.append("$end")
        elif token.kind != "broken_comment":
            result.append(token.text.lower())
    return tuple(result)


def split_sql(text: str) -> list[tuple[str, int, int]]:
    tokens = scan_sql(text)
    statements: list[tuple[str, int, int]] = []
    first: Token | None = None
    for token in tokens:
        if first is None:
            first = token
        if token.kind == "symbol" and token.text == ";":
            raw = text[first.start:token.end]
            statements.append((raw, first.line, token.line))
            first = None
    if first is not None:
        last = tokens[-1]
        raw = text[first.start:last.end]
        if raw.strip():
            statements.append((raw, first.line, last.line + raw.count("\n")))
    return statements


def statement_kind(tokens: Sequence[str], forced: str | None = None) -> str:
    if forced:
        return forced
    prefix = " ".join(tokens[:12])
    if re.match(r"create (?:or replace )?(?:function|procedure)\b", prefix):
        return "definition"
    if tokens and tokens[0] in {"select", "with", "insert", "update", "delete", "merge", "call"}:
        return "query"
    return "ddl"


def role_for(path: str) -> str:
    normalized = path.replace("\\", "/").lower().strip("/")
    value = "/" + normalized + "/"
    parts = normalized.split("/")
    if (
        "/tests/" in value
        or "/test/" in value
        or "/expected/" in value
        or any(part.startswith("test") or part.endswith(".tests") for part in parts)
    ):
        return "test"
    if "/generated/" in value:
        return "generated"
    if value.startswith("/db/migrations/"):
        return "migration"
    if value.startswith("/scripts/"):
        return "script"
    return "production"


def is_sql_path(path: Path) -> bool:
    value = path.name.lower()
    return any(value.endswith(suffix) for suffix in SQL_SUFFIXES)


def excluded(relative: str, config: AuditConfig) -> bool:
    parts = Path(relative).parts
    if any(part in IGNORED_PARTS for part in parts):
        return True
    return any(fnmatch.fnmatch(relative, pattern) for pattern in config.exclude)


def excluded_directory(relative: str, config: AuditConfig) -> bool:
    parts = Path(relative).parts
    if any(part in IGNORED_PARTS for part in parts):
        return True
    normalized = relative.rstrip("/")
    return any(
        fnmatch.fnmatch(normalized, pattern.rstrip("/**"))
        or fnmatch.fnmatch(normalized + "/__audit_probe__", pattern)
        for pattern in config.exclude
    )


def decode_quoted(literal: str) -> str | None:
    try:
        value = ast.literal_eval(literal)
        return value if isinstance(value, str) else None
    except (SyntaxError, ValueError):
        quote = literal.find('"')
        if quote < 0:
            quote = literal.find("'")
        if quote < 0:
            return None
        body = literal[quote + 1:-1]
        escapes = {
            "\\": "\\", '"': '"', "'": "'", "n": "\n", "r": "\r",
            "t": "\t", "b": "\b", "f": "\f", "v": "\v", "0": "\0",
        }
        return re.sub(r"\\(.)", lambda match: escapes.get(match.group(1), match.group(0)), body)


def python_string_chunks(text: str) -> list[tuple[str, int, int]]:
    chunks: list[tuple[str, int, int]] = []
    pending: list[tuple[str, int, int]] = []

    def flush() -> None:
        if not pending:
            return
        value = "".join(item[0] for item in pending)
        if len(value) >= 12 and SQL_LEADS.search(value):
            chunks.append((value, pending[0][1], pending[-1][2]))
        pending.clear()

    try:
        stream = tokenize.generate_tokens(iter(text.splitlines(keepends=True)).__next__)
        for token in stream:
            if token.type == tokenize.STRING:
                value = decode_quoted(token.string)
                if value is not None:
                    pending.append((value, token.start[0], token.end[0]))
                else:
                    flush()
            elif token.type not in {
                tokenize.NL, tokenize.NEWLINE, tokenize.INDENT, tokenize.DEDENT,
                tokenize.COMMENT,
            }:
                flush()
        flush()
    except (tokenize.TokenError, IndentationError):
        flush()
    return chunks


def source_string_literals(text: str, suffix: str) -> list[tuple[str, int, int, int]]:
    """Lex strings from C-family sources while excluding comments and char literals."""
    chunks: list[tuple[str, int, int, int]] = []
    size = len(text)
    i = 0
    line = 1
    capture_single = suffix in {".js", ".jsx", ".ts", ".tsx", ".mjs"}

    def append(value: str, start: int, end: int, start_line: int) -> None:
        chunks.append((value, start, end, start_line))

    while i < size:
        ch = text[i]
        following = text[i + 1] if i + 1 < size else ""
        if ch == "/" and following == "/":
            end = text.find("\n", i + 2)
            if end < 0:
                break
            i = end + 1
            line += 1
            continue
        if ch == "/" and following == "*":
            end = text.find("*/", i + 2)
            if end < 0:
                break
            end += 2
            line += text.count("\n", i, end)
            i = end
            continue
        start = i
        start_line = line
        cpp = CPP_RAW_OPEN.match(text, i) if ch == "R" and following == '"' else None
        if cpp:
            delimiter = cpp.group(1)
            prefix_end = cpp.end()
            marker = ")" + delimiter + '"'
            close = text.find(marker, prefix_end)
            if close >= 0:
                end = close + len(marker)
                append(text[prefix_end:close], start, end, start_line)
                line += text.count("\n", i, end)
                i = end
                continue
        if ch == '"' and text.startswith('"""', i):
            quote_count = len(text[i:]) - len(text[i:].lstrip('"'))
            marker = '"' * quote_count
            body_start = i + quote_count
            close = text.find(marker, body_start)
            if close >= 0:
                end = close + quote_count
                append(text[body_start:close], start, end, start_line)
                line += text.count("\n", i, end)
                i = end
                continue
        if ch == "@" and following == '"':
            i += 2
            value: list[str] = []
            while i < size:
                if text.startswith('""', i):
                    value.append('"')
                    i += 2
                elif text[i] == '"':
                    i += 1
                    break
                else:
                    value.append(text[i])
                    line += 1 if text[i] == "\n" else 0
                    i += 1
            append("".join(value), start, i, start_line)
            continue
        if ch == "`":
            i += 1
            value = []
            while i < size:
                if text[i] == "`":
                    i += 1
                    break
                if text[i] == "\\" and i + 1 < size:
                    value.append(text[i:i + 2])
                    i += 2
                else:
                    value.append(text[i])
                    line += 1 if text[i] == "\n" else 0
                    i += 1
            append("".join(value), start, i, start_line)
            continue
        if ch == '"' or (capture_single and ch == "'"):
            quote = ch
            i += 1
            value = []
            while i < size:
                if text[i] == quote:
                    i += 1
                    break
                if text[i] == "\\" and i + 1 < size:
                    pair = text[i:i + 2]
                    decoded = decode_quoted(quote + pair + quote)
                    value.append(decoded if decoded is not None else pair)
                    i += 2
                else:
                    value.append(text[i])
                    line += 1 if text[i] == "\n" else 0
                    i += 1
            append("".join(value), start, i, start_line)
            continue
        if ch == "'":
            # C/C++/C# character literal. It cannot be an embedded SQL unit.
            i += 1
            while i < size:
                if text[i] == "\\" and i + 1 < size:
                    i += 2
                elif text[i] == "'":
                    i += 1
                    break
                else:
                    i += 1
            continue
        line += 1 if ch == "\n" else 0
        i += 1
    return chunks


def string_chunks(text: str, suffix: str) -> list[tuple[str, int, int]]:
    """Extract source-language strings, joining C/Python adjacent literals."""
    if suffix == ".py":
        return python_string_chunks(text)
    raw_chunks = source_string_literals(text, suffix)

    join_adjacent = suffix in {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".cs",
    }
    chunks: list[tuple[str, int, int]] = []
    index = 0
    while index < len(raw_chunks):
        value, start, end, line = raw_chunks[index]
        index += 1
        if join_adjacent:
            while index < len(raw_chunks):
                next_value, next_start, next_end, _ = raw_chunks[index]
                gap = text[end:next_start]
                gap = re.sub(r"/\*.*?\*/|//[^\n]*(?:\n|$)", "", gap, flags=re.S)
                allowed = r"\s*" if suffix != ".cs" else r"\s*(?:\+\s*)?"
                if not re.fullmatch(allowed, gap):
                    break
                value += next_value
                end = next_end
                index += 1
        if len(value) >= 12 and SQL_LEADS.search(value):
            chunks.append((value, line, line + value.count("\n")))
    return chunks


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
