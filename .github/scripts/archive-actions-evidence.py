#!/usr/bin/env python3
"""Archive evidence-bearing stale Actions workflows, then remove their old runs.

This is intentionally one-way and evidence-first:
  1. Discover workflow identities whose YAML path no longer exists.
  2. Preserve every run's API metadata.
  3. For runs that still own non-expired artifacts, preserve those artifact ZIPs
     and the workflow-run logs ZIP.
  4. Upload stable per-workflow archive assets to one GitHub Release.
  5. Upload a per-workflow archive manifest LAST.
  6. Only after that marker exists, delete the old workflow's runs.

A rerun is safe: an existing per-workflow archive manifest is treated as a
completed archive transaction and deletion resumes without re-downloading data.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
CLEANUP_PATH = HERE / "cleanup-actions-history.py"
SPEC = importlib.util.spec_from_file_location("cleanup_actions_history", CLEANUP_PATH)
CLEANUP = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(CLEANUP)

SCHEMA = 1
DEFAULT_RELEASE_TAG = "actions-evidence-archive-2026-09-18"
DEFAULT_CHUNK_BYTES = 800_000_000
MAX_RELEASE_ASSET_BYTES = 1_850_000_000
PART_BYTES = 1_500_000_000


def safe_name(value: str, limit: int = 80) -> str:
    value = re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-._")
    return (value or "artifact")[:limit]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def gh(args: list[str], *, check: bool = True, capture: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["gh", *args],
        check=check,
        text=True,
        capture_output=capture,
        timeout=300,
    )


def ensure_release(repo: str, tag: str, target_sha: str) -> None:
    existing = gh(["release", "view", tag, "--repo", repo, "--json", "tagName"], check=False)
    if existing.returncode == 0:
        return
    notes = (
        "Historical GitHub Actions evidence moved out of obsolete workflow-run identities.\n\n"
        "Each workflow is archived transactionally: run metadata for every run, plus "
        "artifact ZIPs and logs ZIPs for runs that still had retrievable artifacts. "
        "A workflow's old runs are deleted only after its \`workflow-<id>-archive.json\` "
        "marker asset is uploaded. Split \`.part-*\` assets are concatenated in lexical "
        "order before opening the resulting tar archive."
    )
    gh(
        [
            "release",
            "create",
            tag,
            "--repo",
            repo,
            "--target",
            target_sha,
            "--title",
            "Historical Actions evidence archive — 2026-09-18",
            "--notes",
            notes,
        ]
    )


def release_asset_names(repo: str, tag: str) -> set[str]:
    result = gh(
        [
            "release",
            "view",
            tag,
            "--repo",
            repo,
            "--json",
            "assets",
            "--jq",
            ".assets[].name",
        ],
        check=False,
    )
    if result.returncode != 0:
        return set()
    return {line.strip() for line in result.stdout.splitlines() if line.strip()}


def upload_assets(repo: str, tag: str, paths: list[Path]) -> None:
    if not paths:
        return
    gh(
        [
            "release",
            "upload",
            tag,
            *[str(path) for path in paths],
            "--repo",
            repo,
            "--clobber",
        ],
        capture=True,
    )


def download_api_zip(repo: str, token: str, endpoint: str, destination: Path) -> tuple[bool, str]:
    url = f"https://api.github.com/repos/{repo}/{endpoint.lstrip('/')}"
    destination.parent.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [
            "curl",
            "--fail",
            "--location",
            "--silent",
            "--show-error",
            "--retry",
            "3",
            "--retry-delay",
            "2",
            "-H",
            "Accept: application/vnd.github+json",
            "-H",
            f"Authorization: Bearer {token}",
            "-H",
            f"X-GitHub-Api-Version: {CLEANUP.API_VERSION}",
            "--output",
            str(destination),
            url,
        ],
        text=True,
        capture_output=True,
        timeout=900,
    )
    if result.returncode != 0:
        destination.unlink(missing_ok=True)
        return False, (result.stderr or result.stdout).strip()
    if not destination.is_file() or destination.stat().st_size == 0:
        destination.unlink(missing_ok=True)
        return False, "download produced no bytes"
    return True, ""


def artifact_map(artifacts: list[dict]) -> dict[int, list[dict]]:
    by_run: dict[int, list[dict]] = {}
    for artifact in artifacts:
        run = artifact.get("workflow_run") or {}
        run_id = run.get("id")
        if run_id is None:
            continue
        by_run.setdefault(int(run_id), []).append(artifact)
    for values in by_run.values():
        values.sort(key=lambda item: int(item["id"]))
    return by_run


def retrievable_artifacts(artifacts: list[dict]) -> list[dict]:
    return [artifact for artifact in artifacts if not artifact.get("expired", False)]


def compact_run(run: dict) -> dict:
    keys = (
        "id",
        "name",
        "display_title",
        "event",
        "status",
        "conclusion",
        "workflow_id",
        "path",
        "head_branch",
        "head_sha",
        "run_number",
        "run_attempt",
        "created_at",
        "updated_at",
        "run_started_at",
        "html_url",
        "artifacts_url",
        "jobs_url",
        "logs_url",
    )
    return {key: run.get(key) for key in keys}


def compact_artifact(artifact: dict) -> dict:
    return {
        "id": artifact.get("id"),
        "name": artifact.get("name"),
        "size_in_bytes": artifact.get("size_in_bytes"),
        "expired": artifact.get("expired"),
        "created_at": artifact.get("created_at"),
        "updated_at": artifact.get("updated_at"),
        "expires_at": artifact.get("expires_at"),
        "archive_download_url": artifact.get("archive_download_url"),
        "workflow_run_id": (artifact.get("workflow_run") or {}).get("id"),
    }


def plan_chunks(run_ids: list[int], artifacts_by_run: dict[int, list[dict]], target_bytes: int) -> list[list[int]]:
    chunks: list[list[int]] = []
    current: list[int] = []
    current_bytes = 0

    for run_id in sorted(run_ids):
        estimated = sum(
            int(item.get("size_in_bytes") or 0)
            for item in retrievable_artifacts(artifacts_by_run.get(run_id, []))
        )
        if current and current_bytes + estimated > target_bytes:
            chunks.append(current)
            current = []
            current_bytes = 0
        current.append(run_id)
        current_bytes += estimated

    if current:
        chunks.append(current)
    return chunks


def split_asset(path: Path) -> list[Path]:
    if path.stat().st_size <= MAX_RELEASE_ASSET_BYTES:
        return [path]

    parts: list[Path] = []
    with path.open("rb") as source:
        index = 1
        while True:
            block = source.read(PART_BYTES)
            if not block:
                break
            part = path.with_name(f"{path.name}.part-{index:03d}")
            part.write_bytes(block)
            parts.append(part)
            index += 1
    path.unlink()
    return parts


def create_chunk_archive(
    *,
    repo: str,
    token: str,
    workflow_id: int,
    chunk_index: int,
    run_ids: list[int],
    runs_by_id: dict[int, dict],
    artifacts_by_run: dict[int, list[dict]],
    directory: Path,
) -> tuple[list[Path], dict]:
    payload_root = directory / f"payload-{chunk_index:03d}"
    payload_root.mkdir(parents=True, exist_ok=True)
    chunk_entries: list[dict] = []

    for run_id in run_ids:
        run_dir = payload_root / f"run-{run_id}"
        run_dir.mkdir(parents=True, exist_ok=True)
        run = runs_by_id[run_id]
        artifacts = retrievable_artifacts(artifacts_by_run.get(run_id, []))
        (run_dir / "run.json").write_text(
            json.dumps(compact_run(run), indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        (run_dir / "artifacts.json").write_text(
            json.dumps([compact_artifact(item) for item in artifacts], indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )

        log_path = run_dir / "logs.zip"
        logs_ok, logs_error = download_api_zip(
            repo,
            token,
            f"actions/runs/{run_id}/logs",
            log_path,
        )

        archived_artifacts: list[dict] = []
        for artifact in artifacts:
            artifact_id = int(artifact["id"])
            destination = run_dir / (
                f"artifact-{artifact_id}-{safe_name(str(artifact.get('name') or 'artifact'))}.zip"
            )
            ok, error = download_api_zip(
                repo,
                token,
                f"actions/artifacts/{artifact_id}/zip",
                destination,
            )
            if not ok:
                raise RuntimeError(
                    f"artifact {artifact_id} for run {run_id} could not be archived: {error}"
                )
            archived_artifacts.append(
                {
                    "artifact_id": artifact_id,
                    "name": artifact.get("name"),
                    "source_size_in_bytes": artifact.get("size_in_bytes"),
                    "archive_file": destination.relative_to(payload_root).as_posix(),
                    "archive_size_in_bytes": destination.stat().st_size,
                    "sha256": sha256_file(destination),
                }
            )

        chunk_entries.append(
            {
                "run_id": run_id,
                "logs_archived": logs_ok,
                "logs_error": logs_error if not logs_ok else "",
                "logs_file": log_path.relative_to(payload_root).as_posix() if logs_ok else "",
                "logs_sha256": sha256_file(log_path) if logs_ok else "",
                "artifacts": archived_artifacts,
            }
        )

    chunk_manifest = {
        "schema": SCHEMA,
        "workflow_id": workflow_id,
        "chunk_index": chunk_index,
        "run_ids": run_ids,
        "runs": chunk_entries,
    }
    (payload_root / "chunk-manifest.json").write_text(
        json.dumps(chunk_manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    archive = directory / f"workflow-{workflow_id}-evidence-{chunk_index:03d}.tar"
    with tarfile.open(archive, "w") as output:
        output.add(payload_root, arcname=payload_root.name)
    shutil.rmtree(payload_root)

    parts = split_asset(archive)
    assets = [
        {
            "name": part.name,
            "size_in_bytes": part.stat().st_size,
            "sha256": sha256_file(part),
        }
        for part in parts
    ]
    return parts, {
        "chunk_index": chunk_index,
        "run_ids": run_ids,
        "assets": assets,
    }


def workflow_metadata(entry: dict, artifacts_by_run: dict[int, list[dict]]) -> dict:
    runs = sorted(entry["runs"], key=lambda item: int(item["id"]))
    artifacts = [
        compact_artifact(artifact)
        for run in runs
        for artifact in artifacts_by_run.get(int(run["id"]), [])
    ]
    retrievable = [item for item in artifacts if not item.get("expired", False)]
    return {
        "schema": SCHEMA,
        "workflow_id": entry["workflow_id"],
        "path": entry["path"],
        "names": sorted(entry["names"]),
        "run_count": len(runs),
        "runs": [compact_run(run) for run in runs],
        "artifact_count": len(artifacts),
        "retrievable_artifact_count": len(retrievable),
        "artifact_source_bytes": sum(int(item.get("size_in_bytes") or 0) for item in retrievable),
        "artifacts": artifacts,
    }


def report_summary(report: dict) -> str:
    lines = [
        "## Historical Actions evidence archive",
        "",
        f"- Release: \`{report['release_tag']}\`",
        f"- Stale evidence-bearing identities discovered: {report['discovered_workflow_count']}",
        f"- Workflow identities archived this run: {report['archived_workflow_count']}",
        f"- Workflow identities already archived: {report['already_archived_workflow_count']}",
        f"- Old workflow identities fully deleted: {report['deleted_workflow_count']}",
        f"- Old runs deleted: {report['deleted_run_count']}",
        f"- Workflows left unresolved: {len(report['failed'])}",
        "",
    ]
    if report["failed"]:
        lines += ["### Unresolved", ""]
        for item in report["failed"]:
            lines.append(f"- \`{item['path']}\`: {item['error']}")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--token", default=os.environ.get("GITHUB_TOKEN", ""))
    parser.add_argument("--api", default=os.environ.get("GITHUB_API_URL", "https://api.github.com"))
    parser.add_argument("--target-sha", default=os.environ.get("GITHUB_SHA", ""))
    parser.add_argument("--release-tag", default=DEFAULT_RELEASE_TAG)
    parser.add_argument("--chunk-bytes", type=int, default=DEFAULT_CHUNK_BYTES)
    parser.add_argument("--min-rate-remaining", type=int, default=150)
    parser.add_argument("--delete-delay", type=float, default=0.05)
    parser.add_argument("--report", required=True)
    parser.add_argument("--summary")
    args = parser.parse_args()

    if not args.repo:
        parser.error("--repo or GITHUB_REPOSITORY is required")
    if not args.token:
        parser.error("--token or GITHUB_TOKEN is required")
    if not args.target_sha:
        parser.error("--target-sha or GITHUB_SHA is required")

    os.environ.setdefault("GH_TOKEN", args.token)
    root = Path(args.root).resolve()
    client = CLEANUP.GitHub(args.repo, args.token, args.api)
    active = CLEANUP.current_workflow_paths(root)

    runs = list(client.pages(f"/repos/{args.repo}/actions/runs?", "workflow_runs"))
    artifacts = list(client.pages(f"/repos/{args.repo}/actions/artifacts?", "artifacts"))
    by_run = artifact_map(artifacts)
    run_ids_with_artifacts = {
        run_id
        for run_id, values in by_run.items()
        if retrievable_artifacts(values)
    }

    grouped = CLEANUP.group_stale_runs(runs, active)
    evidence_groups = []
    for entry in grouped.values():
        ids = {int(run["id"]) for run in entry["runs"]}
        if ids & run_ids_with_artifacts:
            if entry["has_nonterminal"]:
                continue
            evidence_groups.append(entry)
    evidence_groups.sort(key=lambda item: (item["path"], item["workflow_id"]))

    ensure_release(args.repo, args.release_tag, args.target_sha)
    existing_assets = release_asset_names(args.repo, args.release_tag)

    report = {
        "schema": SCHEMA,
        "repository": args.repo,
        "release_tag": args.release_tag,
        "discovered_workflow_count": len(evidence_groups),
        "archived_workflow_count": 0,
        "already_archived_workflow_count": 0,
        "deleted_workflow_count": 0,
        "deleted_run_count": 0,
        "archived": [],
        "failed": [],
        "rate_remaining": client.remaining,
    }

    report_path = Path(args.report)
    report_path.parent.mkdir(parents=True, exist_ok=True)

    def persist_report() -> None:
        report["rate_remaining"] = client.remaining
        report_path.write_text(
            json.dumps(report, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )

    persist_report()

    for entry in evidence_groups:
        workflow_id = int(entry["workflow_id"])
        marker_name = f"workflow-{workflow_id}-archive.json"
        metadata_name = f"workflow-{workflow_id}-metadata.json"
        marker_present = marker_name in existing_assets
        workflow_record = {
            "workflow_id": workflow_id,
            "path": entry["path"],
            "names": sorted(entry["names"]),
            "run_count": len(entry["runs"]),
            "marker_asset": marker_name,
            "metadata_asset": metadata_name,
            "archive_assets": [],
            "already_archived": marker_present,
            "deleted_run_ids": [],
        }

        try:
            if not marker_present:
                with tempfile.TemporaryDirectory(
                    prefix=f"actions-evidence-{workflow_id}-"
                ) as temporary:
                    directory = Path(temporary)
                    metadata = workflow_metadata(entry, by_run)
                    metadata_path = directory / metadata_name
                    metadata_path.write_text(
                        json.dumps(metadata, indent=2, sort_keys=True) + "\n",
                        encoding="utf-8",
                    )
                    upload_assets(args.repo, args.release_tag, [metadata_path])

                    artifact_run_ids = [
                        int(run["id"])
                        for run in entry["runs"]
                        if int(run["id"]) in run_ids_with_artifacts
                    ]
                    chunks = plan_chunks(artifact_run_ids, by_run, args.chunk_bytes)
                    chunk_records: list[dict] = []
                    for index, chunk in enumerate(chunks, start=1):
                        paths, chunk_record = create_chunk_archive(
                            repo=args.repo,
                            token=args.token,
                            workflow_id=workflow_id,
                            chunk_index=index,
                            run_ids=chunk,
                            runs_by_id={int(run["id"]): run for run in entry["runs"]},
                            artifacts_by_run=by_run,
                            directory=directory,
                        )
                        upload_assets(args.repo, args.release_tag, paths)
                        chunk_records.append(chunk_record)
                        workflow_record["archive_assets"].extend(
                            asset["name"] for asset in chunk_record["assets"]
                        )
                        for path in paths:
                            path.unlink(missing_ok=True)

                    marker = {
                        "schema": SCHEMA,
                        "archived_at": datetime.now(timezone.utc).isoformat(),
                        "repository": args.repo,
                        "release_tag": args.release_tag,
                        "workflow_id": workflow_id,
                        "path": entry["path"],
                        "names": sorted(entry["names"]),
                        "run_count": len(entry["runs"]),
                        "metadata_asset": metadata_name,
                        "chunks": chunk_records,
                        "reconstruction": (
                            "For any chunk split into .part-NNN assets, concatenate those "
                            "parts in lexical order to reconstruct the .tar before extraction."
                        ),
                    }
                    marker_path = directory / marker_name
                    marker_path.write_text(
                        json.dumps(marker, indent=2, sort_keys=True) + "\n",
                        encoding="utf-8",
                    )
                    upload_assets(args.repo, args.release_tag, [marker_path])

                existing_assets = release_asset_names(args.repo, args.release_tag)
                if marker_name not in existing_assets:
                    raise RuntimeError("archive marker was not visible after upload")
                report["archived_workflow_count"] += 1
            else:
                report["already_archived_workflow_count"] += 1

            # The archive marker is the commit point. Only now may old runs disappear.
            for run in sorted(entry["runs"], key=lambda item: int(item["id"])):
                run_id = int(run["id"])
                if (
                    client.remaining is not None
                    and client.remaining < args.min_rate_remaining
                ):
                    raise RuntimeError(
                        f"rate-limit safety stop at {client.remaining} requests remaining"
                    )
                client.delete_run(run_id)
                workflow_record["deleted_run_ids"].append(run_id)
                report["deleted_run_count"] += 1
                if args.delete_delay:
                    time.sleep(args.delete_delay)

            report["deleted_workflow_count"] += 1
            report["archived"].append(workflow_record)
            persist_report()
        except Exception as error:
            workflow_record["error"] = str(error)
            report["failed"].append(workflow_record)
            persist_report()
            # Preserve the workflow's remaining runs on any archive/delete failure.
            continue

    index_path = report_path.with_name("actions-evidence-archive-index.json")
    index_path.write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    try:
        upload_assets(args.repo, args.release_tag, [index_path])
    except Exception as error:
        report["failed"].append(
            {
                "workflow_id": 0,
                "path": "<release-index>",
                "error": str(error),
            }
        )
        persist_report()

    summary = report_summary(report)
    print(summary)
    if args.summary:
        with Path(args.summary).open("a", encoding="utf-8") as stream:
            stream.write(summary + "\n")
    persist_report()
    return 1 if report["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
