# GH #593 — LSP-backed semantic decomposer: design proposal

Status: PROPOSAL, not started **as of 2026-08-05** (written 2026-07-25); 592 commits
have landed since. Re-verify against GH #593 and HEAD before treating that as current.
Written for review before any implementation.
Companion to the batch-ingest fix PR (#662, covering #592/#594/#595/#596) from
the same corpus run that surfaced this issue.

## 0. "DEFINES: 0 rows" — checked, this is NOT a bug

The issue's live counts list `DEFINES` as declared-but-emitting-0-rows and
reads that as a gap. Traced it fully (correcting an earlier pass at this that
wrongly concluded it was dead code — checked again before touching anything):
`GrammarEntityBuilder.cs:392` emits `NativeAttestation.Categorical(d,
"DEFINES", nm, ...)` from real tag captures (`TagType.DefFunction`/`DefType`/
`DefVar`). `relation_types.toml` declares `DEFINES` as an alias of
`HAS_DEFINITION` (`[[alias]] surface = "DEFINES" canonical =
"HAS_DEFINITION"`), and alias resolution happens at attestation time — so
every `DEFINES` row lands in the substrate already folded under its canonical
name. `HAS_DEFINITION` shows 2704 rows in the issue's own count table. That's
where the "missing" `DEFINES` rows are: correctly working as designed, just
invisible if you only grep raw relation-type strings without knowing the
alias table. **No fix needed here** — flagging only so nobody re-opens this
as a bug later.

## 1. What's actually being asked for

Tree-sitter gives grammar (`IS_A` = "this token is a syntax node of this
kind"), and name-string matching gives untyped `CALLS`/`REFERENCES`. Neither is
symbol resolution. The issue wants real `IMPORTS`/`EXTENDS`/`IMPLEMENTS`/
`OVERRIDES`/`HAS_PARAMETER_TYPE`/`RETURNS_TYPE`/`DEPENDS_ON_PACKAGE` —
edges that only exist once something has actually bound an identifier to a
specific declaration, resolved a type, or walked an inheritance chain. That's
what a language server computes internally on every keystroke in an IDE;
nothing in tree-sitter (or any static grammar) can produce it, because that's
precisely the boundary between syntax and semantics.

Confirmed no existing LSP infrastructure anywhere in this repo — greenfield.

## 2. Architectural placement: this is a CALCULATED layer, not a witnessed one

CLAUDE.md's own witnessed/calculated split (doc 08) is a closer fit here than
it first looks, and the codebase already has a concrete template for exactly
this shape: **`ChessAnalyzeDecomposer`** (`Laplace.Chess/Service/
ChessAnalyzeDecomposer.cs`). Read closely, it is:

- A **separate decomposer / CLI verb** (`laplace ingest chess-analyze`), not a
  patch on the witness decomposer (`ChessPgnDecomposer`).
- Its own `SourceId`/`TrustClass` ("ChessAnalysis"), distinct from the witness
  source ("ChessPgn") — analysis is a distinct, less-trusted voice, never
  mashed into the witness's provenance.
- It reads **already-witnessed rows out of the substrate** (via
  `ChessWitnessHydrator`), not raw files — the witness decomposer already
  recorded the PGN; analysis re-derives from what's already there.
- A **versioned marker** (`ChessAnalyze.Version`, `AnalysisMarkerId(gameId,
  version)`) as the record's trunk root, so bumping the version re-derives
  cleanly on the next run with zero backfill/rebuild machinery — this is the
  established alternative to "no batch backfill of consensus."
- Since GH #600, the *fast path* also runs this derivation inline during the
  witness ingest (`ChessPgnDecomposer.Compose -> DeriveFromParsed`) so a fresh
  ingest doesn't need the separate pass at all; the standalone decomposer
  exists for backfill and version bumps on already-recorded data.

The LSP semantic pass should follow this exact shape: a new decomposer
(`LspSemanticAnalyzeDecomposer` or similar), its own source/trust
(`"CodeLspAnalysis"`, outranked relative to whatever ground truth would ever
correct it — same "one voice among many" posture as every other analysis
source), reading already-witnessed `RepoDecomposer` file rows out of the
substrate rather than re-walking the filesystem, with a versioned marker per
(repo, LSP-server-version) so bumping OmniSharp/Pyright versions or the
extraction logic re-derives without a backfill.

## 3. Where this pattern MUST diverge from ChessAnalyze — granularity

Every existing calculated-layer decomposer operates per-record (one game, one
row at a time) because the derivation is local. LSP resolution is not: an LSP
server needs the **whole project/repo loaded** in one long-lived process to
resolve a cross-file reference, an import, or a type hierarchy correctly.
Feeding it one file at a time defeats the entire point.

This means:

- The natural batch unit is **one repo per language**, not N records. Spin up
  one LSP server process per (repo, language) pair, `initialize` +
  `textDocument/didOpen` every source file in that language for the repo,
  issue the resolution requests, then shut the process down. `DefaultBatchSize`
  / `EstimatedComposeUnitsPerRecord` (the knobs every other decomposer here
  tunes) don't map cleanly onto "how many LSP servers run concurrently" —
  this decomposer's concurrency knob is process count, not record count.
- Process lifecycle needs the same rigor this session's #595 diagnosis used
  on the native hang: a wedged/crashed language server (malformed project,
  missing SDK, infinite project-load on a huge monorepo) must not hang the
  whole ingest — needs an explicit per-repo timeout and hard kill, with the
  repo's marker left unset so it's picked up (and can be investigated) on a
  later run rather than silently "succeeding" with zero rows.
- Multi-project repos (which `.sln`/`.csproj` for OmniSharp, which Python
  root(s) for Pyright) need explicit discovery logic — this is new surface
  area, tree-sitter has nothing analogous.

## 4. Symbol identity — the one invariant that must not be violated

Every entity in this substrate is content-addressed; the mesh only works
because two decomposers producing the same key produce the same node. The LSP
layer's resolved symbols **must** resolve to the exact same entity ids
`RepoDecomposer`'s tree-sitter pass already minted for that
file/span/identifier — never a parallel identity space keyed off, say,
OmniSharp's own internal symbol IDs. Concretely: when the LSP server resolves
`CALLS` foo-the-name to "definition at file X, line N, column M", the
attestation's object must be the SAME content-addressed entity that
`RepoDecomposer`'s span-lookup (the thing #595 just fixed the performance of)
already produced for that exact byte range in file X — i.e. this decomposer
needs read access to the existing span→entity mapping, not just raw
LSP responses.

This is also exactly the fix for the issue's own "generic name false
convergence" observation (`join`/`get`/`len` conflating unrelated targets):
resolving through the LSP replaces name-string matching with
file+span-derived identity, so `str.join` and `os.path.join` land on two
distinct entities (their distinct definition sites) instead of one shared
name-keyed node. Distinctive names already converge correctly via content
addressing with zero LSP involvement (verified live this session:
`RolePermission`/`UserRole`/`AuditEntry`/etc. converge on `BaseEntity` with no
resolver at all) — LSP is needed specifically to stop the *false* convergence
on generic/overloaded names, not to invent convergence tree-sitter is already
providing for the distinctive case.

## 5. New relation types + manifest reseed

None of `IMPORTS`/`EXTENDS`/`IMPLEMENTS`/`OVERRIDES`/`HAS_PARAMETER_TYPE`/
`RETURNS_TYPE`/`DEPENDS_ON_PACKAGE` exist yet in `relation_types.toml`.
Per CLAUDE.md: highway bits are assigned alphabetically at codegen time, so
adding these renumbers bits and owes a reseed (regenerate, never backfill) —
this needs to land as its own step, gated the same way any relation-manifest
change is (the policy job's determinism gate), before the decomposer that
emits them can declare them in `relationNodeNames`. Also: per the binding
decomposer rule, this new decomposer must declare every one of these in
`InitializeAsync.relationNodeNames`, or it will fault the native attestation
path exactly like the pre-existing `HAS_POS` case the architecture gate test
pins.

Suggested `rank`/`symmetry` (matching the shape of neighboring structural
relations like `HAS_AST_KIND`/`CANONICAL_DECOMPOSES_TO`, for whoever finalizes
the manifest entries — not binding, just a starting point):

| relation | rank | symmetry |
|---|---|---|
| `IMPORTS` | `standards_structural` | asymmetric |
| `EXTENDS` | `standards_structural` | asymmetric |
| `IMPLEMENTS` | `standards_structural` | asymmetric |
| `OVERRIDES` | `standards_structural` | asymmetric |
| `HAS_PARAMETER_TYPE` | `associative` | asymmetric |
| `RETURNS_TYPE` | `associative` | asymmetric |
| `DEPENDS_ON_PACKAGE` | `associative` | asymmetric |

## 6. Two LSP integrations, same shape

Both OmniSharp (C#) and Pyright (Python) speak standard LSP over stdio
JSON-RPC — `initialize` → `textDocument/didOpen` per file → then per
identifier-of-interest: `textDocument/definition` (resolves `CALLS`/
`REFERENCES` to a real target, replacing the untyped versions with a typed
edge to a specific entity), `textDocument/typeDefinition` /
`textDocument/implementation` (→ `IMPLEMENTS`/`EXTENDS`/`OVERRIDES`),
`workspace/symbol` (repo-wide symbol inventory, cheap cross-check against
what tree-sitter already found). A thin, protocol-only client (no existing
.NET LSP client dependency currently in the tree) is enough — this doesn't
need a full editor-grade LSP client library, just the handful of request
shapes above.

Provisioning is new operational surface: unlike tree-sitter (vendored,
built from source under `external/`), OmniSharp and Pyright are external
tool installs. Needs explicit version-pinned setup in `scripts/setup-host.sh`
(Linux) and the Windows `scripts/win/` toolchain, analogous to how
`build-cutechess.cmd` provisions the chess-lab binaries — this is a new
row in that table, not a code-only change.

## 7. Suggested incremental delivery order

1. Relation-type manifest reseed for the 7 new relations (§5), landed and
   green on its own before any decomposer references them.
2. `LspSemanticAnalyzeDecomposer` scaffolding + OmniSharp only, proving the
   full shape end-to-end on one language: process lifecycle, marker
   versioning, span→entity identity reuse (§4), timeout/kill handling (§3).
3. Extend to Pyright once the OmniSharp path is proven — the second
   language should mostly be "does the abstraction actually generalize,"
   not new architecture.

(§0's `DEFINES` observation needed no follow-up — confirmed working as
designed, listed only so it isn't re-flagged as a bug later.)

## Open questions for whoever approves this

- Timeout budget per repo for LSP project-load (this varies wildly by repo
  size — needs a number, not "reasonable").
- Whether `workspace/symbol`'s repo-wide inventory is worth attesting on its
  own (cheap cross-check value) or only the per-identifier resolution
  requests matter.
