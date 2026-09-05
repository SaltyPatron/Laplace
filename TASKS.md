## Recovery implementation — current evidence, 2026-09-05

- Worktree `/tmp/laplace-content-recovery`, existing PR #1496. Not yet on main or
  installed in original production. Historical checkpoints below are superseded.
- Native full-source composition preserves exact Python bytes, grammar structure,
  dynamic tier floors, and RLE flags. File trunk owns content and identity metadata;
  path/bytes/mtime observations persist separately. Generic CodeDecomposer worker tested.
- Native constituent closure replaces recursive SQL. Florence: 8,420 edges, zero
  differences; HTTP exact 15,119-byte reads after DB reload 856/176/192 ms.
- Shared journaled writer now commits evidence, consensus fold, and replay receipt in
  one transaction. Native semantic digest covers admitted payload, independent of row
  order/stage partition/clocks. Injected merge/fold failures roll back; retry/replay tested.
- Fresh public upload repeated across process restart: evidence and consensus unchanged;
  only file observations advance. Verified legacy bootstrap can receive a reconciled
  receipt without recounting; partial/mismatched/artifact history returns 409 unchanged.
- Morse source from `/vault/Data/test-data/electronics/international-morse-code.txt`:
  exact 2,594-byte HTTP POST/GET, file db25a4eb16c82b3c5d81e505b32b9902 has two children,
  existing content ef5e41c6013c481cdf2af28096678cf5 and metadata
  da8bba849715df83965c2e822a2360b9. Original database remains untouched.
- OMW 2.0: all 32 lexicons parsed and inventoried against raw XML (570 MB), 18 tests
  passed. Italian artifact set was ingested into retained recovery DB; dependency omw-en
  remains unresolved. Canonical relation alias/direction fixes now under verification;
  prior row-count proof does not establish corrected relation semantics.
- Reverse native containment now selects indexed physical candidates before entity
  hydration and retains its typed SPI plan. Morse content→file: 70.38 ms cold,
  8.83 ms warm / 1,806 buffers, versus 563 ms / 1.15M buffers; exact parent verified.
  Two-hop Morse word traversal: four containers, 24.24 ms. Cycle test also passed.
- Native logical trajectory equivalence replaces per-row SQL expansion in historical
  reconciliation. Disjoint historical artifact cells are accepted without recounting;
  overlapping/incomplete/mismatched bootstrap evidence remains a nonmutating conflict.
- Gutenberg format metadata and shared work composition implemented; actual 195-file
  inventory and 28 document tests passed. Final recognition/native-normalization review
  and retained generic-worker ingestion are active, not delivered.
- Existing SafeTensors codec repaired for exact scalar/empty/Unicode-name output,
  bounded export memory, and write-error propagation. Native 3/3 and independent
  safetensors.numpy readback passed. Full model export route remains unconnected.
- Model work active: replace checkpoint-only hash attestations with native ordered
  structural trajectories and inspect real MiniLM tensor ingestion. No model ingestion,
  export, code generation, or full conversation delivery claimed.
- Retained runtime: localhost:18081, laplace_recovery_demo on isolated socket
  `/tmp/laplace-content-pg/socket`; hosted services skipped. Evidence files under
  `/tmp/laplace-content-recovery-proof`.
- Acceptance: combined managed/database 27/27; native closure affected DB 7/7;
  native core 43/43; intent stage 23/23; atomic writer 2/2; bootstrap reconciliation
  3/3; singleton replay compatibility 3/3. Full integration/CI/deployment outstanding.

# Active recovery — 2026-09-05

The acceptance boundary is the connected Laplace product defined by the invention,
current inventor corrections, and both repositories' applicable issue history.
Component results and the historical notes below are not current delivery claims.

