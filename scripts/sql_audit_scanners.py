#!/usr/bin/env python3
"""Lexical scanners, string extractors, and statement discovery for sql-audit.py."""

from __future__ import annotations

import ast
from collections import Counter
from dataclasses import dataclass, field
import fnmatch
import hashlib
import os
from pathlib import Path
import re
import sys
import tokenize
from typing import Sequence


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
    if value.startswith("/db/schema/"):
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
        opening = re.match("(?i)([rubf]*)(\"{3}|'{3}|\"|')", literal)
        if opening is None:
            return None
        prefix, delimiter = opening.groups()
        if not literal.endswith(delimiter) or len(literal) < opening.end() + len(delimiter):
            return None
        body = literal[opening.end():-len(delimiter)]
        if "r" in prefix.lower():
            return body
        escapes = {
            "\\": "\\", '"': '"', "'": "'", "n": "\n", "r": "\r",
            "t": "\t", "b": "\b", "f": "\f", "v": "\v", "0": "\0",
        }
        return re.sub(r"\\(.)", lambda match: escapes.get(match.group(1), match.group(0)), body)


def python_string_chunks(text: str) -> list[tuple[str, int, int]]:
    chunks: list[tuple[str, int, int]] = []
    nested_chunks: list[tuple[str, int, int]] = []
    pending: list[tuple[str, int, int]] = []
    lines = text.splitlines(keepends=True)
    offsets = [0]
    for line in lines:
        offsets.append(offsets[-1] + len(line))

    def literal_value(literal: str, start_line: int) -> str | None:
        # AST parsing decodes escapes/raw prefixes and literal braces identically
        # on both token layouts. Replacement expressions are never evaluated or
        # misidentified as SQL text: their values remain opaque placeholders.
        try:
            node = ast.parse(literal, mode="eval").body
        except (SyntaxError, ValueError):
            return decode_quoted(literal)

        def render(value: ast.AST) -> str | None:
            if isinstance(value, ast.Constant) and isinstance(value.value, str):
                return value.value
            if isinstance(value, ast.JoinedStr):
                return "".join(item.value if isinstance(item, ast.Constant) else "@PYTHON_EXPR@"
                               for item in value.values)
            return None

        def nested(value: ast.AST) -> None:
            rendered = render(value)
            if rendered is not None:
                if len(rendered) >= 12 and SQL_LEADS.search(rendered):
                    nested_chunks.append((rendered, start_line + value.lineno - 1,
                                          start_line + value.end_lineno - 1))
                if isinstance(value, ast.JoinedStr):
                    for part in value.values:
                        if isinstance(part, ast.FormattedValue):
                            nested(part.value)
                            if part.format_spec is not None:
                                nested(part.format_spec)
                return
            for child in ast.iter_child_nodes(value):
                nested(child)

        if isinstance(node, ast.JoinedStr):
            for part in node.values:
                if isinstance(part, ast.FormattedValue):
                    nested(part.value)
                    if part.format_spec is not None:
                        nested(part.format_spec)
        return render(node)

    def flush() -> None:
        if not pending:
            return
        value = "".join(item[0] for item in pending)
        if len(value) >= 12 and SQL_LEADS.search(value):
            chunks.append((value, pending[0][1], pending[-1][2]))
        pending.clear()

    try:
        stream = tokenize.generate_tokens(iter(lines).__next__)
        for token in stream:
            if token.type == getattr(tokenize, "FSTRING_START", -1):
                # Python <=3.11 returns the whole f-string as STRING; >=3.12
                # exposes its parts, including arbitrary nested expressions.
                # Recover the exact outer source span without interpreting any
                # replacement token as an independent adjacent SQL literal.
                first = token
                depth = 1
                while depth:
                    token = next(stream)
                    if token.type == tokenize.FSTRING_START:
                        depth += 1
                    elif token.type == tokenize.FSTRING_END:
                        depth -= 1
                start = offsets[first.start[0] - 1] + first.start[1]
                end = offsets[token.end[0] - 1] + token.end[1]
                token = tokenize.TokenInfo(tokenize.STRING, text[start:end], first.start, token.end, first.line)
            if token.type == tokenize.STRING:
                value = literal_value(token.string, token.start[0])
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
    except (tokenize.TokenError, IndentationError, StopIteration):
        flush()
    return sorted(chunks + nested_chunks, key=lambda item: item[1])


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

