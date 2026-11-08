#!/usr/bin/env python3
"""Helper validation checks and manifest parsers for validate-pipeline.py."""

from __future__ import annotations

import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "scripts" / "win" / "witness-manifest.json"


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def foundation_sources(manifest: dict) -> list[str]:
    block = manifest.get("foundation")
    if not isinstance(block, dict):
        return []
    srcs = block.get("sources")
    if not isinstance(srcs, list):
        return []
    return [s for s in srcs if isinstance(s, str) and s]


def parse_seed_foundation_cmd(text: str) -> list[str] | None:
    m = re.search(r"for\s+%%S\s+in\s+\(([^)]+)\)", text, re.IGNORECASE)
    if not m:
        return None
    out = [s.strip() for s in m.group(1).split() if s.strip()]
    return out or None


def validate_retired_workflows() -> list[str]:
    errs: list[str] = []
    workflows = ROOT / ".github" / "workflows"
    for name in (
        "ci.yml",
        "integration.yml",
        "deploy-app.yml",
        "seed-ladder.yml",
        "_ingest.yml",
    ):
        if (workflows / name).is_file():
            errs.append(f"retired workflow still present: .github/workflows/{name}")
    laplace = workflows / "laplace.yml"
    if not laplace.is_file():
        errs.append("missing canonical workflow: .github/workflows/laplace.yml")
    else:
        text = read_text(laplace)
        compact = re.sub(r"\s+", "", text)
        # Main delivery is automatic. Its one manual form replays delivery of the product
        # work not yet installed, planned from the installed base_sha; other manual
        # maintenance lives in the operator workflow, so the main Actions graph remains one
        # source-to-product chain.
        if "push:branches:[main]" not in compact:
            errs.append("laplace.yml: expected push trigger on main")
        if "workflow_dispatch:" in compact and "workflow_dispatch:inputs:base_sha:" not in compact:
            errs.append("laplace.yml: manual dispatch belongs in product-operator.yml")
        if "pull_request:" in compact:
            errs.append("laplace.yml: pull_request trigger forbidden (self-hosted runner)")
        for stale in ("integration.yml", "deploy-app.yml", "seed-ladder.yml", "mainline-delivery.yml"):
            if stale in text:
                errs.append(f"laplace.yml: references retired workflow {stale}")
        operator = workflows / "product-operator.yml"
        if not operator.is_file():
            errs.append("missing manual product operator workflow: .github/workflows/product-operator.yml")
        elif "workflow_dispatch:" not in re.sub(r"\\s+", "", read_text(operator)):
            errs.append("product-operator.yml: expected workflow_dispatch trigger")
    if not (workflows / "seed.yml").is_file():
        errs.append("missing canonical ingestion workflow: .github/workflows/seed.yml")
    return errs


def validate_foundation_indexes_stay_online() -> list[str]:
    """Production foundation ingest must not remove the live read indexes."""
    workflow = ROOT / ".github" / "workflows" / "seed.yml"
    if not workflow.is_file():
        return ["missing canonical ingestion workflow: .github/workflows/seed.yml"]

    text = read_text(workflow)
    ensure = read_text(ROOT / "scripts" / "ensure-foundation.sh")
    errs: list[str] = []
    forbidden = ("index_cycle", "drop-indexes", "LAPLACE_INDEX_CYCLE")
    for token in forbidden:
        if token in text or token in ensure:
            errs.append(
                f"foundation ingest: production index-cycle token remains: {token}"
            )
    return errs


def load_manifest() -> dict:
    with MANIFEST.open(encoding="utf-8") as f:
        return json.load(f)


