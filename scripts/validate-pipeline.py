#!/usr/bin/env python3
"""Validate ingest orchestration against scripts/win/witness-manifest.json."""

from __future__ import annotations

import json
from pathlib import Path
import re
import runpy
import subprocess
import sys

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from ingest_catalog import canonical_source, load_catalog
from validate_pipeline_checks import (  # noqa: F401
    MANIFEST,
    ROOT,
    _LEGACY_TYPE_PATH,
    _TYPE_IDENTITY_CARVEOUTS,
    _legacy_type_path_hits,
    _resolver_case_labels,
    _seed_workflow_sources,
    _validate_seed_preset_routes,
    _workflow_choice_options,
    compare_audit_base,
    compare_order,
    expected_audit_layer_map,
    expected_seed_stage_relations,
    extract_bash_array,
    flat_ingest_all,
    foundation_sources,
    load_manifest,
    manifest_errors,
    parse_audit_ladder,
    parse_audit_layer_map,
    parse_seed_foundation_cmd,
    parse_seed_stage_relations,
    read_text,
    stage_cli_lists,
    validate_foundation_indexes_stay_online,
    validate_retired_workflows,
    validate_seed_chess_routes,
    validate_seed_code_routes,
    validate_type_identity_law,
)


def validate_decomposer_matrix(manifest: dict, gates: dict) -> list[str]:
    errs: list[str] = []
    root = ROOT

    errs.extend(validate_seed_chess_routes())
    errs.extend(validate_seed_code_routes())

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
                manifest_sources.add(canonical_source(cli))

    missing_gates = sorted(manifest_sources - gate_sources)
    if missing_gates:
        errs.append(
            f"decomposer-gates.json: missing gate entries for manifest sources: {missing_gates}"
        )

    # CLI keys used only for journal name resolution (verify-ingest-journal.sh
    # --cli-key) may sit outside the foundation/relations ladder.
    #
    # What this check is actually protecting against is an ORPHAN GATE: thresholds
    # nothing ever runs, which rot silently and prove nothing. Ladder membership was
    # the proxy for "something runs it", and for a foundation source it is the right
    # proxy. It is the wrong proxy for a MODALITY lane: chess is not seeded by the
    # foundation ladder and must not be added to it just to earn the right to be
    # verified — that would put an 8 GB corpus in the floor sequence to satisfy a
    # lint. Under the old rule the only way to keep Policy green was to leave every
    # chess lane with empty consensus_gates, i.e. permanently unverifiable, which is
    # how five chess seed workflows came to be green on the ingest exit code alone.
    #
    # So test reachability directly: a key is legitimate off-ladder when a seed
    # workflow can dispatch it. Orphan thresholds still fail.
    sources_spec = gates.get("sources", {})
    dispatchable = {canonical_source(key) for key in _seed_workflow_sources()}
    dispatchable.update(source["name"] for source in load_catalog()["sources"])
    orphan_consensus = sorted(
        key
        for key in (gate_sources - manifest_sources)
        if sources_spec.get(key, {}).get("consensus_gates") and key not in dispatchable
    )
    if orphan_consensus:
        errs.append(
            "decomposer-gates.json: consensus_gates on sources no ladder stage and no "
            f"seed workflow can run: {orphan_consensus}"
        )

    if "manifest_order" in gates:
        errs.append("decomposer-gates.json must not duplicate recipes/order")
    try:
        planner = runpy.run_path(str(ROOT / "scripts" / "decomposer-isolate-plan.py"))
        planner["source_order"]()
    except (OSError, ValueError) as error:
        errs.append(f"canonical recipes/order: {error}")

    return errs


# EtlManifest rows that are a phase of another source rather than a route of their own.
_ETL_PHASE_OF = {"predicatematrix": "semlink"}


