## Bucket: I5 — root Claude-authored audit docs, CLAUDE.md, bench CSVs/logs, LICENSE

### Files read (coverage)
- [x] `D:/Repositories/Laplace/AGENT_001_REPORT.md` — read in full (148 lines)
- [x] `D:/Repositories/Laplace/AGENT_002_REPORT.md` — read in full (169 lines)
- [x] `D:/Repositories/Laplace/AGENT_003_REPORT.md` — read in full (166 lines)
- [x] `D:/Repositories/Laplace/AUDIT-CONSOLIDATED.md` — read in full (120 lines)
- [x] `D:/Repositories/Laplace/AUDIT-DECOMPOSERS.md` — read in full (311 lines)
- [x] `D:/Repositories/Laplace/AUDIT-LINKING.md` — read in full (182 lines)
- [x] `D:/Repositories/Laplace/AUDIT-PERF.md` — read in full (326 lines)
- [x] `D:/Repositories/Laplace/AUDIT-REPORT.md` — read in full (262 lines)
- [x] `D:/Repositories/Laplace/CLAUDE.md` — read in full (180 lines)
- [x] `D:/Repositories/Laplace/LICENSE` — read in full (38 lines; clean, proprietary all-rights-reserved notice, no issues)
- [x] `D:/Repositories/Laplace/conceptnet-bench-results.csv` — read in full (31 lines)
- [x] `D:/Repositories/Laplace/omw-bench-results.csv` — read in full (35 lines)
- [x] `D:/Repositories/Laplace/conceptnet-ingest.log` — read in full (5 lines)
- [x] `D:/Repositories/Laplace/parity-fail.log` — read in full (185 lines)

Note: the bucket list `.txt` named only 12 files; the prompt added `conceptnet-ingest.log` and `parity-fail.log`. Both read.

---

### FINDINGS

#### F1 — [HIGH][disparagement / stale-doc] AUDIT-CONSOLIDATED.md and AUDIT-DECOMPOSERS.md report THREE "CRITICAL/CONFIRMED" bugs that are already FIXED in the live code
The two oldest audit docs (dated 2026-06-18) present as live, severity-CRITICAL defects three things that the code now does correctly. AUDIT-LINKING.md (2026-06-21) and the AGENT reports already note them as fixed; CONSOLIDATED/DECOMPOSERS were never updated. Verified each against code:

