#!/usr/bin/env python3
"""Admit and read back the exact operational bundle for this product invocation."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SOURCE = "OperationalDecomposer"
EXPECTED_ARTIFACTS = 14
# This is the existing file-resume-fingerprint/v1 framing, not a tuning knob.
# IngestBatchPipeline.TryResolveFileIdentity uses the same 4 MiB byte blocks.
FINGERPRINT_BLOCK_BYTES = 4 << 20


def authored_selection(root: Path) -> dict[str, Path]:
    """Resolve the one literal project inventory without a build or native runtime."""
    project = root / "app/Laplace.Decomposers/Laplace.Decomposers.csproj"
    selected: dict[str, Path] = {}
    for item in ET.parse(project).getroot().iter("Content"):
        link = item.get("Link", "")
        if not link.startswith("seeds/operational/"):
            continue
        if not link.endswith("%(Filename)%(Extension)"):
            raise RuntimeError("operational bundle Link must declare a literal directory and filename")
        for include in item.get("Include", "").split(";"):
            if not include or any(c in include for c in "*?%$"):
                raise RuntimeError("operational bundle inputs must remain literal authored files")
            source = (project.parent / include).resolve()
            if not source.is_relative_to(root.resolve()):
                raise RuntimeError("operational artifact is outside the source checkout")
            relative = link.removeprefix("seeds/operational/").replace(
                "%(Filename)%(Extension)", source.name)
            if (Path(relative).is_absolute() or ".." in Path(relative).parts
                    or any(c in relative or c in str(source) for c in ("\n", "\r", "\x00"))):
                raise RuntimeError("operational artifact paths must be single relative inventory lines")
            if relative in selected:
                raise RuntimeError(f"duplicate operational bundle path: {relative}")
            if not source.is_file() or source.stat().st_size == 0:
                raise RuntimeError(f"empty authored operational artifact: {relative}")
            selected[relative] = source
    if len(selected) != EXPECTED_ARTIFACTS:
        raise RuntimeError(f"expected {EXPECTED_ARTIFACTS} authored artifacts, found {len(selected)}")
    return dict(sorted(selected.items()))


def authored_bundle(root: Path, bundle: Path) -> dict[str, bytes]:
    selected: dict[str, bytes] = {}
    for relative, source in authored_selection(root).items():
        payload = source.read_bytes()
        if not payload:
            raise RuntimeError(f"empty authored operational artifact: {relative}")
        if (bundle / relative).read_bytes() != payload:
            raise RuntimeError(f"bundled bytes differ from authored artifact: {relative}")
        selected[relative] = payload
    actual = {p.relative_to(bundle).as_posix() for p in bundle.rglob("*") if p.is_file()}
    if actual != set(selected):
        raise RuntimeError("bundled artifact paths differ from the declared project selection")
    return dict(sorted(selected.items()))


def read_run_receipt(path: Path) -> dict:
    receipt = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(receipt, dict):
        raise RuntimeError("ingest run receipt must be an object")
    run_id = receipt.get("run_id")
    if not isinstance(run_id, str) or str(uuid.UUID(run_id)) != run_id:
        raise RuntimeError("ingest run receipt has an invalid UUID")
    if receipt.get("source_name") != SOURCE or receipt.get("layer") != 2:
        raise RuntimeError("ingest run receipt names another source or layer")
    if not re.fullmatch(r"[0-9a-f]{32}", receipt.get("source_id", "")):
        raise RuntimeError("ingest run receipt has an invalid source identity")
    return receipt


def sql_text(value: str) -> str:
    # SQL arrives on psql stdin; no shell evaluation or command-line source bytes.
    return "convert_from(decode('" + value.encode("utf-8").hex() + "','hex'),'UTF8')"


def fingerprint_sql(payload: bytes) -> str:
    parts = ["realize.canonical_id('substrate/file-resume-fingerprint/v1')",
             "public.laplace_hash128_blake3(decode('"
             + len(payload).to_bytes(8, "little", signed=True).hex() + "','hex'))"]
    parts.extend("public.laplace_hash128_blake3(decode('"
                 + payload[start:start + FINGERPRINT_BLOCK_BYTES].hex() + "','hex'))"
                 for start in range(0, len(payload), FINGERPRINT_BLOCK_BYTES))
    return "public.laplace_hash128_merkle(0::smallint,ARRAY[" + ",".join(parts) + "])"


def readback_sql(receipt: dict, selected: dict[str, bytes]) -> str:
    values = ",\n".join(
        f"({sql_text(path)},{len(payload)}::bigint,{fingerprint_sql(payload)})"
        for path, payload in selected.items())
    # UUID was validated at the receipt boundary. Every source byte is passed as
    # hex to native identity functions; Python only frames exact byte chunks.
    return f"""