def manifest_errors(manifest: dict) -> list[str]:
    errs: list[str] = []
    cadence = manifest.get("cadence")
    if not isinstance(cadence, list) or not cadence:
        return ["manifest: missing or empty cadence[]"]

    seen_stages: set[str] = set()
    prev_order = -1
    for stage in cadence:
        name = stage.get("stage")
        order = stage.get("order")
        if not name:
            errs.append("manifest: cadence entry missing stage")
            continue
        if name in seen_stages:
            errs.append(f"manifest: duplicate stage '{name}'")
        seen_stages.add(name)
        if not isinstance(order, int):
            errs.append(f"manifest: stage '{name}' missing integer order")
        elif order <= prev_order:
            errs.append(f"manifest: stage '{name}' order={order} not after prior stage")
        else:
            prev_order = order

        sources = stage.get("sources")
        if not isinstance(sources, list) or not sources:
            errs.append(f"manifest: stage '{name}' has no sources[]")
            continue

        clis: list[str] = []
        prev_src_order = 0
        for i, src in enumerate(sources):
            cli = src.get("cli")
            if not cli:
                errs.append(f"manifest: stage '{name}' source[{i}] missing cli")
                continue
            if "path" in src and not src["path"]:
                errs.append(f"manifest: stage '{name}' cli '{cli}' has empty path")
            if name == "relations":
                src_order = src.get("order")
                if not isinstance(src_order, int):
                    errs.append(f"manifest: database source '{cli}' missing integer order")
                elif src_order <= prev_src_order:
                    errs.append(
                        f"manifest: database source '{cli}' order={src_order} "
                        f"not after prior ({prev_src_order})"
                    )
                else:
                    prev_src_order = src_order
            clis.append(cli)

        if name in ("floor", "document", "relations", "usage") and len(clis) != len(set(clis)):
            dupes = [c for c in clis if clis.count(c) > 1]
            errs.append(f"manifest: stage '{name}' duplicate cli values: {sorted(set(dupes))}")

    return errs


def stage_cli_lists(manifest: dict) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for stage in manifest["cadence"]:
        name = stage["stage"]
        sources = stage["sources"]
        if name == "relations":
            ordered = sorted(sources, key=lambda s: s.get("order", 0))
            out[name] = [s["cli"] for s in ordered]
        else:
            out[name] = [s["cli"] for s in sources]
    return out


def expected_seed_stage_relations(manifest: dict) -> list[str]:
    """seed-stage.cmd relations loop = floor cili (if any) + manifest relations order."""
    stages = stage_cli_lists(manifest)
    prefix = [c for c in stages.get("floor", []) if c == "cili"]
    return prefix + stages["relations"]


def flat_ingest_all(manifest: dict) -> list[str]:
    """CLI sources ingest-source.sh `all` should run (no path-only witnesses)."""
    stages = stage_cli_lists(manifest)
    skip = {"stack", "repo", "safetensors"}
    order: list[str] = []
    for stage_name in ("floor", "document", "relations", "usage"):
        for cli in stages.get(stage_name, []):
            if cli in skip:
                continue
            order.append(cli)
    return order


def extract_bash_array(text: str, var_name: str) -> list[str] | None:
    m = re.search(rf"{re.escape(var_name)}=\(([^)]*)\)", text)
    if not m:
        return None
    inner = m.group(1)
    quoted = re.findall(r'"([^"]+)"', inner)
    if quoted:
        return quoted
    return [t for t in inner.split() if t and not t.startswith("#")]


def parse_seed_stage_relations(text: str) -> list[str] | None:
    m = re.search(r":stage_relations[\s\S]*?for %%s in \(([^)]+)\)", text, re.IGNORECASE)
    if not m:
        return None
    return [s.strip() for s in m.group(1).split() if s.strip()]


def parse_audit_ladder(text: str) -> list[str] | None:
    relations = extract_bash_array(text, "RELATIONS")
    if not relations:
        return None
    base = ["iso639", *relations]
    full_m = re.search(
        r"\[\[\s*\$FULL\s*-eq\s*1\s*\]\]\s*&&\s*LADDER\+=\(([^)]*)\)", text
    )
    if full_m:
        base.extend(t.strip().strip('"') for t in full_m.group(1).split() if t.strip())
    return base


