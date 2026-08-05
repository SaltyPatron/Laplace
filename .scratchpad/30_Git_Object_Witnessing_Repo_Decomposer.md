<!-- DRAINED 2026-07-20 — tracked by GitHub issues #452 (witness the git object DB
     as a Merkle-DAG; RepoDecomposer.cs:123 still skips .git, and the vendored
     tree-sitter-gitcommit/gitdiff grammars are still unregistered in grammar_registry.c)
     and #504 (decide the git-lane relation ledger BEFORE codegen — adding relations
     renumbers highway bits alphabetically and owes a reseed).
     Section 6's two-stage grammar-unpack plan is folded into #452.
     Open work lives in GitHub issues. -->

# 30 — Git-object witnessing: the Repo decomposer as a Merkle-DAG valet

**Thesis.** Git is *already* a content-addressed Merkle DAG. It maps onto the
substrate's identity law almost 1:1. The current `RepoDecomposer` throws that
structure away — it skips `.git` and witnesses only the working tree's current
files. Teaching it to witness the git object DB (plus the working tree *as its
own source*) turns every "what happened in this repo's history" question into a
native witness-count / DAG-reachability read. No bespoke comparison code — the
fold IS the comparison.

Grounding (verified 2026-07-16):
- `RepoDecomposer.cs:123` skips `obj/ bin/ .git/ node_modules/` → working-tree
  only, no commit/ref/history.
- `GrammarComposeDecomposer<RepoSource,FullScope>`, LayerOrder 2, trust
  `StructuredCorpus` (0.70, `WitnessConstants`).
- Entity types present: `RepoRoot`, `SourceFile`, `Document`, `CodeConcept`
  (`EntityTypeRegistry`). Missing: `GitCommit`, `GitTree`.
- Chess is the template: recorder = witnessed verbatim; analyzer = calculated,
  versioned, evictable (`ChessPgnDecomposer` vs `ChessAnalyzeDecomposer`).

---

## 1. The git → substrate identity map

| Git object | Substrate | Identity vs witness |
|---|---|---|
| blob | Content entity (its bytes) | IDENTITY — content hash. **Same entity as the working-tree file when bytes match.** This collision IS the sync check. |
| tree | composed Content (ordered dir entries) | IDENTITY — content-addressed by its ordered constituent sequence, exactly like a document's tier tree. |
| commit | `GitCommit` entity | IDENTITY of the commit object; but it *carries* provenance (author, epoch, message) and trajectory (parents). |
| ref (branch / HEAD / `origin/*` / tag) | a **witness / source pointer** | PROVENANCE — "SaltyPatron's `origin` asserts commit X is `main`'s tip." Refs are sources, never identity ([[content-is-identity-source-is-witness]]). |
| working tree | Content witnessed by a **WorkingTree source** | verbatim on-disk record, a *distinct source* over the same content space. |

The load-bearing move: **a git blob and the on-disk file with identical bytes
resolve to the same content id by construction.** So "committed vs on-disk" is
never a diff we run — it's whether a content entity carries both a Committed
witness and a WorkingTree witness, or only one.

## 2. Two record sources + one calculated source (chess split)

RECORD (verbatim, high trust — both are cryptographic/literal records):
- **`GitObjectDb` source** — everything reachable from refs: commits, trees,
  blobs, the parent DAG, author/message/date. This is the repo's own testimony.
- **`WorkingTree` source** — current on-disk file content. What's actually on
  disk right now, committed or not.

CALCULATED (deferred, versioned, evictable — `CodeAnalysis` source, trust ~0.40
`AppDerived`, the `ChessAnalyze` analog):
- compile/test outcome per commit-tree → the **referee** ({Loss/Draw/Win} =
  fails / warns / clean), bit-identical to chess `PlyOutcome`.
- ahead/behind/deviation classification, diff semantics, fork-vs-branch verdict.
- "what was being accomplished / did it succeed / where did it fall short" —
  inference over the rated DAG, never stored raw.

Attest each fact once, at the source that asserts it ([[decomposer-contract]]).
The commit message is witnessed by `GitObjectDb` (the repo said it); the compile
outcome is witnessed by `CodeAnalysis` (we computed it). Their divergence —
"message says 'fixed', compile says Loss" — is itself signal.

## 3. His questions → native reads (no comparison code)