WITH expected(relative_path, bytes, fingerprint) AS MATERIALIZED (VALUES
{values}
), run AS MATERIALIZED (
    SELECT * FROM laplace.ingest_run_journal WHERE run_id = '{receipt['run_id']}'::uuid
), files AS (
    SELECT f.file_label, f.relative_path, f.source_name, f.disposition,
           f.status, f.bytes, f.ended_at, f.error,
           encode(f.file_id,'hex') AS file_id,
           encode(f.resume_fingerprint,'hex') AS resume_fingerprint,
           encode(e.fingerprint,'hex') AS expected_fingerprint,
           e.bytes AS expected_bytes,
           EXISTS (
               SELECT 1 FROM laplace.attestations a
               WHERE a.type_id = realize.canonical_id('substrate/type/HasLayerCompleted/2/v1')
                 AND a.source_id = e.fingerprint
                 AND a.subject_id = e.fingerprint AND a.object_id = e.fingerprint
                 AND a.context_id = laplace.source_id('{SOURCE}')
           ) AS completion_present
    FROM laplace.ingest_file_journal f
    LEFT JOIN expected e ON e.relative_path = f.relative_path
    WHERE f.run_id = '{receipt['run_id']}'::uuid
)
SELECT json_build_object(
    'expected_source_id', encode(laplace.source_id('{SOURCE}'),'hex'),
    'run', (SELECT json_build_object(
        'run_id', run_id, 'source_name', source_name, 'source_id', encode(source_id,'hex'),
        'layer', layer, 'status', status, 'evidence_persisted', evidence_persisted,
        'files_done', files_done, 'files_total', files_total, 'units_failed', units_failed,
        'ended_at', ended_at, 'error', error) FROM run),
    'files', COALESCE((SELECT json_agg(files ORDER BY relative_path, file_label) FROM files),'[]'::json)
);
"""


def database_readback(receipt: dict, selected: dict[str, bytes]) -> dict:
    database = os.environ.get("LAPLACE_DBNAME") or os.environ.get("PGDATABASE", "laplace")
    command = ["psql", "-X", "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
               "-U", os.environ.get("PGUSER", "laplace_admin"), "-d", database,
               "-v", "ON_ERROR_STOP=1", "-tA"]
    result = subprocess.run(command, input=readback_sql(receipt, selected), text=True,
                            capture_output=True, check=False)
    if result.returncode:
        # psql errors may echo SQL source lines containing byte payloads.
        raise RuntimeError(f"operational database readback failed (psql exit {result.returncode})")
    return json.loads(result.stdout)


def validate_readback(receipt: dict, selected: dict[str, bytes], report: dict) -> None:
    run = report.get("run")
    if not isinstance(run, dict) or run.get("run_id") != receipt["run_id"]:
        raise RuntimeError("the exact invocation has no matching durable run")
    if (run.get("source_name") != SOURCE or run.get("layer") != 2
            or run.get("source_id") != receipt["source_id"]
            or receipt["source_id"] != report.get("expected_source_id")):
        raise RuntimeError("durable run source identity differs from the requested invocation")
    if (run.get("status") != "ok" or run.get("evidence_persisted") is not True
            or run.get("units_failed") != 0 or not run.get("ended_at") or run.get("error")):
        raise RuntimeError(f"operational run is not a complete durable success: {run.get('status')}")
    if run.get("files_done") != len(selected) or run.get("files_total") != len(selected):
        raise RuntimeError("operational run file totals do not cover the authored selection")
    files = report.get("files")
    if not isinstance(files, list) or len(files) != len(selected):
        raise RuntimeError("operational file journal does not cover the exact selected artifact set")
    paths = [f.get("relative_path") for f in files]
    if len(set(paths)) != len(paths) or set(paths) != set(selected):
        raise RuntimeError("operational file journal paths differ from the authored selection")
    for file in files:
        path = file["relative_path"]
        if (file.get("source_name") != SOURCE or file.get("file_label") != "operational/" + path
                or file.get("disposition") != "admitted"
                or file.get("status") not in {"ok", "skipped-complete"}
                or not file.get("ended_at") or file.get("error")):
            raise RuntimeError(f"operational artifact did not complete admission: {path}")
        if (file.get("bytes") != len(selected[path]) or file.get("expected_bytes") != len(selected[path])
                or not file.get("expected_fingerprint")
                or file.get("resume_fingerprint") != file.get("expected_fingerprint")):
            raise RuntimeError(f"operational artifact byte fingerprint differs: {path}")
        if file.get("completion_present") is not True:
            raise RuntimeError(f"operational artifact has no exact source completion marker: {path}")


def seed_and_verify(root: Path = ROOT) -> dict:
    # This wrapper is the default product seed, not the operator's custom ingest
    # path. A new private directory prevents a prior invocation's receipt reuse.
    scratch = Path(os.environ.get("TMPDIR", str(root / "build")))
    with tempfile.TemporaryDirectory(prefix="operational-seed-", dir=scratch) as directory:
        receipt_path = Path(directory) / "run.json"
        environment = dict(os.environ, LAPLACE_INGEST_RUN_RECEIPT_PATH=str(receipt_path),
                           LAPLACE_INGEST_MAX_UNITS="0", LAPLACE_INGEST_FORCE="0")
        subprocess.run(["bash", str(root / "scripts/ingest-source.sh"), "operational"],
                       cwd=root, env=environment, check=True)
        receipt = read_run_receipt(receipt_path)
        bundle = root / "app/Laplace.Cli/bin/Release/net10.0/seeds/operational"
        selected = authored_bundle(root, bundle)
        report = database_readback(receipt, selected)
        validate_readback(receipt, selected, report)
        return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ingest", action="store_true",
                      help="run the default product seed and verify that exact invocation")
    mode.add_argument("--list-source-paths", action="store_true",
                      help="list exact project-selected repository source paths without a build or ingestion")
    parser.add_argument("--report", type=Path,
                        help="retain the exact validated run/file report for later product proof")
    args = parser.parse_args()
    if args.list_source_paths:
        if args.report is not None:
            parser.error("--report belongs to --ingest")
        try:
            paths = sorted({path.relative_to(ROOT.resolve()).as_posix()
                            for path in authored_selection(ROOT).values()})
        except (OSError, ValueError, RuntimeError, ET.ParseError) as error:
            print(f"OPERATIONAL_SELECTION_FAIL {error}", file=sys.stderr)
            return 1
        print("\n".join(paths))
        return 0

    def retain(value: dict) -> None:
        if args.report is None:
            return
        args.report.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.report.with_name(args.report.name + f".tmp-{os.getpid()}")
        with temporary.open("w", encoding="utf-8") as output:
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, args.report)

    try:
        # A failed new invocation must never leave an older successful report
        # available for post-publication proof to mistake as this invocation.
        retain({"disposition": "running"})
        report = seed_and_verify()
        retain({**report, "disposition": "verified"})
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        retain({"disposition": "failed", "error": str(error)})
        print(f"OPERATIONAL_SEED_FAIL {error}", file=sys.stderr)
        return 1
    print(f"OPERATIONAL_SEED_OK run={report['run']['run_id']} "
          f"source={report['expected_source_id']} files={len(report['files'])}")
    for file in report["files"]:
        print(f"OPERATIONAL_ARTIFACT path={file['relative_path']} status={file['status']} "
              f"bytes={file['bytes']} fingerprint={file['resume_fingerprint']} completion=present")
    if args.report is not None:
        print(f"OPERATIONAL_SEED_REPORT={args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
