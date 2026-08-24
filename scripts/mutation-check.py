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

NATIVE MUTATIONS require the engine rebuilt AND the test loading that build. Until
2026-08-24 they could not work at all: /etc/ld.so.conf.d puts /opt/laplace/lib on the system
loader path, so a managed test loaded the INSTALLED liblaplace_core.so and never build/.
Mutating LAPLACE_GLICKO2_NEUTRAL_MU_FP 1500 -> 1400 and relinking left
NeutralMu_MatchesServerConstant GREEN -- it asserts a hard literal and would have caught the
change, but was reading a copy installed 21 minutes earlier. Directory.Build.props now copies
the built libraries beside every test binary, .NET probes the app directory before the OS
loader, and NativeArtifactIdentityTests fails the run if the mapped artifact is not the built
one. The same mutation now reports Expected 1500000000000 / Actual 1400000000000.

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
  dict(
    id="glicko-neutral-mu",
    defect="Every witness in the substrate plays an opponent pinned at "
           "CONSENSUS_FOLD_NEUTRAL_MU = 1500 (consensus_fold_apply_partial), so the "
           "client and the server must agree on it exactly or the managed fold and the "
           "server fold produce different ratings from identical evidence -- the "
           "bit-reproducibility the whole fold rests on.",
    file="engine/core/include/laplace/core/glicko2.h",
    before="#define LAPLACE_GLICKO2_NEUTRAL_MU_FP   1500000000000LL",
    after="#define LAPLACE_GLICKO2_NEUTRAL_MU_FP   1400000000000LL",
    project="Laplace.Core.Tests",
    filter="FullyQualifiedName~NeutralMu_MatchesServerConstant",
    rebuild_native=True,
  ),
  dict(
    id="rule8-undeclared-relation",
    defect="Rule #8: a decomposer declares every relation it emits. Nothing enforced it. "
           "DecomposerArchitectureGateTests asserts DeclaredCoversEmitted over three "
           "literals and walks no source; a first replacement read the db-tier fixture's "
           "own database, matched zero sources, and passed with this very declaration "
           "deleted. Found a real pre-existing violation on landing: ISODecomposer emitted "
           "HAS_ISO639_2B_CODE, HAS_ISO639_2T_CODE and SUPERSEDED_BY without declaring any "
           "of them.",
    file="app/Laplace.Decomposers/Unicode/UnicodeSource.cs",
    before='        "HAS_BLOCK", "HAS_UPPERCASE_MAPPING", "HAS_LOWERCASE_MAPPING",',
    after='        "HAS_UPPERCASE_MAPPING", "HAS_LOWERCASE_MAPPING",',
    project="Laplace.Substrate.Tests",
    filter="FullyQualifiedName~Rule8DeclaredCovers",
  ),
  dict(
    id="ladder-rung-calls-upward",
    defect="The prompt-resolution ladder is word_segment -> prompt_words -> prompt_language "
           "-> prompt_state -> prompt_coherence -> elect, and a rung may never call a rung "
           "above it. prompt_language reading prompt_state is a cycle -- prompt_state "
           "resolves a token's language AGAINST this tally -- and it terminates in \"stack "
           "depth limit exceeded\" at runtime, not at install.",
    file="extension/laplace_substrate/src/prompt_language.c",
    before='            "SELECT p.id FROM converse.prompt_words($1) p WHERE p.id IS NOT NULL",',
    after='            "SELECT p.id FROM converse.prompt_state($1) p WHERE p.id IS NOT NULL",',
    project="Laplace.Substrate.Tests",
    filter="FullyQualifiedName~PromptResolutionLadder",
  ),
  dict(
    id="elector-key-order-drift",
    defect="converse.elect is THE topic election and every elector must share ONE key "
           "order. Six bodies carry it; a seventh spelling is a second election policy "
           "that answers differently while every test still passes.",
    file="extension/laplace_substrate/sql/functions/converse/elect.sql.in",
    before="    ORDER BY y.specificity DESC NULLS LAST,\n             y.rel_mass    DESC NULLS LAST,",
    after="    ORDER BY y.rel_mass    DESC NULLS LAST,\n             y.specificity DESC NULLS LAST,",
    project="Laplace.Substrate.Tests",
    filter="FullyQualifiedName~ElectorArchitectureGate",
  ),
  dict(
    id="tier-mixed-into-identity",
    defect="THE content-addressing law (spec 05 #1b): same content = same hash at every "
           "tier. The id is a function of the child-id sequence and nothing else -- no "
           "tier, no ordinal, no container. hash128.c discards the tier parameter with an "
           "explicit (void)tier and records that a tier byte was briefly mixed in on "
           "2026-07-01, broke the law, and was reverted. Mixing it back in re-mints every "
           "entity per tier, so 'cat' the word and 'cat' the one-word sentence stop being "
           "the same entity and no cross-source merge can ever occur again.",
    file="engine/core/src/hash128.c",
    before="    (void)tier;\n"
           "    static const uint8_t MERKLE_DOMAIN = 0x01;\n"
           "    blake3_hasher h;\n"
           "    blake3_hasher_init(&h);\n"
           "    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));",
    after="    static const uint8_t MERKLE_DOMAIN = 0x01;\n"
          "    blake3_hasher h;\n"
          "    blake3_hasher_init(&h);\n"
          "    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));\n"
          "    blake3_hasher_update(&h, &tier, sizeof(tier));",
    project="Laplace.Core.Tests",
    filter="FullyQualifiedName~ContentAddressingLaw",
    rebuild_native=True,
  ),
]

