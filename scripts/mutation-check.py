#!/usr/bin/env python3
"""Known-bad mutation harness: prove each guarding test FAILS against the defect it guards.

A regression test is evidence of nothing until the known-bad implementation makes it red.
Measured on this repo 2026-08-24: 911 Core+Substrate tests passed with the machine modelled
as 6 physical cores and as 12 alike, while that value feeds every ingest worker pool. And of
four tests written the same day for GH #1300, one -- IsoRetirementTests -- stayed GREEN when
the defect it was written for was restored, because it exercised the parser rather than the
rule deciding whether to call it.

Each entry names a defect, the exact source mutation that reintroduces it, and the test that
must turn red. The harness restores every file it touched, including on failure.

A mutation whose `before` text is not found is a HARD ERROR, never a skip: silently applying
nothing and observing a green test is the vacuous-gate failure this harness exists to detect.

NATIVE MUTATIONS ARE NOT SUPPORTED, and the reason is a gap worth knowing about.
/etc/ld.so.conf.d puts /opt/laplace/lib on the system loader path, so a managed test loads
the INSTALLED liblaplace_core.so, never the one in build/. Measured 2026-08-24: mutating
LAPLACE_GLICKO2_NEUTRAL_MU_FP from 1500 to 1400 and relinking the library left
NeutralMu_MatchesServerConstant green, because the test was reading a copy installed 21
minutes earlier. Every native-backed parity test -- Glicko2FoldParity, ConsensusKeysParity,
CollapseIndexParity, QkPairsThresholdParity -- can pass against a stale installed library
while the source is broken. Reaching them needs an install into a path shared with the CI
runner, which this harness will not do on its own.

  scripts/mutation-check.py             # run all
  scripts/mutation-check.py --list
  scripts/mutation-check.py -k iso      # substring filter on id
"""
import argparse, json, pathlib, shutil, subprocess, sys, tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "app"

MUTATIONS = [
  dict(
    id="atomic-none-tail",
    defect="ATOMIC2020 spells 'no tail exists' as the literal tail `none` (147,608 of "
           "1,331,113 rows). Emitting it as an object folds a CONFIRM toward the entity "
           "`none`, asserting the opposite of what the corpus said.",
    file="app/Laplace.Decomposers/Atomic2020/Atomic2020Decomposer.cs",
    before="            assertsAbsence ? null : UnderscoredUtf8Canonicalize.ToSpaces(tail),",
    after="            UnderscoredUtf8Canonicalize.ToSpaces(tail),",
    project="Laplace.Decomposers.Tests",
    filter="FullyQualifiedName~Atomic2020NoneTail",
  ),
  dict(
    id="iso-retirement-remedy",
    defect="ISO 639-3 retirements required a 3-char Change_To, dropping 174 of 386 rows: "
           "72 reason=N (the standard stating a code names no real language) and 102 "
           "reason=S (successors named in Ret_Remedy, not Change_To).",
    file="app/Laplace.Decomposers/ISO/ISODecomposer.cs",
    before='        if (reason == "N") return (reason, [], true);\n'
           '        string[] successors =\n'
           '            changeTo.Length == 3 ? [changeTo] : SuccessorsFromRemedy(remedy);',
    after='        string[] successors = changeTo.Length == 3 ? [changeTo] : [];',
    project="Laplace.Decomposers.Tests",
    filter="FullyQualifiedName~IsoRetirement",
  ),
  dict(
    id="unicode-normalization-qc",
    defect="DerivedNormalizationProps.txt is the only UCD file stating a negative and the "
           "decomposer never opened it: 36,424 REFUTEs and 264 DRAWs, the substrate's first.",
    file="app/Laplace.Decomposers/Unicode/UcdProperties.cs",
    before='foreach (var (st, en, field) in OptionalRangeFile(ucdDir, "DerivedNormalizationProps.txt"))',
    after='foreach (var (st, en, field) in Array.Empty<(uint,uint,string)>())',
    project="Laplace.Decomposers.Tests",
    filter="FullyQualifiedName~UcdNormalizationQc",
  ),
  dict(
    id="framenet-total-annotated",
    defect="FrameNet states annotation depth as totalAnnotated on 13,572 <lexUnit>. Unread, "
           "an LU with 116 instances evoked its frame exactly as hard as one with 1.",
    file="app/Laplace.Decomposers/FrameNet/FrameNetLuIngest.cs",
    before='long.TryParse((string?)root.Attribute("totalAnnotated"), out long ta) && ta > 0 ? ta : 1;',
    after='1;',
    project="Laplace.Decomposers.Tests",
    filter="FullyQualifiedName~ParseLu_Reads_TotalAnnotated",
  ),
  dict(
    id="topology-threads-as-cores",
    defect="DetectPlatform discarded pools.PhysicalPCores on any non-hybrid CPU and reported "
           "Environment.ProcessorCount -- the LOGICAL count -- as physical. 911 tests passed "
           "at both values (GH #986).",
    file="app/Laplace.Core/Core/CpuTopology.cs",
    before="        int physical = pools.PhysicalPCores > 0 ? Math.Min(pools.PhysicalPCores, logical) : logical;",
    after="        int physical = logical;",
    project="Laplace.Core.Tests",
    filter="FullyQualifiedName~ReportedPhysicalCores",
  ),
  dict(
    id="consensus-key-composition",
    defect="consensus_id = blake3(subject || type || object). Swapping the component order "
           "silently re-keys every cell in the substrate and splits the fold: the same "
           "triple would land on two different rows, and neither would carry the other's "
           "witnesses. Client and server must compose it identically or the client writes "
           "cells the server can never find.",
    file="app/Laplace.Core/Core/ConsensusKeys.cs",
    before="        subject.WriteBytes(buf[..16]);\n"
           "        type.WriteBytes(buf.Slice(16, 16));\n"
           "        obj.WriteBytes(buf.Slice(32, 16));",
    after="        type.WriteBytes(buf[..16]);\n"
          "        subject.WriteBytes(buf.Slice(16, 16));\n"
          "        obj.WriteBytes(buf.Slice(32, 16));",
    project="Laplace.Core.Tests",
    filter="FullyQualifiedName~ConsensusKeysParity",
  ),
]

