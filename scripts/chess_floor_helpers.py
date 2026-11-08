#!/usr/bin/env python3
"""Helper functions for export-qualified-chess-floors.py."""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys


def load(path, limit=4 * 1024 * 1024):
    with Path(path).open("rb") as source:
        raw = source.read(limit + 1)
    if len(raw) > limit:
        raise ValueError("metadata exceeds its fixed envelope")
    return json.loads(raw)


def module(root, name, filename):
    spec = importlib.util.spec_from_file_location(name, root / "scripts" / filename)
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value


def selection_identity(plan):
    """Select immutable source identities; qualification authenticates their proof."""
    if not isinstance(plan, dict):
        raise ValueError("export selection must be a metadata object")
    values = [plan.get(key) for key in ("candidate_commit", "candidate_tree", "installed_source")]
    if any(not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{40}", value) is None
           for value in values):
        raise ValueError("export source must use complete immutable commit and tree identities")
    if values[0] != values[2]:
        raise ValueError("export candidate must be the selected installed source")
    return {"commit": values[0], "tree": values[1]}


def file_identity(path):
    path = Path(path)
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1048576), b""):
            digest.update(block)
    return {"path": str(path.resolve(strict=True)), "bytes": path.stat().st_size,
            "sha256": digest.hexdigest()}