1. **JSON string-leaf merkle ≠ ContentWitnessBatch ("graph silently fragments", CONSOLIDATED #1 / DECOMPOSERS top CRITICAL at `grammar_compose.cpp:266`).**
   VERIFIED FALSE NOW: `engine/core/src/grammar_compose.cpp:240-247` adopts `laplace_content_root_id(content_span, content_len, out_root_id)` for JSON scalar leaves specifically so multi-grapheme surfaces ("New York") converge with the content path. The comment at :240 states this intent. AUDIT-LINKING #3 confirms "Fixed (2026-06-21)."

2. **FrameNet "Subframe of" inverted (CONSOLIDATED #5 / DECOMPOSERS `FrameNetDecomposer.cs:41`).**
   VERIFIED FALSE NOW: `FrameNetDecomposer.cs:276-279` special-cases the relation: comment "the HAS_SUBEVENT edge runs Y -> X (subject HAS_SUBEVENT object)" and `if (rel.Type == "Subframe of")` swaps operands. Orientation is correct in code. AUDIT-LINKING confirms "Fixed orientation."

3. **UD XPOS parsed but never emitted (CONSOLIDATED #6 / DECOMPOSERS HIGH at `UDDecomposer.cs:403`).**
   VERIFIED FALSE NOW: `UDDecomposer.cs:52` bootstraps `HAS_XPOS`; lines 307-319 emit `form HAS_XPOS xposId` via `VocabularyAnchor.Emit` + `NativeAttestation.Categorical`. AUDIT-LINKING confirms "XPOS: Now emitted (HAS_XPOS)."

How verified: Grep + Read of each cited file/symbol. CONFIDENCE: high. These are exactly the "frequently WRONG" stale-status defects CLAUDE.md §0 warns about — CONSOLIDATED's TIER-0 "accuracy bugs that silently corrupt the substrate" list is partly obsolete, so anyone actioning CONSOLIDATED would redo finished work.

#### F2 — [MEDIUM][correctness / contract-defect] CLAUDE.md §0 asserts the AGENT_*_REPORT.md files are "deleted" — they are present in the repo root
`CLAUDE.md:23`: "Any `AGENT_*_REPORT.md` (deleted)…"; `CLAUDE.md:130` lists superseded plans "(deleted, listed for provenance)". But `AGENT_001/002/003_REPORT.md` exist at repo root (this bucket read all three; git status shows them tracked). The project contract makes a false factual claim about the tree state. Either the files should be deleted (the contract's intent) or the contract corrected. CONFIDENCE: high (files read directly).

#### F3 — [MEDIUM][dead-doc / clutter] AUDIT-REPORT.md, AUDIT-PERF.md, AUDIT-DECOMPOSERS.md, AUDIT-CONSOLIDATED.md are stale prior-Claude work product that CLAUDE.md §0 explicitly says not to trust, yet they remain checked in
CLAUDE.md §0 names `AUDIT-*.md` among docs whose status tags are "Claude-authored editorializing — frequently wrong." They are large (AUDIT-DECOMPOSERS 97 KB, AUDIT-PERF 79 KB, AUDIT-REPORT 75 KB), self-contradicting (F1), and superseded. They are clutter that invites re-deriving wrong models. Recommend deletion or archival. CONFIDENCE: high.

#### F4 — [INFO][disparagement vs real] The AUDIT/AGENT docs are a MIX — many findings are real and code-traceable, not just disparagement
Counter-balance to F1/F3: spot-checks show several findings are accurate and not editorializing:
- `EntityTier.Vocabulary = 5` (AGENT_003 §2, CLAUDE.md "live violation"): VERIFIED present at `app/Laplace.SubstrateCRUD/EntityTier.cs:20` (const byte Vocabulary = 5, with the codepoint-tier-0 dodge comment). Real tier-as-kind violation.
- `ResolveComposeWorkers` capped at 4 (AGENT_001 §2): VERIFIED at `StructuredGrammarIngest.cs:124` — `return Math.Min(4, CpuTopology.ResolveCpuBoundWorkers(...))`. The "arbitrary 4" choke is real and still present.
- `feature_extractor.cpp` is a full stub (AUDIT-REPORT Low #19): VERIFIED — `engine/synthesis/src/feature_extractor.cpp` returns nullptr/-1/0 from all four exported functions; struct is `int _placeholder`. Real dead-but-bound module.
So the docs cannot be dismissed wholesale; they must be triaged finding-by-finding against current code. The disparagement risk is in the framing/status tags and the stale CRITICALs (F1), not in every finding. CONFIDENCE: high.

#### F5 — [LOW][misnamed-artifact] parity-fail.log actually records a PASSING test run, not a failure
`parity-fail.log` (2026-06-22) is an MSBuild+xUnit transcript: "Build succeeded. 0 Warning(s) 0 Error(s)", "Test Run Successful. Total tests: 1, Passed: 1" — the single test `CrossSourceLinkingTests.ConceptAnchor_SynsetId_Requires_Cili_Map` passed. The filename implies a parity failure; the content is a green run. Either a stale capture or a misnamed file. Also note CLAUDE.md §0(3) caution applies: a 1-test pass proves very little. CONFIDENCE: high.

#### F6 — [MEDIUM][measured-failure] conceptnet-ingest.log records a FAILED ConceptNet ingest (EXIT=-1)
`conceptnet-ingest.log` (2026-06-19 18:16): started ConceptNetDecomposer layer=2, `input_units=34074917` assertions, ran ~90 s, then `INGEST_END … EXIT=-1`. This is a real failed run, consistent with AGENT_001's account of a wrecked DB / throttled compose. Not perf evidence — a crash record. CONFIDENCE: high (log read).

#### F7 — [MEDIUM][measured-instability] The bench CSVs document crashes and instability, NOT stable perf numbers
- `conceptnet-bench-results.csv`: most rows carry crash exit codes. `-1073741819` = 0xC0000005 ACCESS_VIOLATION (the `parallel+unordered` and `parallel+serial` @1% rows); `serial @1%` exits `1` (8 s); `serial @100%` exits `-1` after 1952.8 s; only the `max_units` runs exit `0` (120.9 s / 36.4 s / 152.1 s / 200 s). A final `serial max_units=10000` exits `-1` at 471.9 s. The "=== CONCEPTNET BENCH DONE ===" markers print even when constituent rows crashed.
- `omw-bench-results.csv`: first batch all `-532462766` = 0xE0434352 (.NET CLR managed exception) at 2-3 s — every config crashed. Second batch mixes successes (`phased+epoch`/`phased+serial` exit 0, 26-385 s) with `legacy+serial` exit `1` and `-2147450749`. So `phased` paths completed; `legacy` paths crashed/failed.
Takeaway: these CSVs evidence an unstable native path (access violations, CLR exceptions) and that the "phased/max_units" lanes complete while "parallel/legacy/serial-full" lanes crash — they are NOT a clean perf comparison and should not be cited as throughput results. This corroborates the AccessViolation crash noted in the chess/state memories. CONFIDENCE: high (CSV read; exit codes decoded).

#### F8 — [LOW][clutter] Bench CSVs + logs are loose working artifacts at repo root
`conceptnet-bench-results.csv`, `omw-bench-results.csv`, `conceptnet-ingest.log`, `parity-fail.log` are ad-hoc run captures committed at the repo root (alongside the audit MDs). They are transient session debris, not source. Recommend moving to a scratch/artifacts dir or deleting. CONFIDENCE: med (judgment).

#### F9 — [INFO][contract-accuracy] CLAUDE.md's architectural claims largely match code where spot-checked; its KNOWN-violation callouts are accurate
CLAUDE.md is the project contract. Spot-checks of its concrete claims: tier ladder constants (`EntityTier` 0-4 + the flagged Vocabulary=5), the "arbitrary 4" compose-worker cap, the inline-fold conventions, and the convergence-index model all line up with code I traced or with verified findings (F4). The one concrete factual error found is F2 (AGENT reports "deleted"). The doc is substantially TRUE; fix F2 and it stands. CONFIDENCE: med (broad claims not all individually traceable in this bucket; the testable ones held).

---

### Bucket summary
- Files read: 14/14 (all in full).
- Findings: HIGH 1 (F1), MEDIUM 4 (F2, F3, F6, F7), LOW 3 (F5, F8, + F2-adjacent), INFO 2 (F4, F9). LICENSE clean.
- Severity tally: 1 HIGH, 4 MEDIUM, 3 LOW, 2 INFO.
- **Worst issue (F1):** AUDIT-CONSOLIDATED.md and AUDIT-DECOMPOSERS.md headline three CRITICAL "substrate-corrupting" bugs (JSON-leaf merkle divergence, FrameNet Subframe inversion, dropped UD XPOS) that are all ALREADY FIXED in the live code — verified at `grammar_compose.cpp:240-247`, `FrameNetDecomposer.cs:276-279`, `UDDecomposer.cs:52/307-319`. Acting on these stale docs would mean redoing finished work or "fixing" correct code. The audit docs are a mix of real and obsolete findings and must be triaged against current code per finding; they are the precise "frequently wrong, disparaging" prior-Claude artifacts CLAUDE.md §0 warns about and are strong candidates for deletion.
