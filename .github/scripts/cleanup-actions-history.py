#!/usr/bin/env python3
"""Conservatively remove obsolete GitHub Actions workflow identities.

Only completed runs belonging to workflow paths no longer present in the repository
are eligible. If ANY retained artifact belongs to a stale workflow identity, every
run for that workflow id is preserved. Eligible workflow identities are removed
whole (all their runs), smallest first, so cleanup actually reduces sidebar clutter.
"""
from __future__ import annotations

import argparse
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import defaultdict
from pathlib import Path


API_VERSION = "2026-03-10"


class GitHub:
    def __init__(self, repo: str, token: str, api: str = "https://api.github.com"):
        self.repo = repo
        self.token = token
        self.api = api.rstrip("/")
        self.remaining: int | None = None

    def request(self, method: str, path: str, *, retries: int = 4):
        url = self.api + path
        request = urllib.request.Request(
            url,
            method=method,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {self.token}",
                "X-GitHub-Api-Version": API_VERSION,
                "User-Agent": "laplace-actions-history-cleanup",
            },
        )
        for attempt in range(retries):
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    self._remember_limit(response.headers)
                    body = response.read()
                    return response.status, (json.loads(body) if body else None)
            except urllib.error.HTTPError as error:
                self._remember_limit(error.headers)
                if error.code in (403, 429) and attempt + 1 < retries:
                    delay = int(error.headers.get("Retry-After") or "0")
                    if delay <= 0:
                        delay = min(60, 5 * (attempt + 1))
                    time.sleep(delay)
                    continue
                detail = error.read().decode("utf-8", "replace")
                raise RuntimeError(f"{method} {url} -> {error.code}: {detail}") from error
        raise AssertionError("unreachable")

    def _remember_limit(self, headers) -> None:
        value = headers.get("X-RateLimit-Remaining")
        if value and value.isdigit():
            self.remaining = int(value)

    def pages(self, path: str, key: str):
        page = 1
        joiner = "&" if "?" in path else "?"
        while True:
            _status, payload = self.request(
                "GET", f"{path}{joiner}per_page=100&page={page}"
            )
            values = payload.get(key, [])
            if not values:
                break
            yield from values
            if len(values) < 100:
                break
            page += 1

    def delete_run(self, run_id: int) -> None:
        self.request(
            "DELETE",
            f"/repos/{self.repo}/actions/runs/{run_id}",
        )


def current_workflow_paths(root: Path) -> set[str]:
    directory = root / ".github" / "workflows"
    return {
        path.relative_to(root).as_posix()
        for path in directory.glob("*.yml")
        if path.is_file()
    } | {
        path.relative_to(root).as_posix()
        for path in directory.glob("*.yaml")
        if path.is_file()
    }


def group_stale_runs(runs: list[dict], current_paths: set[str]) -> dict[int, dict]:
    grouped: dict[int, dict] = {}
    for run in runs:
        path = str(run.get("path") or "")
        if path in current_paths:
            continue
        workflow_id = int(run["workflow_id"])
        entry = grouped.setdefault(
            workflow_id,
            {
                "workflow_id": workflow_id,
                "path": path,
                "names": set(),
                "runs": [],
                "has_nonterminal": False,
            },
        )
        entry["names"].add(str(run.get("name") or ""))
        entry["runs"].append(run)
        if run.get("status") != "completed":
            entry["has_nonterminal"] = True
    return grouped


def classify_groups(
    grouped: dict[int, dict], artifact_run_ids: set[int], max_deletes: int
) -> tuple[list[dict], list[dict], list[dict]]:
    preserved_artifacts: list[dict] = []
    preserved_nonterminal: list[dict] = []
    eligible: list[dict] = []

    for entry in grouped.values():
        run_ids = {int(run["id"]) for run in entry["runs"]}
        compact = {
            "workflow_id": entry["workflow_id"],
            "path": entry["path"],
            "names": sorted(entry["names"]),
            "run_count": len(run_ids),
            "run_ids": sorted(run_ids),
        }
        if run_ids & artifact_run_ids:
            compact["artifact_run_ids"] = sorted(run_ids & artifact_run_ids)
            preserved_artifacts.append(compact)
        elif entry["has_nonterminal"]:
            preserved_nonterminal.append(compact)
        else:
            eligible.append(compact)

    # Remove the most sidebar identities per API request budget.
    eligible.sort(key=lambda item: (item["run_count"], item["path"], item["workflow_id"]))
    selected: list[dict] = []
    budget = max_deletes
    for entry in eligible:
        if entry["run_count"] > budget:
            continue
        selected.append(entry)
        budget -= entry["run_count"]
    return selected, preserved_artifacts, preserved_nonterminal