def native_managed_outcome(outcome, expected_outcome, source, directory, receipts):
    """Retain separately completed post-native publication without rewriting the native phase receipt."""
    legacy = {"workflowOutcome": expected_outcome, "source": source["commit"],
              "applicationPublication": "not_attempted", "fullLifecyclePassed": False}
    if outcome == legacy:
        return {"status": "not_attempted"}
    required = set(legacy) | {"schema", "applicationPublicationReceipt",
                             "applicationPublicationReceiptSha256", "applicationPublicationSource"}
    if (not isinstance(outcome, dict) or set(outcome) != required
            or outcome.get("schema") != "laplace.native-recording-workflow-outcome/v2"
            or outcome.get("workflowOutcome") != expected_outcome
            or outcome.get("source") != source["commit"]
            or outcome.get("fullLifecyclePassed") is not False
            or outcome.get("applicationPublication") != "completed"
            or outcome.get("applicationPublicationSource") != source):
        raise ValueError("native installation retained workflow outcome differs")
    publication = directory / "managed-after-native"
    def retained(name, expected_hash):
        path = publication / name
        if (path.is_symlink() or not path.is_file() or path.resolve(strict=True) != path
                or path.stat().st_uid != os.getuid() or path.stat().st_size > 4 * 1024 * 1024
                or not isinstance(expected_hash, str) or re.fullmatch(r"[0-9a-f]{64}", expected_hash) is None
                or file_identity(path)["sha256"] != expected_hash):
            raise ValueError("native installation managed publication evidence changed: " + name)
        receipts["managed-after-native/" + name] = {"path": str(path), "sha256": expected_hash}
        return load(path)
    receipt_path = publication / "receipt.json"
    if outcome["applicationPublicationReceipt"] != str(receipt_path):
        raise ValueError("native installation managed publication path differs")
    managed = retained("receipt.json", outcome["applicationPublicationReceiptSha256"])
    phases = ["bind-publication-owners", "prepare-managed-policy", "publish-managed-services"]
    for role in ("api", "uci", "mcp", "lichess"):
        phases += ["reference-" + role]
        phases += (["seal-" + role + "-payload"] if role in ("api", "uci")
                   else ["seal-" + role + "-files", "seal-" + role + "-native"])
        phases += ["verify-" + role + "-payload"]
    phases += ["authenticated-managed-readiness"]
    actual = managed.get("phases", [])
    # Historical receipts predate explicit recovery; new publications retain it
    # as a separate passed phase before any preparation or payload mutation.
    with_recovery = phases[:1] + ["recover-managed-services"] + phases[1:]
    if (managed.get("schema") != "laplace.managed-followthrough/v1"
            or managed.get("status") != "completed" or managed.get("mode") != "publish"
            or managed.get("source") != source
            or [row.get("name") for row in actual] not in (phases, with_recovery)
            or any(row.get("status") != "passed" for row in actual)
            or managed.get("authenticatedMcpInitializeAndDiscovery") is not True
            or managed.get("lichess", {}).get("status", {}).get("ready") is not True):
        raise ValueError("native installation managed publication did not complete its exact source and readiness proof")
    owners = managed.get("publicationOwners", {})
    if owners.get("path") != str(publication / "publication-owners.json"):
        raise ValueError("native installation managed owner receipt path differs")
    owner_receipt = retained("publication-owners.json", owners.get("sha256"))
    if owner_receipt.get("source") != source or set(owner_receipt.get("owners", {})) != {"managed", "publisher", "startup"}:
        raise ValueError("native installation managed owner source differs")
    payloads = managed.get("payloads", {})
    if set(payloads) != {"api", "uci", "mcp", "lichess"}:
        raise ValueError("native installation managed payload closure is incomplete")
    for role, row in payloads.items():
        manifest_name, verified_name = role + "-manifest.json", role + "-verified.json"
        if (row.get("manifest") != str(publication / manifest_name)
                or row.get("verified") != str(publication / verified_name)):
            raise ValueError("native installation managed payload evidence path differs")
        manifest = retained(manifest_name, row.get("manifestSha256"))
        verified = retained(verified_name, row.get("verifiedSha256"))
        expected_files = manifest.get("files")
        observed_files = verified.get("payload" if role in ("api", "uci") else "files")
        if (verified.get("status") != "passed" or not isinstance(expected_files, dict)
                or not expected_files or not isinstance(observed_files, dict)
                or set(expected_files) != set(observed_files)):
            raise ValueError("native installation managed payload verification is incomplete")
        native_names = ("liblaplace_core.so", "liblaplace_dynamics.so",
                        "liblaplace_synthesis.so", "liblaplace_syzygy.so")
        assembly = {"api": "Laplace.Endpoints.OpenAICompat", "uci": "laplace-uci",
                    "mcp": "Laplace.Endpoints.Mcp", "lichess": "Laplace.Endpoints.Lichess"}[role]
        required_files = {assembly + ".dll", *native_names}
        if role in ("uci", "mcp", "lichess"):
            required_files.update((assembly + ".deps.json", assembly + ".runtimeconfig.json"))
        if role in ("api", "uci"):
            required_files.update(("Laplace.Core.dll", "Laplace.Chess.dll"))
        if role == "api":
            required_files.add("wwwroot/index.html")
        if role == "uci":
            required_files.add("laplace-uci")
        native = manifest.get("native_sources")
        if (not required_files.issubset(expected_files) or not isinstance(native, dict)
                or not set(native_names).issubset(native)):
            raise ValueError("native installation managed payload required closure is incomplete")
        for name, native_fact in native.items():
            if (name not in expected_files or not isinstance(native_fact, dict)
                    or any(native_fact.get(key) != expected_files[name].get(key) for key in ("bytes", "sha256"))):
                raise ValueError("native installation managed native file closure differs")
        for name, fact in expected_files.items():
            observed = observed_files[name]
            if (not isinstance(fact, dict) or not isinstance(observed, dict)
                    or type(fact.get("bytes")) is not int or fact["bytes"] < 0
                    or not isinstance(fact.get("sha256"), str)
                    or re.fullmatch(r"[0-9a-f]{64}", fact["sha256"]) is None
                    or any(observed.get(key) != fact[key] for key in ("bytes", "sha256"))):
                raise ValueError("native installation managed verified file differs: " + role)
        if role in ("api", "uci"):
            if (manifest.get("schema") != "laplace." + role + "-payload/v1"
                    or verified.get("schema") != "laplace." + role + "-payload-verification/v1"
                    or manifest.get("provenance") != "selected-build"
                    or verified.get("provenance") != "selected-build"
                    or verified.get("native_sources") != manifest.get("native_sources")):
                raise ValueError("native installation managed native payload provenance differs")
        elif manifest.get("source") != source:
            raise ValueError("native installation managed source differs")
    return {"status": "completed", "source": source, "receipt": str(receipt_path),
            "receipt_sha256": outcome["applicationPublicationReceiptSha256"],
            "scope": "Separately completed managed publication after the immutable native phase receipt"}