| Work | Existing owners | Current obligation / demonstrable result |
|---|---|---|
| Reconcile invention and implementation | Laplace invention/specs; Refactor #184 | Active. Resolve documented answers before asking the inventor; distinguish superseded descriptions from current requirements. |
| Production digital-content ingestion | Laplace #1049, #799, #802, #806, #1010; PR #1496; Refactor #53, #57, #195 | Active implementation on PR #1496. Finish one shared file/content/metadata/provenance route, dynamic grammar-fed tier floors, exact reconstruction and bidirectional traversal. Preserve existing reusable content. |
| Selected source estate and ETL | Laplace #1403, #967, #1153; Refactor #171, #195, #223 | Reconcile selected releases/artifacts in /vault/Data and /vault/models; shared native composition and bulk apply; complete artifact dispositions and metadata; physical batching must not change identity. |
| Standing and evidence | Laplace #1303, #1321; Refactor #16, #110 | Preserve observations, derivations, independent evidence and contextual standing as distinct state; demonstrate actual outcome-driven updates through their consumers. |
| Cognition and language realization | Laplace #1401, #1478, #921; Refactor #17, #18, #60, #132, #182 | Whole-input interpretation through shared typed native operations to a completed semantic act and language realization; no rank-one topic, regex, substring or template substitute. |
| Gödel / procedural learning | Refactor #19, #169, #218, #221, #222 | Persistent successes/failures, validated skills, contextual habits and equivalent acceleration; demonstrate changed subsequent execution without self-corroboration. |
| Optional model ingestion and model synthesis | Laplace #927, #928; Refactor #20, #61, #71, #129, #223 | Exact reusable model structure and calibrated behavioral evidence; same substrate operators drive scoped target generation; demonstrate external-runtime behavior, not merely a loadable file. |
| Unified product navigation / UX | Laplace #1404; Refactor #21, #68 | Deferred presentation work per inventor. Shared search/rank/entity/structure/evidence traversal; Hikaru player-name resolution and king/substring distinction remain required. |
| Integration and installed behavior | Existing owning PRs; Refactor #22, #23 | Preserve valid work, avoid overlapping PRs, verify actual installed native/public behavior. No destructive reseed inferred from stale notes. |

## Verified baseline and limits

- Original database is populated. SQL, MCP and HTTP reproduced the same defective
  continuation for “The opposite of hot is”; callable adapters do not prove cognition.
- International Morse Code has canonical content root
  `ef5e41c6013c481cdf2af28096678cf5`, currently stored at floor 4. Native-backed SQL
  reconstruction reproduced the source's 2,594 bytes exactly; a sentence child
  ascended to that root. This does **not** establish a proper file entity trunk.
- Current `FileEntity.SourceId()` aliases content identity; metadata is attached by
  a relation. PR #1496 owns the unfinished content/file/document/work correction.
- Ingest journal `file_id` is a separate resume fingerprint, not a navigable content
  identity. Journal completion and staged counts cannot certify artifact structure.
- Tier is compositional floor, never a universal category number. “Hello” does not
  gain another content identity merely by serving as a title or document. Actual
  multi-child compositions, including a file's content and metadata, have their own
  identities. Segmentation/roles are modality- and grammar-specific.
- Refactor's existing interrupted rebase is preserved; its deployment state does
  not establish the state of the original populated database.

## Current bounded assignments

- Primary: integrated artifact callers, endpoint integration, review and combined acceptance.
- `content_contract` (Sol/high): authority review complete; real HTTP/database tenant
  occurrence checks passed (2/2). Public IDs are canonical lowercase hex; POST → GET uses
  the returned ID. File → [content, metadata] and content → file assertions passed.
- `native_content_path` (Terra/high): generic ordered native composition/staging
  implemented; six focused native checks and managed builds passed.
- `artifact_pr_review` (Sol/high): native-backed canonical reconstruction implemented;
  three isolated PostgreSQL checks passed against the final static SQL definition.

Implementation is in `/tmp/laplace-content-recovery`, extending the existing PR branch.
These local results are not merged, installed, or delivered product behavior.

## Integration findings — not delivery claims

