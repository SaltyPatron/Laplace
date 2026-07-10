#!/usr/bin/env python3
"""Validate ingest orchestration against scripts/win/witness-manifest.json."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "scripts" / "win" / "witness-manifest.json"


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


def parse_ensure_foundation_sh(text: str) -> list[str] | None:
    m = re.search(r'FOUNDATION=\(\s*([\s\S]*?)\s*\)', text)
    if not m:
        return None
    out: list[str] = []
    for line in m.group(1).splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        cli = line.strip('"').split(":")[0]
        if cli:
            out.append(cli)
    return out or None


def validate_retired_workflows() -> list[str]:
    errs: list[str] = []
    workflows = ROOT / ".github" / "workflows"
    for name in ("ci.yml", "integration.yml", "deploy-app.yml", "seed-ladder.yml"):
        if (workflows / name).is_file():
            errs.append(f"retired workflow still present: .github/workflows/{name}")
    laplace = workflows / "laplace.yml"
    if not laplace.is_file():
        errs.append("missing canonical workflow: .github/workflows/laplace.yml")
    else:
        text = read_text(laplace)
        compact = re.sub(r"\s+", "", text)
        # Main-only self-hosted pipeline: push + manual dispatch. No pull_request —
        # PRs must not burn the laplace runner (see laplace.yml on: comment).
        if "push:branches:[main]" not in compact:
            errs.append("laplace.yml: expected push trigger on main")
        if "workflow_dispatch:" not in compact:
            errs.append("laplace.yml: expected workflow_dispatch trigger")
        if "pull_request:" in compact:
            errs.append("laplace.yml: pull_request trigger forbidden (self-hosted runner)")
        for stale in ("integration.yml", "deploy-app.yml", "seed-ladder.yml"):
            if stale in text:
                errs.append(f"laplace.yml: references retired workflow {stale}")
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
            if name == "knowledge":
                src_order = src.get("order")
                if not isinstance(src_order, int):
                    errs.append(f"manifest: knowledge source '{cli}' missing integer order")
                elif src_order <= prev_src_order:
                    errs.append(
                        f"manifest: knowledge source '{cli}' order={src_order} "
                        f"not after prior ({prev_src_order})"
                    )
                else:
                    prev_src_order = src_order
            clis.append(cli)

        if name in ("floor", "document", "knowledge", "usage") and len(clis) != len(set(clis)):
            dupes = [c for c in clis if clis.count(c) > 1]
            errs.append(f"manifest: stage '{name}' duplicate cli values: {sorted(set(dupes))}")

    return errs


def stage_cli_lists(manifest: dict) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for stage in manifest["cadence"]:
        name = stage["stage"]
        sources = stage["sources"]
        if name == "knowledge":
            ordered = sorted(sources, key=lambda s: s.get("order", 0))
            out[name] = [s["cli"] for s in ordered]
        else:
            out[name] = [s["cli"] for s in sources]
    return out


def expected_seed_stage_knowledge(manifest: dict) -> list[str]:
    """seed-stage.cmd knowledge loop = floor cili (if any) + manifest knowledge order."""
    stages = stage_cli_lists(manifest)
    prefix = [c for c in stages.get("floor", []) if c == "cili"]
    return prefix + stages["knowledge"]


def flat_ingest_all(manifest: dict) -> list[str]:
    """CLI sources ingest-source.sh `all` should run (no path-only witnesses)."""
    stages = stage_cli_lists(manifest)
    skip = {"stack", "repo", "safetensors"}
    order: list[str] = []
    for stage_name in ("floor", "document", "knowledge", "usage"):
        for cli in stages.get(stage_name, []):
            if cli in skip:
                continue
            order.append(cli)
    return order


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def extract_bash_array(text: str, var_name: str) -> list[str] | None:
    m = re.search(rf"{re.escape(var_name)}=\(([^)]*)\)", text)
    if not m:
        return None
    inner = m.group(1)
    quoted = re.findall(r'"([^"]+)"', inner)
    if quoted:
        return quoted
    return [t for t in inner.split() if t and not t.startswith("#")]


def parse_ingest_source_all(text: str) -> list[str] | None:
    floor = extract_bash_array(text, "FLOOR")
    knowledge = extract_bash_array(text, "KNOWLEDGE")
    usage = extract_bash_array(text, "USAGE")
    if not floor or not knowledge or not usage:
        return None
    return floor + ["document"] + knowledge + usage


def parse_seed_stage_knowledge(text: str) -> list[str] | None:
    m = re.search(r":stage_knowledge[\s\S]*?for %%s in \(([^)]+)\)", text, re.IGNORECASE)
    if not m:
        return None
    return [s.strip() for s in m.group(1).split() if s.strip()]


def parse_audit_ladder(text: str) -> list[str] | None:
    knowledge = extract_bash_array(text, "KNOWLEDGE")
    if not knowledge:
        return None
    base = ["iso639", *knowledge]
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


def compare_audit_base(expected_knowledge: list[str], actual: list[str] | None) -> list[str]:
    if actual is None:
        return ["audit-decomposers.sh: could not parse LADDER"]
    expected_base = ["iso639", *expected_knowledge]
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
    for key, val in re.findall(r"\[(\w+)\]=(\d+)", m.group(1)):
        out[key] = int(val)
    return out or None


def expected_audit_layer_map(knowledge: list[str]) -> dict[str, int]:
    """Sequential HasLayerCompleted ordinals: iso639=1, then knowledge sources in order."""
    out: dict[str, int] = {"iso639": 1}
    for i, cli in enumerate(knowledge, start=2):
        out[cli] = i
    out["tatoeba"] = len(knowledge) + 2
    out["opensubtitles"] = len(knowledge) + 3
    return out


def validate_decomposer_matrix(manifest: dict, gates: dict) -> list[str]:
    errs: list[str] = []
    root = ROOT

    required_scripts = [
        root / "scripts" / "decomposer-gates.json",
        root / "scripts" / "decomposer-gate-check.py",
        root / "scripts" / "decomposer-isolate-plan.py",
        root / "scripts" / "win" / "db-isolate.cmd",
        root / "scripts" / "win" / "decomposer-test.cmd",
        root / "scripts" / "win" / "decomposer-promote.cmd",
        root / "scripts" / "win" / "decomposer-matrix.cmd",
        root / "scripts" / "decomposer-isolate.sh",
        root / "scripts" / "decomposer-test.sh",
        root / "scripts" / "decomposer-matrix.sh",
    ]
    for path in required_scripts:
        if not path.is_file():
            errs.append(f"decomposer matrix: missing {path.relative_to(root)}")

    gate_sources = set(gates.get("sources", {}))
    manifest_sources: set[str] = set()
    for stage in manifest.get("cadence", []):
        if stage.get("stage") in ("code_capstone", "models"):
            continue
        for src in stage.get("sources", []):
            cli = src.get("cli")
            if cli and cli not in ("stack", "repo", "safetensors", "tiny-codes"):
                manifest_sources.add(cli)

    missing_gates = sorted(manifest_sources - gate_sources)
    if missing_gates:
        errs.append(
            f"decomposer-gates.json: missing gate entries for manifest sources: {missing_gates}"
        )

    extra_gates = sorted(gate_sources - manifest_sources)
    if extra_gates:
        errs.append(
            f"decomposer-gates.json: entries not in manifest ingest order: {extra_gates}"
        )

    manifest_order = gates.get("manifest_order", [])
    expected_order: list[str] = []
    for stage in manifest["cadence"]:
        name = stage["stage"]
        if name in ("code_capstone", "models"):
            continue
        sources = stage["sources"]
        if name == "knowledge":
            sources = sorted(sources, key=lambda s: s.get("order", 0))
        for src in sources:
            cli = src["cli"]
            if cli not in ("stack", "repo", "safetensors", "tiny-codes"):
                expected_order.append(cli)

    if manifest_order != expected_order:
        errs.append("decomposer-gates.json: manifest_order drift")
        errs.append(f"  expected: {' '.join(expected_order)}")
        errs.append(f"  actual:   {' '.join(manifest_order)}")

    return errs


_LEGACY_TYPE_PATH = re.compile(r"substrate/type/[A-Z]")
_TYPE_IDENTITY_CARVEOUTS = (
    "substrate/type/grammar/",
    "substrate/type/HasLayerCompleted/",
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
    skip_files = {"migrate-seed-type-paths.py"}

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


def main() -> int:
    if not MANIFEST.is_file():
        print(f"ERROR: manifest not found: {MANIFEST}", file=sys.stderr)
        return 2

    manifest = load_manifest()
    errs = manifest_errors(manifest)
    errs.extend(validate_type_identity_law())
    foundation = foundation_sources(manifest)
    if not foundation:
        errs.append("manifest: missing or empty foundation.sources[]")
    stages = stage_cli_lists(manifest)
    knowledge = stages["knowledge"]
    ingest_all_expected = flat_ingest_all(manifest)

    ingest_sh = ROOT / "scripts" / "ingest-source.sh"
    seed_stage = ROOT / "scripts" / "win" / "seed-stage.cmd"
    seed_foundation = ROOT / "scripts" / "win" / "seed-foundation.cmd"
    audit_sh = ROOT / "scripts" / "audit-decomposers.sh"
    ensure_foundation = ROOT / "scripts" / "ensure-foundation.sh"
    pipeline_sh = ROOT / "scripts" / "pipeline.sh"

    errs.extend(validate_retired_workflows())

    if not pipeline_sh.is_file():
        errs.append("missing canonical orchestrator: scripts/pipeline.sh")
    elif "ensure-foundation.sh" not in read_text(pipeline_sh):
        errs.append("pipeline.sh: must invoke scripts/ensure-foundation.sh")

    if not ensure_foundation.is_file():
        errs.append("missing foundation helper: scripts/ensure-foundation.sh")
    else:
        errs.extend(compare_order(
            "ensure-foundation.sh",
            foundation,
            parse_ensure_foundation_sh(read_text(ensure_foundation)),
        ))

    if seed_foundation.is_file():
        errs.extend(compare_order(
            "seed-foundation.cmd",
            foundation,
            parse_seed_foundation_cmd(read_text(seed_foundation)),
        ))
    else:
        errs.append("missing foundation helper: scripts/win/seed-foundation.cmd")

    errs.extend(compare_order(
        "ingest-source.sh all",
        ingest_all_expected,
        parse_ingest_source_all(read_text(ingest_sh)),
    ))
    errs.extend(compare_order(
        "seed-stage.cmd knowledge",
        expected_seed_stage_knowledge(manifest),
        parse_seed_stage_knowledge(read_text(seed_stage)),
    ))
    errs.extend(compare_audit_base(
        knowledge,
        parse_audit_ladder(read_text(audit_sh)),
    ))

    expected_layers = expected_audit_layer_map(knowledge)
    actual_layers = parse_audit_layer_map(read_text(audit_sh))
    if actual_layers is None:
        errs.append("audit-decomposers.sh: could not parse LAYER map")
    elif actual_layers != expected_layers:
        errs.append("audit-decomposers.sh: LAYER map drift")
        missing = sorted(set(expected_layers) - set(actual_layers))
        extra = sorted(set(actual_layers) - set(expected_layers))
        if missing:
            errs.append(f"  missing keys: {missing}")
        if extra:
            errs.append(f"  extra keys: {extra}")
        drift = [
            k for k in expected_layers
            if k in actual_layers and actual_layers[k] != expected_layers[k]
        ]
        if drift:
            errs.append(
                "  wrong ordinals: "
                + ", ".join(f"{k}={actual_layers[k]} (expected {expected_layers[k]})" for k in drift)
            )

    gates_path = ROOT / "scripts" / "decomposer-gates.json"
    if gates_path.is_file():
        with gates_path.open(encoding="utf-8") as f:
            gates_spec = json.load(f)
        errs.extend(validate_decomposer_matrix(manifest, gates_spec))
    else:
        errs.append("decomposer-gates.json: file missing")

    if errs:
        print("validate-pipeline: FAILED", file=sys.stderr)
        for e in errs:
            print(f"  ERROR: {e}", file=sys.stderr)
        return 1

    print("validate-pipeline: OK")
    print(f"  foundation:      {' -> '.join(foundation)}")
    print(f"  knowledge order: {' -> '.join(knowledge)}")
    print(f"  ingest all:      {' -> '.join(ingest_all_expected)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