def verify_qualification_receipts(qualification):
    if qualification.get("proof_kind") != "native-only-install":
        raise ValueError("export requires an authenticated native-only installation proof")
    for row in qualification["evidence_receipts"]:
        if file_identity(Path(row["path"]))["sha256"] != row["sha256"]:
            raise ValueError("qualified lifecycle evidence changed")


def recording_compatible(guard, before, after):
    """Retain all runtime identities; only observed journal occupancy may differ."""
    for state in (before, after):
        if (not isinstance(state, dict) or type(state.get("format")) is not int
                or state["format"] != 3):
            raise ValueError("pilot installed runtime has an unsupported format")
    if not guard.compatible(before, after, purpose="recording"):
        raise ValueError("installed native/database selection differs from the recording baseline")


def installed_compatible(guard, baseline, observed):
    """Compare a completed pilot with independently verified current installation.

    A later restart or byte-identical atomic replacement does not change the
    proven code, SQL or database incarnation. Both snapshots retain their actual
    postmaster start and mapped file identities; only this later-use comparison
    excludes those lifetime observations. Current snapshot/mapping validation
    must already have rejected deleted, unselected or disk-mismatched mappings.
    Recording and export intervals continue to use recording_compatible exactly.
    """
    states = copy.deepcopy((baseline, observed))
    for state in states:
        database = state["database"]
        started = database.pop("postmaster_started")
        if not isinstance(started, str) or not started:
            raise ValueError("installed runtime postmaster observation is missing")
        mappings = database["native_mappings"]
        if not isinstance(mappings, dict) or not mappings:
            raise ValueError("installed runtime mapped generation is missing")
        for relative, identity in mappings.items():
            if (not isinstance(relative, str) or not relative.endswith(".so")
                    or not isinstance(identity, dict)
                    or set(identity) != {"path", "device", "inode"}
                    or not isinstance(identity["path"], str)
                    or not Path(identity["path"]).is_absolute()
                    or type(identity["device"]) is not int or identity["device"] < 0
                    or type(identity["inode"]) is not int or identity["inode"] <= 0):
                raise ValueError("installed runtime mapped identity is invalid")
            mappings[relative] = {"path": identity["path"]}
    recording_compatible(guard, *states)


def payload(artifact, directory):
    rows = {}
    for path in sorted(directory.rglob("*")):
        if path.is_file():
            if path.is_symlink():
                raise ValueError("managed publication may not use an unbound symlink")
            rows[str(path.relative_to(directory))] = {"bytes": path.stat().st_size,
                                                     "sha256": artifact.sha256(path)}
    if not rows:
        raise ValueError("managed publication is empty")
    return rows


def publish_selection(artifact, owner, prefix, exported, proof, publish):
    """Observe the atomic publisher's actual outcome even when its process fails."""
    proof.update(selection_attempted=True, selection_completed=None)
    primary = None
    try:
        publish()
    except BaseException as error:
        primary = error
        proof["status"] = "failed"
        raise
    finally:
        try:
            # The full existing export validator owns readback. Bound it separately
            # so cancellation cannot turn an unknown publication outcome into false.
            with owner.deadline(120):
                path = prefix / "etc/chess-corpus-export.json"
                before = artifact.sha256(path) if path.exists() else None
                actual = artifact.selected_export(prefix)
                after = artifact.sha256(path) if path.exists() else None
                if before != after:
                    raise ValueError("persisted selection changed during final readback")
                proof["selection_completed"] = actual == exported
                proof["selection_observation"] = (
                    "matching-export" if actual == exported else
                    "no-persisted-selection" if actual is None else "different-export")
                proof["selection"] = {"path": str(path), "sha256": after}
        except BaseException as error:
            proof.update(status="failed", selection_completed=None, selection_observation="unknown",
                         selection_readback_failure_type=type(error).__name__)
            if primary is None:
                raise
    if proof["selection_completed"] is not True:
        proof["status"] = "failed"
        raise ValueError("persisted selection does not resolve to the completed export")