- The existing PR branch is available in `/tmp/laplace-content-recovery`, based on
  `1e5d628b44820b6dafcec6235a65e42faf17dcca`; the original working tree and the
  Refactor rebase remain undisturbed.
- The original PR `DocumentEntity.Resolve` bypassed `hash_composer_compose_node` and hashed a
  singleton through raw `Hash128.Merkle`, reminting a wrapper. The generic native
  composer already collapses singletons. Its fixed document category/floor and
  tests requiring content/document inequality must be reconciled together.
- The original PR `FileEntity.Emit` used a fixed document floor and Karcher mean instead of
  deriving the parent floor and consuming the canonical composition calculation.
  The correction belongs to the shared native composition boundary.
- Recipe-declared name/path may participate in the file's metadata composition
  under #1049 without salting the underlying content identity. Mtime/transfer size
  remain observational. No new inventor decision is needed on this distinction.
- #799 already defines canonical work descriptor content from title/author;
  attributed claims and referential interpretation remain separate. Do not reopen
  this as an unanswered invention question without conflicting higher authority.
- Preserve useful native text/grammar compose, bulk drain, trajectory decode and
  batched containment implementations. Verify existing bindings before adding an
  ABI; do not create another file-specific composition engine.
- Implemented locally: removed the plain-text document reminting wrapper; native
  ordered composition now derives file floor, identity, coordinates and trajectory.
  Five artifact identity/staging checks passed, including the singleton “Hello”.
- Artifact export checks the tenant's recorded admission, not global
  `first_observed_by`. An isolated HTTP/database check confirms that two admitting
  tenants share one artifact while an unrelated tenant cannot export it.
- Reconstruction uses the existing native renderer through fixed SQL and verifies
  canonical identity before returning UTF-8. Three database checks passed for
  normalization, missing constituents and cycles; the endpoint builds cleanly.
- Export requires a confirmed tenant occurrence, SourceFile type, exactly two
  constituents and a matching recomputed file identity. Prompt contexts require
  confirmed outcomes. Isolated actual-writer tests reject false-type membership,
  refuted file membership and refuted prompt membership with HTTP 404.
- Metadata identity now uses fixed-field escaped JSON: embedded newlines cannot
  alias distinct name/path pairs. Both pairs round-trip in the collision regression.
  Admission preserves leading/trailing name/path characters instead of trimming them.
- Existing reusable structured path confirmed: `laplace_grammar_compose` produces
  native roots/tiers/trajectories; `laplace_compose_drain_into_stage` is its bulk drain.
  `GrammarRowComposer` already owns that result and borrowed containment tree. The
  next content integration is a root-component accessor plus FileEntity overload
  accepting that prepared root, rather than another format-specific file engine.
- Remaining: original encoding/BOM/format-byte reconstruction; reusable modality/
  format grammars including paragraph/page/section structure; observational metadata
  persistence; multi-document artifact envelopes; complete physical source inventory
  and coverage; shared prepared composition to eliminate repeated tree construction;
  flag-preserving trajectory RLE; installed integration and operator measurements.
  The current ordered native trajectory preserves repetitions and atom flags exactly,
  but its flagged codec does not yet provide run-length encoding.

---

# Historical reconstructed task state — 2026-08-24

The following is retained as historical evidence. Its database counts, branch/CI
state, priority declarations and assertion that the seed must be discarded are not
current findings or authorization.

Status vocabulary: DONE = landed AND verified against the running artifact.
PARTIAL = landed, some part unverified. OPEN = not started or not finished.

## The four original tasks (from session start, 2026-08-22 21:03)