def report_summary(report: dict) -> str:
    lines = [
        "## Actions history cleanup",
        "",
        f"- Repository runs snapshotted: {report['run_count']}",
        f"- Current workflow paths: {report['current_workflow_count']}",
        f"- Stale workflow identities found: {report['stale_workflow_count']}",
        f"- Stale identities deleted: {report['deleted_workflow_count']}",
        f"- Runs deleted: {report['deleted_run_count']}",
        f"- Evidence-bearing stale identities preserved: {len(report['preserved_artifacts'])}",
        f"- Nonterminal stale identities preserved: {len(report['preserved_nonterminal'])}",
        f"- Eligible stale identities left by delete budget/failure: {len(report['remaining_eligible'])}",
        "",
    ]
    if report["preserved_artifacts"]:
        lines += ["### Preserved because they contain artifacts", ""]
        for item in report["preserved_artifacts"][:30]:
            lines.append(
                f"- `{item['path']}` — {item['run_count']} run(s), "
                f"artifact run(s): {', '.join(map(str, item['artifact_run_ids'][:8]))}"
            )
        lines.append("")
    if report["remaining_eligible"]:
        lines += ["### Still eligible for a later cleanup batch", ""]
        for item in report["remaining_eligible"][:30]:
            lines.append(f"- `{item['path']}` — {item['run_count']} run(s)")
        lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--token", default=os.environ.get("GITHUB_TOKEN", ""))
    parser.add_argument("--api", default=os.environ.get("GITHUB_API_URL", "https://api.github.com"))
    parser.add_argument("--delete", action="store_true")
    parser.add_argument("--max-deletes", type=int, default=3000)
    parser.add_argument("--min-rate-remaining", type=int, default=500)
    parser.add_argument("--delete-delay", type=float, default=0.12)
    parser.add_argument("--report", required=True)
    parser.add_argument("--summary")
    args = parser.parse_args()

    if not args.repo:
        parser.error("--repo or GITHUB_REPOSITORY is required")
    if not args.token:
        parser.error("--token or GITHUB_TOKEN is required")
    if args.max_deletes < 0:
        parser.error("--max-deletes must be nonnegative")

    root = Path(args.root).resolve()
    client = GitHub(args.repo, args.token, args.api)
    active = current_workflow_paths(root)

    runs = list(
        client.pages(f"/repos/{args.repo}/actions/runs?", "workflow_runs")
    )
    artifacts = list(
        client.pages(f"/repos/{args.repo}/actions/artifacts?", "artifacts")
    )
    artifact_run_ids = {
        int(item["workflow_run"]["id"])
        for item in artifacts
        if item.get("workflow_run") and item["workflow_run"].get("id")
    }

    grouped = group_stale_runs(runs, active)
    selected, preserved_artifacts, preserved_nonterminal = classify_groups(
        grouped, artifact_run_ids, args.max_deletes
    )
    selected_ids = {entry["workflow_id"] for entry in selected}
    all_eligible, _, _ = classify_groups(grouped, artifact_run_ids, 10**9)
    all_eligible_by_id = {entry["workflow_id"]: entry for entry in all_eligible}

    deleted_workflows: list[dict] = []
    failed: list[dict] = []
    deleted_run_count = 0

    if args.delete:
        for entry in selected:
            deleted_ids: list[int] = []
            try:
                for run_id in entry["run_ids"]:
                    if (
                        client.remaining is not None
                        and client.remaining < args.min_rate_remaining
                    ):
                        raise RuntimeError(
                            f"rate-limit safety stop at {client.remaining} requests remaining"
                        )
                    client.delete_run(run_id)
                    deleted_ids.append(run_id)
                    deleted_run_count += 1
                    if args.delete_delay:
                        time.sleep(args.delete_delay)
            except Exception as error:
                failed.append(
                    {
                        **entry,
                        "deleted_run_ids": deleted_ids,
                        "error": str(error),
                    }
                )
                # Partial deletion does not count as removing the identity.
                continue
            deleted_workflows.append(entry)

    deleted_workflow_ids = {entry["workflow_id"] for entry in deleted_workflows}
    failed_workflow_ids = {entry["workflow_id"] for entry in failed}
    remaining_eligible = [
        entry
        for workflow_id, entry in sorted(
            all_eligible_by_id.items(), key=lambda pair: (pair[1]["run_count"], pair[1]["path"])
        )
        if workflow_id not in deleted_workflow_ids
    ]

    report = {
        "schema": 1,
        "repository": args.repo,
        "delete_requested": args.delete,
        "run_count": len(runs),
        "artifact_count": len(artifacts),
        "artifact_run_count": len(artifact_run_ids),
        "current_workflow_count": len(active),
        "current_workflows": sorted(active),
        "stale_workflow_count": len(grouped),
        "selected_workflow_count": len(selected),
        "selected_run_count": sum(item["run_count"] for item in selected),
        "deleted_workflow_count": len(deleted_workflows),
        "deleted_run_count": deleted_run_count,
        "deleted_workflows": deleted_workflows,
        "failed": failed,
        "preserved_artifacts": sorted(
            preserved_artifacts, key=lambda item: (item["path"], item["workflow_id"])
        ),
        "preserved_nonterminal": sorted(
            preserved_nonterminal, key=lambda item: (item["path"], item["workflow_id"])
        ),
        "remaining_eligible": remaining_eligible,
        "rate_remaining": client.remaining,
    }

    report_path = Path(args.report)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    summary = report_summary(report)
    print(summary)
    if args.summary:
        with Path(args.summary).open("a", encoding="utf-8") as stream:
            stream.write(summary + "\n")

    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