def run_tests(project, filt):
    proj = APP / project / f"{project}.csproj"
    r = subprocess.run(
        ["dotnet", "test", str(proj), "-c", "Release", "--nologo", "--filter", filt],
        capture_output=True, text=True, cwd=str(APP))
    return r.returncode, r.stdout + r.stderr

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("-k", default="")
    a = ap.parse_args()

    picked = [m for m in MUTATIONS if a.k in m["id"]]
    if a.list:
        for m in picked:
            print(f"{m['id']:<28} {m['filter']}")
        return 0
    if not picked:
        print(f"no mutation matches -k {a.k!r}", file=sys.stderr)
        return 2

    failures = []
    for m in picked:
        path = ROOT / m["file"]
        src = path.read_text(encoding="utf-8")

        # A mutation that does not apply is a HARD ERROR. Applying nothing and seeing green
        # is precisely the vacuous result this harness exists to detect.
        if src.count(m["before"]) != 1:
            print(f"FAIL {m['id']}: anchor not found exactly once in {m['file']} "
                  f"(found {src.count(m['before'])}) — the harness is stale, not the code",
                  file=sys.stderr)
            failures.append(m["id"])
            continue

        backup = tempfile.NamedTemporaryFile(delete=False, suffix=".bak")
        backup.write(src.encode("utf-8")); backup.close()
        try:
            path.write_text(src.replace(m["before"], m["after"]), encoding="utf-8")
            code, out = run_tests(m["project"], m["filter"])
            if code == 0:
                print(f"FAIL {m['id']}: test PASSED with the defect reintroduced — it does "
                      f"not guard what it claims\n     defect: {m['defect']}", file=sys.stderr)
                failures.append(m["id"])
            else:
                print(f"ok   {m['id']}: red with the defect restored")
        finally:
            shutil.copyfile(backup.name, path)
            pathlib.Path(backup.name).unlink(missing_ok=True)

    # Every file restored; prove it rather than assume it.
    dirty = subprocess.run(["git", "status", "--porcelain", "--", *(m["file"] for m in picked)],
                           capture_output=True, text=True, cwd=str(ROOT)).stdout.strip()
    if dirty:
        print(f"FAIL: harness left the tree modified:\n{dirty}", file=sys.stderr)
        return 2

    print(f"\n{len(picked) - len(failures)}/{len(picked)} guarding tests fail against their defect")
    return 1 if failures else 0

if __name__ == "__main__":
    sys.exit(main())
