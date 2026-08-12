#!/usr/bin/env python3
"""Reject authority/status drift in Laplace's agent-facing documentation."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

NORMATIVE = (
    "05_Substrate_Invariants.txt",
    "06_Engineering_Ruleset.txt",
    "08_Record_vs_Calculate_Spec.txt",
    "09_Substrate_LM_Synthesis.txt",
    "11_Chess_Provenance_Consensus_Spec.txt",
    "12_Mold_A_Model_Synthesis_Map.txt",
    "33_Perfcache_Blob_Law.md",
    "34_Conversational_Provenance.md",
    "36_Laplace_Forward_Pass.md",
    "37_Substrate_Operation_ISA.md",
)

STATUS_PATTERNS = {
    "dated observation": re.compile(r"\b20\d\d-\d\d-\d\d\b"),
    "GitHub issue/PR status": re.compile(r"(?:GitHub|GH|issue|PR)\s*#?\d+", re.I),
    "mutable status token": re.compile(
        r"\b(?:still open|current status|status update|status note|landed|merged|"
        r"closed as of|not started|in progress|implementation status)\b",
        re.I,
    ),
    "checkbox tracker": re.compile(r"^\s*[-*]\s+\[[ xX]\]", re.M),
}

DESIGN_DRIFT_PATTERNS = {
    "dated design observation": re.compile(r"\b20\d\d-\d\d-\d\d\b"),
    "status section": re.compile(
        r"^#{1,6}\s+.*\b(?:status|known gaps|todo|what landed)\b", re.I | re.M
    ),
    "mutable state badge": re.compile(r"\*\*(?:OPEN|CLOSED|PARTIAL)\*\*", re.I),
    "checkbox tracker": re.compile(r"^\s*[-*]\s+\[[ xX]\]", re.M),
}

DESIGN_ROOTS = (
    ROOT / "docs" / "ARCHITECTURE.md",
    ROOT / "docs" / "INVENTION.md",
    ROOT / "docs" / "INVENTIONS.md",
    ROOT / "docs" / "INDEX.md",
    ROOT / "docs" / "invention",
    ROOT / "docs" / "guides",
    ROOT / "docs" / "plan" / "README.md",
    ROOT / "docs" / "plan" / "WORKSTREAMS.md",
    ROOT / "docs" / "plan" / "REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md",
    # Classified 2026-08-12. The model ingestion/export lane is active work —
    # GPU evaluation, fp64->fp32, safetensors decomposition. 09d2e64f DELETED
    # this artifact to make the gate pass, which is backwards: the gate exists
    # to stop status snapshots and archived plans accumulating in docs/plan, not
    # to reject genuine active design. When the plan legitimately changes, widen
    # the classification; do not drop the document.
    ROOT / "docs" / "plan" / "MODEL_INGESTION_DESIGN.md",
)

INSTRUCTION_ROOTS = (
    ROOT / "CLAUDE.md",
    ROOT / "AGENTS.md",
    ROOT / ".github" / "instructions",
    ROOT / ".github" / "agents",
    ROOT / ".github" / "prompts",
    ROOT / ".cursor" / "rules",
)

INSTRUCTION_FORBIDDEN = {
    "specific scratchpad authority": re.compile(
        r"\.scratchpad/(?!README\.md)[A-Za-z0-9_.-]+\.(?:md|txt)"
    ),
    "archived Cursor plan": re.compile(r"\.cursor/plans/"),
    "checkpoint routing": re.compile(r"CHECKPOINT_\d{4}"),
    "backlog-snapshot routing": re.compile(r"BACKLOG_KILL_LIST"),
    "threat narrative": re.compile(
        r"innocent people die|pulling triggers|setting fires|lethal harm|"
        r"sabotage-class|operator abuse",
        re.I,
    ),
}

REQUIRED = (
    ROOT / "docs" / "DOCUMENTATION_GOVERNANCE.md",
    ROOT / "docs" / "specs" / "README.md",
    ROOT / "docs" / "archive" / "README.md",
    ROOT / ".scratchpad" / "README.md",
    ROOT / "docs" / "plan" / "REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md",
)

ACTIVE_PLAN_FILES = {
    "README.md",
    "WORKSTREAMS.md",
    "REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md",
    "MODEL_INGESTION_DESIGN.md",
}

LINK_PATTERN = re.compile(r"\[[^\]]*\]\(([^)]+)\)")


def text_files(path: Path):
    if path.is_file():
        yield path
        return
    if not path.exists():
        return
    for candidate in sorted(path.rglob("*")):
        if candidate.is_file() and candidate.suffix.lower() in {
            ".md",
            ".mdc",
            ".txt",
        }:
            yield candidate


def report(errors: list[str], path: Path, rule: str, match: re.Match[str]) -> None:
    line = path.read_text(encoding="utf-8").count("\n", 0, match.start()) + 1
    errors.append(f"{path.relative_to(ROOT)}:{line}: {rule}: {match.group(0)!r}")


def resolve_repo_link(
    source: Path, target: str, root: Path = ROOT
) -> tuple[Path, bool]:
    """Resolve a relative link and report whether it remains inside root."""
    resolved_root = root.resolve(strict=False)
    resolved = (source.parent / target).resolve(strict=False)
    try:
        resolved.relative_to(resolved_root)
    except ValueError:
        return resolved, False
    return resolved, True


def main() -> int:
    errors: list[str] = []

    for path in REQUIRED:
        if not path.exists():
            errors.append(f"missing required governance artifact: {path.relative_to(ROOT)}")

    for name in NORMATIVE:
        path = ROOT / "docs" / "specs" / name
        if not path.exists():
            errors.append(f"missing normative spec: {path.relative_to(ROOT)}")
            continue
        content = path.read_text(encoding="utf-8")
        for rule, pattern in STATUS_PATTERNS.items():
            match = pattern.search(content)
            if match:
                report(errors, path, rule, match)

    for root in DESIGN_ROOTS:
        for path in text_files(root):
            content = path.read_text(encoding="utf-8")
            for rule, pattern in DESIGN_DRIFT_PATTERNS.items():
                match = pattern.search(content)
                if match:
                    report(errors, path, rule, match)

    for root in INSTRUCTION_ROOTS:
        for path in text_files(root):
            content = path.read_text(encoding="utf-8")
            for rule, pattern in INSTRUCTION_FORBIDDEN.items():
                match = pattern.search(content)
                if match:
                    report(errors, path, rule, match)

    active_plan = ROOT / "docs" / "plan"
    for pattern in ("CHECKPOINT_*", "BACKLOG_KILL_LIST_*", "ONBOARDING.md"):
        for path in active_plan.glob(pattern):
            errors.append(f"active plan contains archived/status artifact: {path.relative_to(ROOT)}")
    for path in active_plan.iterdir():
        if path.is_file() and path.name not in ACTIVE_PLAN_FILES:
            errors.append(f"unclassified active plan artifact: {path.relative_to(ROOT)}")

    cursor_plans = ROOT / ".cursor" / "plans"
    if cursor_plans.exists():
        for path in cursor_plans.rglob("*"):
            if path.is_file():
                errors.append(f"active Cursor plan must be archived: {path.relative_to(ROOT)}")

    link_roots = (
        ROOT / "README.md",
        ROOT / "CLAUDE.md",
        ROOT / "AGENTS.md",
        ROOT / "docs",
        ROOT / ".github" / "instructions",
        ROOT / ".github" / "agents",
        ROOT / ".github" / "prompts",
        ROOT / ".cursor" / "rules",
    )
    for root in link_roots:
        for path in text_files(root):
            if "docs/archive" in path.as_posix():
                continue
            content = path.read_text(encoding="utf-8")
            for match in LINK_PATTERN.finditer(content):
                target = match.group(1).strip()
                if target.startswith(("http://", "https://", "mailto:", "#")):
                    continue
                target = target.split("#", 1)[0]
                if not target:
                    continue
                resolved, inside_repo = resolve_repo_link(path, target)
                if not inside_repo:
                    report(errors, path, "relative link escapes repository", match)
                    continue
                if not resolved.exists():
                    report(errors, path, "broken relative link", match)

    if errors:
        print("documentation governance violations:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("documentation governance: ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
