#!/usr/bin/env python3
"""Validate ingest orchestration against scripts/win/witness-manifest.json."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "scripts" / "win" / "witness-manifest.json"

# CI job -> needs (from manifest signal-dependency law)
SEED_LADDER_DEPS: dict[str, list[str]] = {
    "unicode": [],
    "iso639": ["unicode"],
    "wordnet": ["iso639"],
    "omw": ["wordnet"],
    "verbnet": ["iso639"],
    "propbank": ["iso639"],
    "framenet": ["wordnet"],
    "mapnet": ["wordnet", "framenet"],
    "wordframenet": ["wordnet", "framenet"],
    "semlink": ["wordnet", "verbnet", "propbank", "framenet"],
    "ud": ["iso639"],
    "tatoeba": ["iso639"],
    "conceptnet": ["iso639"],
    "wiktionary": ["iso639"],
    "atomic2020": ["iso639"],
    "opensubtitles": ["iso639"],
}


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


def parse_seed_ladder_yml(text: str) -> dict[str, list[str]]:
    deps: dict[str, list[str]] = {}
    current: str | None = None
    for line in text.splitlines():
        job_m = re.match(r"^  (\w+):\s*$", line)
        if job_m and not line.startswith("    "):
            current = job_m.group(1)
            if current not in ("convergence",):
                deps.setdefault(current, [])
            continue
        if current and "needs:" in line:
            need_part = line.split("needs:", 1)[1].strip()
            if need_part.startswith("["):
                jobs = re.findall(r"(\w+)", need_part)
                deps[current] = jobs
            else:
                deps[current] = [need_part]
    return deps


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


def validate_seed_ladder_deps(deps: dict[str, list[str]]) -> list[str]:
    errs: list[str] = []
    for job, needs in SEED_LADDER_DEPS.items():
        if job not in deps:
            continue
        actual = deps[job]
        for req in needs:
            if req not in actual:
                errs.append(
                    f"seed-ladder.yml: job '{job}' needs '{req}' "
                    f"(manifest dependency); has {actual or '[]'}"
                )
    semlink_needs = deps.get("semlink", [])
    for req in ("wordnet", "verbnet", "propbank", "framenet"):
        if req not in semlink_needs:
            errs.append(f"seed-ladder.yml: semlink must need {req}; has {semlink_needs}")
    for bridge in ("mapnet", "wordframenet"):
        bridge_needs = deps.get(bridge, [])
        for req in ("wordnet", "framenet"):
            if bridge not in deps:
                continue
            if req not in bridge_needs:
                errs.append(f"seed-ladder.yml: {bridge} must need {req}; has {bridge_needs}")
    if "wordnet" not in deps.get("omw", []):
        errs.append("seed-ladder.yml: omw must need wordnet")
    return errs


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


def main() -> int:
    if not MANIFEST.is_file():
        print(f"ERROR: manifest not found: {MANIFEST}", file=sys.stderr)
        return 2

    manifest = load_manifest()
    errs = manifest_errors(manifest)
    stages = stage_cli_lists(manifest)
    knowledge = stages["knowledge"]
    ingest_all_expected = flat_ingest_all(manifest)

    ingest_sh = ROOT / "scripts" / "ingest-source.sh"
    seed_stage = ROOT / "scripts" / "win" / "seed-stage.cmd"
    audit_sh = ROOT / "scripts" / "audit-decomposers.sh"
    seed_yml = ROOT / ".github" / "workflows" / "seed-ladder.yml"

    errs.extend(compare_order(
        "ingest-source.sh all",
        ingest_all_expected,
        parse_ingest_source_all(read_text(ingest_sh)),
    ))
    errs.extend(compare_order(
        "seed-stage.cmd knowledge",
        knowledge,
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

    seed_deps = parse_seed_ladder_yml(read_text(seed_yml))
    errs.extend(validate_seed_ladder_deps(seed_deps))

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
    print(f"  knowledge order: {' -> '.join(knowledge)}")
    print(f"  ingest all:      {' -> '.join(ingest_all_expected)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