| # | Task | Status | Evidence / what is missing |
|---|---|---|---|
| 1 | Universal agent-log decomposer — every provider, batched, parallel, no reinvented wheels | **OPEN** | Landed at `34195736`. **Never run through a single ingest.** No journal row, no gate, no verification of any kind. |
| 2 | Get CI green | **OPEN** | 4 blockers found and fixed tonight (#1322, #1323, #1326, #1327). Last main run still **failed** on `Eval — generation / election correctness`, `election 1/6 exact`. Root cause identified (#1328) but the seed that would clear it is unfinished. |
| 3 | Recover lost work | **OPEN** | Orphan count corrected from 400 → 98 candidates across 63 branches, then shown to over-report. **5 verified, 1 landed** (#1325). **189 SQL functions still string-bodied** that orphaned `3999680f` converted to BEGIN ATOMIC — aborted, 34 conflicts. |
| 4 | Does Laplace converse properly | **ANSWERED, NOT FIXED** | No. Proven live: three-turn water-cycle test returns isolated dictionary glosses, no carried topic, no chain. Non-English returns `null`. Root cause is #1321 + unseeded knowledge layer. |

## Landed tonight

| PR | What | Verified? |
|---|---|---|
| #1322 | Symmetric relations reachable from only one endpoint — `generate_walk.c`, `prompt_coherence.c`, `consensus.gaps` | **PARTIAL.** `generate_walk` proven RED→GREEN live on `avant/après J.-C.`, 26/26 regress. `prompt_coherence` rel_mass and `consensus.gaps` **never measured** — shipped on "it compiled". |
| #1323 | `rating_spread` gate premise: constant is forced when `max(witness_count) <= 1` | **UNPROVEN.** atomic2020 gate has not re-run. |
| #1324 | ARCHITECTURE: entities hash-sharded at tiers 0, 2, 3 | DONE. Verified against `ops.partition_pressure` — 27 partitions. |
| #1325 | Recovered joint-edge election + `LAPLACE_XYZM_MAX_POINTS` allocator guard | PARTIAL. Broke main; repaired by #1326. |
| #1326 | Two recovered SPI plans were planned serial | DONE. `SpiParallelPlanGateTests` 2/2. |
| #1327 | 3 db-tier classes raced `ContentLadderLedger` static state | DONE. 797/798 with db tier enabled. |

## Open defects filed, not fixed

- **#1321 — the fold has no opponent.** `consensus_fold_math.h:38` and three sites in `fold_route.c` pass `CONSENSUS_FOLD_NEUTRAL_MU` as the opponent rating on every fold. Every rating in the substrate is a witness counter. Simulation reproduces the live distribution to 0.4%. **This is the core defect. Nothing else matters until it is fixed.**
- **#1328 — cancelled ingests fold partial evidence.** Wiktionary 12,076,118 / UD 181,814 / OMW 2,204,303 attestations from truncated corpora, `evidence_persisted = t`. Cancellation is not transactional at the semantic level.
- **#1303** — source trust asserted by literals, never earned. Measurement added: one extra witness ≈ 690× the entire trust ladder.

## Started and abandoned tonight

- **Fold opponent fix** (`fix/fold-real-opponent`): engine core edited only —
  `laplace_attestation_witness_opponent_rating()` + staged struct field. Does
  nothing on its own. Still needs C# marshalling, `attestations.opponent_rating_fp1e9`,
  three `fold_route.c` sites, `consensus_fold_math.h`, rebuild, install, regress,
  and a full reseed.
- **BEGIN ATOMIC recovery** (189 functions): cherry-pick applies 167 files clean,
  34 conflicts. Aborted.

## Database state

- Recreated empty 2026-08-24 06:24 (`db-ops recreate`).
- Foundation ladder seeded: 10/10 sources ok, 1595s. 3,650,808 entities /
  7,490,474 attestations / 7,099,321 consensus.
- Documents: seeded.
- Knowledge / code / models: **never seeded.**
- **The entire seed is worthless** — it encodes the constant-opponent fold. It
  must be discarded and redone after #1321.

## Left on disk

- `laplace_d_iso639`, `laplace_d_propbank`, `laplace_d_unicode` — ~9 GB of
  isolate DBs from before tonight.
