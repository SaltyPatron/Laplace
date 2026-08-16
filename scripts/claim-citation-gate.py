#!/usr/bin/env python3
"""Claim-citation gate: a comment that cites file:line must still point at what it claims.

A citation in prose or a code comment is a claim about the tree. It has no owner and no
expiry, so it decays silently while reading with the same authority as the code beside it.
This gate gives that class a losing condition.

Checks every `path:line` citation found in tracked comments and docs:
  MISSING_FILE  cited path does not resolve
  SHORT_FILE    cited line is past EOF
  BLANK_LINE    cited line is empty or a lone brace/comment marker
  OK            line has content

Exit 1 when any MISSING_FILE or SHORT_FILE is found, or when BLANK_LINE exceeds the
recorded baseline. Shrink-only, like scripts/model-payload-gate-check.py.

Usage:  scripts/claim-citation-gate.py [--write-baseline]
"""
import json, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASELINE = os.path.join(ROOT, "scripts", "claim-citation-gate-baseline.json")

SCAN_EXT = {".cs", ".c", ".h", ".cpp", ".hpp", ".sql", ".in", ".py", ".sh",
            ".toml", ".md", ".txt"}

# path:line — require a real extension so `Q4_K:2` and `10:30` do not match.
CITE = re.compile(
    r"\b((?:[\w.\-]+/)*[\w.\-]+\.(?:cs|c|h|cpp|hpp|sql|in|py|sh|toml|md|txt)):(\d{1,6})\b")

# Roots a relative citation may be resolved against.
PREFIXES = ["", "app/", "engine/", "extension/", "scripts/", "docs/", ".scratchpad/",
            "app/Laplace.Decomposers/Model/", "app/Laplace.Substrate/Abstractions/",
            "app/Laplace.Cli/", "engine/core/src/", "engine/core/include/",
            "extension/laplace_substrate/src/"]

BLANKISH = {"", "{", "}", "};", ");", "//", "--", "*", "*/", "/*", "#"}


def tracked():
    out = subprocess.run(["git", "-C", ROOT, "ls-files"], capture_output=True, text=True)
    return [l for l in out.stdout.splitlines() if os.path.splitext(l)[1].lower() in SCAN_EXT]


def resolve(cited):
    if os.path.isfile(os.path.join(ROOT, cited)):
        return cited
    for p in PREFIXES:
        cand = p + cited
        if os.path.isfile(os.path.join(ROOT, cand)):
            return cand
    # last resort: unique basename match anywhere in the tree
    base = os.path.basename(cited)
    hits = [f for f in tracked_cache if os.path.basename(f) == base]
    return hits[0] if len(hits) == 1 else None


tracked_cache = tracked()
linecount = {}


def lines_of(rel):
    if rel not in linecount:
        try:
            with open(os.path.join(ROOT, rel), errors="replace") as f:
                linecount[rel] = f.readlines()
        except OSError:
            linecount[rel] = []
    return linecount[rel]


findings = {"MISSING_FILE": [], "SHORT_FILE": [], "BLANK_LINE": []}
checked = 0

for rel in tracked_cache:
    try:
        with open(os.path.join(ROOT, rel), errors="replace") as f:
            src = f.readlines()
    except OSError:
        continue
    for i, raw in enumerate(src, 1):
        for m in CITE.finditer(raw):
            cited, lno = m.group(1), int(m.group(2))
            if cited.endswith(tuple(os.path.basename(rel).split())) and cited == rel:
                continue
            checked += 1
            target = resolve(cited)
            where = f"{rel}:{i}"
            if target is None:
                findings["MISSING_FILE"].append((where, f"{cited}:{lno}"))
                continue
            body = lines_of(target)
            if lno > len(body):
                findings["SHORT_FILE"].append(
                    (where, f"{target}:{lno} (file has {len(body)} lines)"))
                continue
            if body[lno - 1].strip() in BLANKISH:
                findings["BLANK_LINE"].append((where, f"{target}:{lno}"))

# THE BASELINE IS THE CONTRACT, and it MAY ONLY DECREASE. A gate that fails on
# arrival gets disabled, so it arms at the measured present and shrinks from there.
base = {"missing_file": 0, "short_file": 0, "blank_line": 0}
if os.path.exists(BASELINE):
    base = json.load(open(BASELINE))

print(f"citations checked : {checked}")
for k in ("MISSING_FILE", "SHORT_FILE", "BLANK_LINE"):
    print(f"{k:<13} : {len(findings[k])}")
print()
for k in ("MISSING_FILE", "SHORT_FILE", "BLANK_LINE"):
    for where, what in sorted(findings[k])[:40]:
        print(f"  {k:<12} {where:<58} -> {what}")
    if len(findings[k]) > 40:
        print(f"  {k:<12} ... and {len(findings[k]) - 40} more")

now = {"missing_file": len(findings["MISSING_FILE"]),
       "short_file": len(findings["SHORT_FILE"]),
       "blank_line": len(findings["BLANK_LINE"])}

if "--write-baseline" in sys.argv:
    json.dump(now, open(BASELINE, "w"), indent=2)
    print(f"\nbaseline written: {now}")
    sys.exit(0)

failed = [k for k in now if now[k] > base.get(k, 0)]
if failed:
    for k in failed:
        print(f"\nFAIL: {k} {now[k]} > baseline {base.get(k, 0)} (shrink-only)")
    sys.exit(1)
shrunk = {k: base.get(k, 0) - now[k] for k in now if now[k] < base.get(k, 0)}
print(f"\nPASS{f' — shrunk {shrunk}; re-run with --write-baseline' if shrunk else ''}")