- **"Committed vs on-disk, in sync?"** → content set witnessed by `WorkingTree`
  minus content reachable from `HEAD`'s tree. Empty = clean.
- **"Did they ever commit their latest work?"** → does every `WorkingTree` blob
  appear in some commit's tree? Missing ones = uncommitted work.
- **"What is ahead?"** → commits reachable from the local-`HEAD` ref not
  reachable from any remote ref. DAG reachability over parent edges, refs as
  roots. (`astar_path.c` / reachability already native.)
- **"Deviation / feature branch?"** → DAG topology: shared ancestor, divergent
  descendants; a **branch** is a divergent tip witnessed by the *same* repo's ref.
- **"A fork?"** → a divergent subtree witnessed by a *different source identity*
  (a different `origin`). Provenance is what separates fork from branch.
- **"What is new work?"** → content with **witness_count = 1** (one copy, one
  source), wide RD. This is `model_jitter_catalog` pointed at code: convict the
  singletons, trust the corroborated.

## 4. Cross-repo diff is automatic (the "I've seen this before" property)

Ingest **all** copies into one substrate — 121 dump copies + every GitHub repo.
Identical commits/trees/blobs collide to one entity (the collision IS the mesh,
[[content-is-identity-source-is-witness]]). `witness_count` = how many copies
carry it. **Everything that does NOT collide is the difference**, surfaced by
witness count, ranked automatically. Nobody compares copies pairwise. The
substrate says "seen it / seen it — *wait, this one's new*" by construction:
- high witness → the shared backbone (mainline history all copies agree on)
- single witness → the deviation: the uncommitted experiment, the unpushed
  branch, the local-only fork, the abandoned line.

Best-source-per-project falls out: you don't elect a canonical, you ingest all
and read the union; a GitHub repo being "ahead" of a dump is just its tip commit
carrying a witness the dump's tip doesn't.

## 5. The trajectory = the game (chess parity)

Commit→parent edges are the move sequence. Each commit is a ply:
- **provenance edges** — this commit, this author, this date, this ref/source.
- **aggregating edges** — the content it touched, folding outcome across every
  place that content appears.
- **outcome** (calculated) — does the next commit build on it (confirm), get
  reverted (refute), or does the branch die (abandoned)? Signed Glicko fold, a
  real loss for dead lines — exactly the Gödel-engine requirement that "this
  didn't work" be a first-class rated fact, kept *with* its refutation like
  opening theory keeps refuted lines.