def compare_order(label: str, expected: list[str], actual: list[str] | None) -> list[str]:
    if actual is None:
        return [f"{label}: could not parse ordering"]
    if actual != expected:
        return [
            f"{label}: order drift",
            f"  expected: {' '.join(expected)}",
            f"  actual:   {' '.join(actual)}",
        ]
    return []


def compare_audit_base(expected_relations: list[str], actual: list[str] | None) -> list[str]:
    if actual is None:
        return ["audit-decomposers.sh: could not parse LADDER"]
    expected_base = ["iso639", *expected_relations]
    prefix = actual[: len(expected_base)]
    if prefix != expected_base:
        return [
            "audit-decomposers.sh: LADDER base drift",
            f"  expected: {' '.join(expected_base)}",
            f"  actual:   {' '.join(prefix)}",
        ]
    return []


def parse_audit_layer_map(text: str) -> dict[str, int] | None:
    m = re.search(r"declare -A LAYER=\(([\s\S]*?)\)", text)
    if not m:
        return None
    out: dict[str, int] = {}
    for key, val in re.findall(r"\[([\w-]+)\]=(\d+)", m.group(1)):
        out[key] = int(val)
    return out or None


def expected_audit_layer_map(relations: list[str]) -> dict[str, int]:
    """Sequential layer-completion ordinals: ISO 639 first, then database sources."""
    out: dict[str, int] = {"iso639": 1}
    for i, cli in enumerate(relations, start=2):
        out[cli] = i
    out["tatoeba"] = len(relations) + 2
    out["opensubtitles"] = len(relations) + 3
    return out


def _seed_workflow_sources() -> set[str]:
    """
    Every low-level source key a `.github/workflows/seed-*.yml` can hand to
    canonical `seed.yml`.

    Operator workflows may expose friendly presets rather than raw source keys, so
    reachability comes from three explicit forms:
      - a literal `source: <key>` in a reusable-workflow call
      - a raw `source:` choice input, where one still exists
      - a shell resolver assignment `source=<key>` used by preset workflows

    The last form is deliberately narrow: it matches only a shell assignment line,
    not prose, YAML keys, corpus names, or dynamic values.
    """
    keys: set[str] = set()
    wf_dir = ROOT / ".github" / "workflows"
    if not wf_dir.is_dir():
        return keys

    inline = re.compile(r"^(\s+)source:[ \t]+(\S+)[ \t]*$", re.M)
    block = re.compile(r"^(\s+)source:[ \t]*\n((?:\1\s+\S.*\n|\s*\n)*)", re.M)
    option_line = re.compile(r"^\s*-\s*([A-Za-z0-9][\w.-]*)", re.M)
    shell_source = re.compile(r"^\s+source=([A-Za-z0-9][\w.-]*)\s*$", re.M)

    for path in sorted(wf_dir.glob("seed-*.yml")):
        text = path.read_text(encoding="utf-8")

        for _indent, value in inline.findall(text):
            if value.startswith("${{"):
                continue
            keys.add(value.strip("\"'"))

        for _indent, body in block.findall(text):
            m = re.search(r"^\s*options:[ \t]*(\[[^\]]*\])?[ \t]*$", body, re.M)
            if not m:
                continue
            if m.group(1):
                keys.update(
                    value.strip().strip("\"'")
                    for value in m.group(1)[1:-1].split(",")
                    if value.strip()
                )
            else:
                keys.update(option_line.findall(body[m.end():]))

        keys.update(shell_source.findall(text))

    return keys


def _workflow_choice_options(text: str, input_name: str) -> set[str]:
    input_block = re.search(
        rf"^\s{{6}}{re.escape(input_name)}:\s*\n"
        rf"(?P<body>(?:^\s{{8,}}.*\n|^\s*\n)*)",
        text,
        re.M,
    )
    if not input_block:
        return set()
    options = re.search(r"options:\s*\[([^]]+)\]", input_block.group("body"))
    if not options:
        return set()
    return {
        value.strip().strip("\"'")
        for value in options.group(1).split(",")
        if value.strip()
    }