def rebuild_native():
    """A native mutation is invisible until the engine is rebuilt AND the test loads that
    build. Directory.Build.props copies build/engine/*/*.so beside each test binary, and
    PreserveNewest refreshes the copy on the next `dotnet test`."""
    r = subprocess.run(["cmake", "--build", "build", "--target", "laplace_core", "-j"],
                       capture_output=True, text=True, cwd=str(ROOT))
    return r.returncode == 0

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

    dirty_before = subprocess.run(
        ["git", "status", "--porcelain", "--", *(m["file"] for m in picked)],
        capture_output=True, text=True, cwd=str(ROOT)).stdout.strip()

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
            if m.get("rebuild_native") and not rebuild_native():
                print(f"FAIL {m['id']}: native rebuild failed — cannot observe the mutation",
                      file=sys.stderr)
                failures.append(m["id"]); continue
            code, out = run_tests(m["project"], m["filter"])

            # A filter matching NOTHING exits 0, and reading that as "the test passed with
            # the defect" is the vacuous success this harness exists to detect -- committed
            # here, by naming Laplace.Substrate.Tests for a test that lives in
            # Laplace.Core.Tests. Require evidence that a test actually ran.
            if "No test matches the given testcase filter" in out or "Total tests: 0" in out:
                print(f"FAIL {m['id']}: filter {m['filter']!r} matched NO test in "
                      f"{m['project']} — the harness verified nothing", file=sys.stderr)
                failures.append(m["id"]); continue

            if code == 0:
                print(f"FAIL {m['id']}: test PASSED with the defect reintroduced — it does "
                      f"not guard what it claims\n     defect: {m['defect']}", file=sys.stderr)
                failures.append(m["id"])
            else:
                print(f"ok   {m['id']}: red with the defect restored")
        finally:
            shutil.copyfile(backup.name, path)
            pathlib.Path(backup.name).unlink(missing_ok=True)
            if m.get("rebuild_native"):
                rebuild_native()   # restore the binary to match the restored source

    # Every file restored; prove it rather than assume it. Compared against the state BEFORE
    # the run, not against a clean tree: a file the caller had already edited is not something
    # this harness left behind, and failing on it made the check unusable mid-change.
    dirty_after = subprocess.run(["git", "status", "--porcelain", "--", *(m["file"] for m in picked)],
                                 capture_output=True, text=True, cwd=str(ROOT)).stdout.strip()
    if dirty_after != dirty_before:
        print("FAIL: harness changed the working tree.\n"
              f"  before: {dirty_before or '(clean)'}\n  after : {dirty_after or '(clean)'}",
              file=sys.stderr)
        return 2

    print(f"\n{len(picked) - len(failures)}/{len(picked)} guarding tests fail against their defect")
    return 1 if failures else 0

if __name__ == "__main__":
    sys.exit(main())