The commit message is the ply annotation (intent, in the author's own words);
the compile referee is the objective ground truth that made chess the proving
domain. Code has both.

## 6. The git-object grammar (packaging stripper) — two-stage unpack

Per CLAUDE.md: **"Tree-sitter's job is narrow: unpack container formats, then
hand off."** Git is a container like GGUF or a safetensors file. It gets its own
**grammar** registered in the same grammar registry `RepoDecomposer` already
uses (`GrammarDecomposer.LookupById(modality)` / `ModalityByExt`). We do NOT
hand-roll a libgit2 black box; we add a grammar that strips git's packaging and
hands the raw product to the pipeline — the format grammar run backward
([[binary-is-encoded-semantics]]).

**Already vendored (verified 2026-07-16)** in `external/tree-sitter-grammars/`,
just not registered in `engine/core/src/grammar_registry.c`:
`tree-sitter-gitcommit` (commit-object/message), `tree-sitter-diff` (patches, for
the analyzer's delta semantics), plus `git-config`/`gitattributes`/`gitignore`/
`git-rebase`. So Stage A is largely *registration*, not authoring. What still
needs a hand-rolled parser: the **binary tree object** (`<mode> <name>\0<20-byte
sha>` — not text, tree-sitter won't touch it) and the loose/pack **codec**. Those
are native ([[layer-allocation-map]]). Net new grammar authoring is small; the
container-unwrap is mostly wiring existing grammars + a native object reader.

**Stage A — git-container grammar (`git-object` modality).** Strips git's
packaging to expose the raw product:
- *Native codec pre-step* (C/C++, the math/bytes layer): inflate loose-object
  zlib framing `<type> <size>\0<payload>`; resolve packfile delta chains
  (OBJ_OFS/REF_DELTA) to full objects. Decompression is native, not grammar
  ([[layer-allocation-map]] — bytes/math in native).
- *Grammar proper*: the inflated commit / tree / tag objects are a simple
  line-oriented format — a tree-sitter grammar (`tree-sitter-git-object`, new)
  parses them into fields: commit → {tree, parents[], author, committer,
  message}; tree → ordered {mode, name, child-sha} entries; tag → {object,
  type, tagger, message}. Refs (`.git/refs/*`, packed-refs, HEAD) parse to
  {name → sha} pointers.
- Output: the **raw product** — blob bytes, tree entry lists, commit fields, ref
  map. Git's SHA framing, zlib, and pack deltas (the *packaging*) are consumed
  and discarded; only meaning proceeds.

**Stage B — hand off to existing grammars.** Each blob's raw bytes are
*themselves* a container (a `.cs`, `.sql`, `.ts` file). They flow into the
existing per-language tree-sitter grammars exactly as working-tree files do
today — same `GrammarComposeRecord` path. Nesting containers is the model:
git-grammar unwraps the repo, language-grammar unwraps each file.

Then **processed into substrate records with attestations** via the normal spine
(`IngestBatchPipeline` → `ConsensusAccumulatingWriter` → COPY). The decomposer
stays a thin valet: strip → records → pipeline; the spine owns
content-addressing, tiering, and the fold ([[decomposer-contract]]).

## 6b. Decomposer delta (implementation shape)

Split, mirroring chess:
- **`RepoRecorder`** (extends today's `RepoDecomposer`):
  1. Stop skipping `.git`. Feed the object DB through the Stage-A `git-object`
     grammar (above).
  2. Witness blobs via the existing content law (a blob's bytes = a file's bytes
     → same entity — this is what makes §1 work). Trees as composed Content.
  3. Commits as `GitCommit` entities: parent trajectory edges, `COMMITS_TREE`
     to root tree, author/message/date provenance.
  4. Witness refs (branch/HEAD/remotes/tags) as source-pointers to commit tips —
     provenance, resolved at ingest, no runtime join ([[decomposer-contract]]).
  5. Emit the working tree under the **`WorkingTree` source**, same content law,
     distinct source id — so identical committed+on-disk blobs share one content
     entity with two witnesses.
- **`RepoAnalyzer`** (new, `CodeAnalysis` source, versioned/evictable):
  compile/test referee per commit-tree, ahead/behind, deviation & fork-vs-branch
  classification. The `ChessAnalyzeDecomposer` analog.

### Entity/relation ledger (mind the reseed law)

New entity types: `GitCommit`, `GitTree` (blob reuses `SourceFile`/Content).

Relations — **reuse first** (adding a relation renumbers highway bits and owes a
reseed; regenerate, never backfill — CLAUDE.md):
- `CONTAINS` — tree→entry, commit→tree. (exists)
- `PRECEDES` / `CONTINUES_TO` — commit DAG trajectory. (exists)
- `APPEARS_IN` — blob→commit/tree occurrence. (exists)
- Candidates that may need adding (each = a reseed): `AUTHORED_BY`
  (commit→author person entity), `COMMITS_TREE` (if `CONTAINS` is too weak to
  distinguish the root-tree pointer), `HAS_COMMIT_MESSAGE` (or route message
  through content as a Sentence/Document tier witness and skip a new relation).
  **Decision owed:** how many new relations are truly needed vs. reused — resolve
  before codegen so exactly one reseed pays for the whole lane.

### Not substrate (app-metadata, write-only) — [[substrate-vs-metadata-boundary]]
Ref *names* as strings, remote URLs, config — HAS_* attestations, not traversed
by inference. The DAG topology and content are substrate; the labels are tags.

## 7. Open questions
1. Relation minimization (§6 ledger) — the one reseed's scope.
2. Person identity: author `Name <email>` → content-addressed person entity;
   cross-repo author merge is a hash collision (same as everything else).
3. Pack files: witness from loose+packed objects uniformly (libgit2 handles;
   hand-parser must inflate packs).
4. Scale: 135 `.git` dirs, ~3.8 GB, many shared commits across copies — dedup by
   construction means the *distinct* object count is far below the sum. Measure
   after first ingest, don't pre-cap ([[no-topk-truncation]]).
5. Working-tree source granularity: one `WorkingTree` source, or per-checkout
   context so multiple dump copies' on-disk states stay distinguishable?