def validate_layer_order(manifest: dict, gates: dict) -> list[str]:
    """Order and layer are declared once (#1882).

    Order: witness-manifest.json cadence (and foundation.sources for the foundation
    batch); ensure-foundation.sh and ingest-source.sh read it, and the copies that
    cannot read it are checked against it here.

    Layer: the runtime layer of a recipe-owned source is its selected recipe's
    layerOrder (the selected generation wins dispatch and owns its completion);
    decomposer-gates.json is what completion checks read. The manifest's layer, the
    gates and EtlManifest's descriptive rows must all agree with it.
    """
    errs: list[str] = []
    sources = gates.get("sources", {})

    def gate_layer(key: str):
        return sources.get(canonical_source(key), {}).get("layer")

    for stage in manifest.get("cadence", []):
        if stage.get("stage") not in ("floor", "document", "relations", "usage"):
            continue
        for src in stage.get("sources", []):
            cli, layer = src.get("cli"), src.get("layer")
            if canonical_source(cli) not in sources:
                errs.append(f"decomposer-gates.json: no entry for ladder source '{cli}'")
                continue
            if layer is not None and layer != f"L{gate_layer(cli)}":
                errs.append(
                    f"layer drift: {cli} is {layer} in witness-manifest.json, "
                    f"{gate_layer(cli)} in decomposer-gates.json"
                )

    for path in sorted((ROOT / "recipes").glob("**/*.source.json")):
        recipe = json.loads(path.read_text(encoding="utf-8"))
        if not recipe.get("selected"):
            continue
        layer = recipe.get("layerOrder", 0)
        for alias in recipe.get("aliases") or []:
            if canonical_source(alias) in sources and gate_layer(alias) != layer:
                errs.append(
                    f"layer drift: {alias} runs at layerOrder {layer} "
                    f"({path.relative_to(ROOT).as_posix()}), {gate_layer(alias)} in decomposer-gates.json"
                )

    etl = ROOT / "app" / "Laplace.Substrate" / "Abstractions" / "EtlManifest.cs"
    if etl.is_file():
        for name, layer in re.findall(r'\["([\w-]+)"\] = Row\("[\w-]+", "\w+", (\d+)', read_text(etl)):
            key = canonical_source(_ETL_PHASE_OF.get(name, name))
            if key in sources and gate_layer(key) != int(layer):
                errs.append(
                    f"layer drift: {name} is layer {layer} in EtlManifest.cs, "
                    f"{gate_layer(key)} in decomposer-gates.json"
                )

    foundation = manifest.get("foundation", {}).get("sources", [])
    for cli in foundation:
        if canonical_source(cli) not in sources:
            errs.append(f"decomposer-gates.json: no entry for foundation source '{cli}'")

    floor = [
        src.get("cli")
        for stage in manifest.get("cadence", [])
        if stage.get("stage") == "floor"
        for src in stage.get("sources", [])
    ]
    ladder = {
        src.get("cli")
        for stage in manifest.get("cadence", [])
        if stage.get("stage") in ("floor", "relations")
        for src in stage.get("sources", [])
    }
    workflow = read_text(ROOT / ".github" / "workflows" / "seed-foundation.yml")
    source_input = re.search(r"^      source:\n((?:^        .*\n)*)", workflow, re.M)
    body = source_input.group(1) if source_input else ""
    choices = re.search(r"^        options: *\[([^\]]*)\]$", body, re.M)
    options = [value.strip() for value in choices.group(1).split(",")] if choices else []
    selected = {canonical_source(value) for value in options if value != "ladder"}
    if (not re.search(r"^        type: choice$", body, re.M)
            or "ladder" not in options or len(options) != len(set(options))
            or selected != {canonical_source(value) for value in foundation}):
        errs.append("seed-foundation.yml source dropdown must offer the ladder and every canonical foundation source")

    ingest_text = read_text(ROOT / "scripts" / "ingest-source.sh")
    other = re.search(r'^OTHER_SOURCES="([^"]*)"', ingest_text, re.M)
    if not other:
        errs.append("ingest-source.sh: could not parse OTHER_SOURCES")
    else:
        for key in other.group(1).split():
            if key.startswith("chess") and key not in sources:
                errs.append(f"decomposer-gates.json: no entry for ingest-source.sh source '{key}'")

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
    relations = stages["relations"]
    ingest_all_expected = flat_ingest_all(manifest)

    ingest_sh = ROOT / "scripts" / "ingest-source.sh"
    seed_stage = ROOT / "scripts" / "win" / "seed-stage.cmd"
    seed_foundation = ROOT / "scripts" / "win" / "seed-foundation.cmd"
    audit_sh = ROOT / "scripts" / "audit-decomposers.sh"
    ensure_foundation = ROOT / "scripts" / "ensure-foundation.sh"
    pipeline_sh = ROOT / "scripts" / "pipeline.sh"

    errs.extend(validate_retired_workflows())
    errs.extend(validate_foundation_indexes_stay_online())

    ingest_text = read_text(ingest_sh)
    if "LAPLACE_INGEST_FORCE" not in ingest_text or "ingest_args+=(--force)" not in ingest_text:
        errs.append(
            "ingest-source.sh: must translate LAPLACE_INGEST_FORCE into the CLI --force flag"
        )
    seed_workflow = read_text(ROOT / ".github" / "workflows" / "seed.yml")
    if "export LAPLACE_INGEST_FORCE=1" not in seed_workflow:
        errs.append(
            "seed.yml: evict_before_ingest must force the replacement observation"
        )
    if 'scripts/measure-lane.sh -- "$GITHUB_WORKSPACE/scripts/laplace" evict "$src_name"' not in seed_workflow:
        errs.append("seed.yml: source eviction must use the measured canonical CLI path")
    eviction_sql = read_text(
        ROOT / "extension" / "laplace_substrate" / "sql" / "functions" / "ops" / "evict_source.sql.in"
    )
    # Resume is decided by unit and layer completion receipts; eviction must clear both.
    for receipt in ("layer completion(s)", "unit completion(s)", "replay claim(s)"):
        if receipt not in eviction_sql:
            errs.append(f"evict_source.sql.in: eviction receipt missing {receipt}")

    if not pipeline_sh.is_file():
        errs.append("missing canonical orchestrator: scripts/pipeline.sh")
    else:
        pipeline_text = read_text(pipeline_sh)
        if "ensure-foundation.sh" not in pipeline_text:
            errs.append("pipeline.sh: must invoke scripts/ensure-foundation.sh")
        for target in (
            "laplace_t0_perfcache",
            "laplace_highway_perfcache",
            "laplace_chess_position_perfcache",
            "laplace_modality_number_perfcache",
        ):
            if not re.search(
                rf'cmake --build[^\n]*(?:\\\n[^\n]*)*\b{re.escape(target)}\b',
                pipeline_text,
            ):
                errs.append(f"pipeline.sh: product build must request target {target}")

    if not ensure_foundation.is_file():
        errs.append("missing foundation helper: scripts/ensure-foundation.sh")
    else:
        ef_text = read_text(ensure_foundation)
        if "win/witness-manifest.json" not in ef_text or '["foundation"]["sources"]' not in ef_text:
            errs.append(
                "ensure-foundation.sh: must read its batch from witness-manifest.json foundation.sources"
            )

    if seed_foundation.is_file():
        errs.extend(compare_order(
            "seed-foundation.cmd",
            foundation,
            parse_seed_foundation_cmd(read_text(seed_foundation)),
        ))
    else:
        errs.append("missing foundation helper: scripts/win/seed-foundation.cmd")

    try:
        planner = runpy.run_path(str(ROOT / "scripts" / "decomposer-isolate-plan.py"))
        actual = subprocess.run(["bash", str(ingest_sh), "--list-sources"],
                                text=True, capture_output=True, check=True, timeout=30)
        errs.extend(compare_order("ingest-source.sh canonical recipe order",
                                  planner["source_order"](), actual.stdout.splitlines()))
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        errs.append(f"ingest-source.sh: could not resolve canonical recipes/order: {error}")
    errs.extend(compare_order(
        "seed-stage.cmd relations",
        expected_seed_stage_relations(manifest),
        parse_seed_stage_relations(read_text(seed_stage)),
    ))
    errs.extend(compare_audit_base(
        relations,
        parse_audit_ladder(read_text(audit_sh)),
    ))

    expected_layers = expected_audit_layer_map(relations)
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
        errs.extend(validate_layer_order(manifest, gates_spec))
    else:
        errs.append("decomposer-gates.json: file missing")

    if errs:
        print("validate-pipeline: FAILED", file=sys.stderr)
        for e in errs:
            print(f"  ERROR: {e}", file=sys.stderr)
        return 1

    print("validate-pipeline: OK")
    print(f"  foundation:      {' -> '.join(foundation)}")
    print(f"  relations order: {' -> '.join(relations)}")
    print(f"  ingest all:      {' -> '.join(ingest_all_expected)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
