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

NOT WIRED INTO CI, and must not be until MISSING_FILE is clean. Three false-positive
classes are known and unhandled:

  1. generated artifacts — highway_manifest.h, relation_law.c and friends are codegen
     outputs and gitignored, so a correct citation to them resolves to nothing;
  2. ambiguous basenames — resolve() gives up when two files share a name
     (NativeInterop.cs exists under both Core/Dynamics and Core/Synthesis);
  3. self-reference — example citations inside this file's own prose are matched.

Arming it before those are handled turns a correct commit red for being correct,
which is how a gate gets disabled instead of fixed.

Shrink-only, like scripts/model-payload-gate-check.py.

Usage:  scripts/claim-citation-gate.py [--write-baseline]
"""
import json, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASELINE = os.path.join(ROOT, "scripts", "claim-citation-gate-baseline.json")

SCAN_EXT = {".cs", ".c", ".h", ".cpp", ".hpp", ".sql", ".in", ".py", ".sh",
            ".toml", ".md", ".txt"}

# path:line — require a real extension so `Q4_K:2` and `10:30` do not match.
# `.sql.in` must be matched whole: a bare `\.in` alternative captures only the
# `sql.in:27` tail of `foo.sql.in:27` and reports a phantom missing file.
CITE = re.compile(
    r"(?<![\w./-])((?:[\w.\-]+/)*[\w\-]+(?:\.\w+)*?"
    r"\.(?:sql\.in|cs|cpp|hpp|sql|py|sh|toml|md|txt|yml|c|h|in)):(\d{1,6})\b")

# Citations into source that is not this repository. A reference to PostgreSQL's
# own tree is a legitimate claim; it is simply not resolvable here, and counting
# it as decay would fail the build for being correct.
EXTERNAL = {
    "clauses.c",            # postgres/src/backend/optimizer/plan
    "tests/CMakeLists.txt",
}

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
            if cited in EXTERNAL or os.path.basename(cited) in EXTERNAL:
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