def _resolver_case_labels(text: str, shell_variable: str) -> set[str]:
    blocks = re.findall(
        rf'case "\${re.escape(shell_variable)}" in\s*\n'
        rf'(?P<body>.*?)^\s{{10}}esac$',
        text,
        re.M | re.S,
    )
    accepted: set[str] = set()
    for block in blocks:
        for label in re.findall(r"^\s{12}([^#\n][^\n)]*)\)", block, re.M):
            accepted.update(
                part.strip()
                for part in label.split("|")
                if part.strip() and part.strip() != "*"
            )
    return accepted


def _validate_seed_preset_routes(
    workflow_name: str, input_name: str, shell_variable: str
) -> list[str]:
    path = ROOT / ".github" / "workflows" / workflow_name
    if not path.is_file():
        return [f"missing workflow: .github/workflows/{workflow_name}"]

    text = read_text(path)
    advertised = _workflow_choice_options(text, input_name)
    if not advertised:
        return [
            f"{workflow_name}: {input_name} input must use an explicit options list"
        ]

    accepted = _resolver_case_labels(text, shell_variable)
    if not accepted:
        return [
            f"{workflow_name}: could not parse {input_name} legality resolver"
        ]

    missing = sorted(advertised - accepted)
    return (
        [f"{workflow_name}: advertised presets rejected by resolver: {missing}"]
        if missing
        else []
    )


def validate_seed_chess_routes() -> list[str]:
    """Every advertised chess preset must reach an explicit resolver branch."""
    return _validate_seed_preset_routes("seed-chess.yml", "preset", "PRESET")


def validate_seed_code_routes() -> list[str]:
    """Every advertised code preset must reach an explicit resolver branch."""
    return _validate_seed_preset_routes("seed-code.yml", "preset", "PRESET")


_LEGACY_TYPE_PATH = re.compile(r"substrate/type/[A-Z]")
_TYPE_IDENTITY_CARVEOUTS = (
    "substrate/type/grammar/",
    "substrate/type/HasFileMetadata/",
    "substrate/type_tier/",
)


def _legacy_type_path_hits(path: Path, text: str) -> list[tuple[int, str]]:
    hits: list[tuple[int, str]] = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        if _LEGACY_TYPE_PATH.search(line) and not any(c in line for c in _TYPE_IDENTITY_CARVEOUTS):
            hits.append((lineno, line.strip()))
    return hits


def validate_type_identity_law() -> list[str]:
    """Fail if production SQL/C#/native code still uses the legacy namespaced type path."""
    errs: list[str] = []
    scan_roots: list[tuple[Path, tuple[str, ...]]] = [
        (ROOT / "extension" / "laplace_substrate" / "sql", (".sql.in",)),
        (ROOT / "app", (".cs",)),
        (ROOT / "engine", (".c", ".cpp", ".h")),
        (ROOT / "scripts", (".py", ".sql", ".sh")),
    ]
    skip_dirs = {"audit-2026-06-26", "node_modules", "bin", "obj"}
    skip_files: set[str] = set()

    for root, suffixes in scan_roots:
        if not root.is_dir():
            continue
        for path in root.rglob("*"):
            if not path.is_file():
                continue
            if any(part in skip_dirs for part in path.parts):
                continue
            if path.name in skip_files:
                continue
            if not path.name.endswith(suffixes):
                continue
            hits = _legacy_type_path_hits(path, read_text(path))
            for lineno, snippet in hits[:3]:
                errs.append(f"type identity drift: {path.relative_to(ROOT)}:{lineno}: {snippet[:120]}")
            if len(hits) > 3:
                errs.append(f"type identity drift: {path.relative_to(ROOT)}: +{len(hits) - 3} more hits")
    return errs
