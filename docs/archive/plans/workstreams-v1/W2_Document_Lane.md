> Archived workstream analysis. Historical evidence only; GitHub owns status.

# W2 — Finish the document lane (Pillar 0)

**Issue:** #754 · **Plan:** `COMPLETION_PLAN.md` R4 / Phase 4 · **Blocks:** #761
(corpus seeding), all encyclopedic answering · **Related:** #596, #418, #660,
#574

---

## 1. Why this exists

Books are in the substrate and cannot be named. `containers_of(word_id('gambit'),
2, 500)` returns 59 tier-3 sentences and 12 tier-4 documents — one of them the
Project Gutenberg Paul Morphy chess book — but naming those twelve requires
rendering each document whole, which exceeds the statement timeout. There is no
title edge, no author, no license, and no source identity of their own: the lane
stamps `UserPrompt`, the **conversational** trust class, on Britannica.

This is not archaeology. It is a **deliberate content-only posture (Pillar 3a)
with an unfinished Pillar 0**, tracked in the tree at
`.scratchpad/session-tasks.md:32` and GH #432.

## 2. How it works today

### 2.1 The path a book takes

```
scripts/ingest-source.sh document [path]        :85-98  (default $DATA_ROOT/test-data/text)
  laplace ingest document <path>
    IngestDispatchTable.cs:88 → IngestCommands.IngestDocumentAsync   :381-399
      IngestViaRunnerAsync(Resolve("document"), skipLayerCheck: true)
DocumentDecomposer                              app/Laplace.Substrate/Abstractions/DocumentDecomposer.cs
  SourceId     => UserPromptContent.Source      :9-13   ← the conversational source
  SourceName   => "UserPrompt"
  TrustClassId => UserPromptContent.TrustClass
  SourceTrust  => Associative × 0.30            WitnessConstants.cs:36
  InitializeAsync → UserPromptContent.BuildBootstrapChange()   :22-23
                    (5 entities, 0 attestations, 0 relation declarations,
                     0 canonical names — UserPromptContent.cs:21-30)
  file enumeration: *.txt only, recursive, vendored-filtered   :89-92
  label: document/<relpath>                                    :30-36
  per file: whole bytes (2 GiB cap) → ContentTierSpine.ResolveRoot(bytes)
            becomes the record's per-file source id            :104-108
            + FileMetadata.FromPath (name/relpath/size/mtime)
IngestExistenceGate    marker-complete files true-skip          :41-45,65-71
DocumentIngestHandler  delegates content DAG to ContentIngestHandler
                                                DocumentIngestAdapter.cs:36-83
  → ContentDeferredUnit → ContentTierSpine.EmitTree            ContentTierSpine.cs:93-99
  → IntentStage.EmitContentTree                                IntentStage.cs:295-325
  → native content_witness_emit_tree
```

### 2.2 What it emits, and what it deletes

Native `emit_node` (`engine/core/src/content_witness_batch.c:276-336`):

- `intent_stage_add_entity(node.id, tier, laplace_content_tier_type_id(tier), source_id)` — `:290`
- `intent_stage_add_physicality(… traj, n_traj …)` — `:329-332`, trajectory built when `child_count > 1` (`:301-323`)
- **zero `intent_stage_add_attestation` calls in the entire file** (`grep -n "attest"` → no hits)

Tier 4 maps to the `Document` type (`TextEntityBuilder.cs:125-132`). The
attestation suppression is explicit and reasoned:

> `TextEntityBuilder.cs:241-248` — *"Pillar 3a: text emits its content DAG
> (entities + physicalities/trajectory) ONLY. … Jamming word->word PRECEDES +
> CONTAINS onto text was the error … Deleted."* with
> `attestations = ImmutableArray<AttestationRow>.Empty`

`ContentIngestHandler.WalkWitness` is an empty body (`ContentIngestAdapter.cs:29-31`).

### 2.3 The exactly-two attestations per file

`DocumentIngestHandler.WalkWitness` (`DocumentIngestAdapter.cs:58-82`):

1. `LayerCompletion.EmitFileMarker` — one `HasLayerCompleted/2` (`LayerCompletion.cs:24-32`)
2. `FileEntity.EmitMetadata` — one `HasFileMetadata` on the metadata-DAG root (`FileEntity.cs:72-83`)

Both carry `source_id = fileRoot`, **not** the UserPrompt source — which is why a
query scoped to `source_id('UserPrompt')` shows ~zero evidence against millions
of entities. Pinned by `DocumentIngestPipelineTests.cs:78-80,145-155`
(`NonMarkerAttestationCount == 0`, `MarkerAttestationCount == records.Count`).

Both types are deliberately off-manifest: *"minted inline with its meta-type
entity, never in `relation_types.toml`, never a highway bit, excluded from the
consensus fold"* (`FileEntity.cs:59-61`) — which is how the lane satisfies the
"declare every relation you emit" gate while declaring none.

### 2.4 CI enforces the current shape

`scripts/decomposer-gates.json:45-51` marks `document` `"content_only": true`
with `"consensus_gates": []`, and `scripts/decomposer-gate-check.py:91-114`
**fails the build if the document lane emits any non-marker attestation.**

**A naive fix breaks CI.** The gate must be amended in the same change.

### 2.5 Everything else that is missing

| Gap | Cited absence |
|---|---|
| No names for any document entity | `Decomposer.cs:219` base `CanonicalNamesForReadback` returns empty; `DocumentDecomposer` never overrides it. One name is registered per run: `substrate/source/UserPrompt/v1` (`IngestCommands.cs:741-749`) |
| No source identity attested | `UserPromptContent.cs:21-30` hand-builds bootstrap, bypassing `BootstrapIntentBuilder`'s `HAS_NAME_ALIAS` (`:32-41`) and `HAS_TRUST_CLASS` (`:81-95`) |
| No license/attribution/citation | no `ISeedSource`/`ISourceManifest` for documents → `SourceVocabularyBootstrap.DepositLicenseAsync` (`:128-175`) never runs |
| No `HAS_TITLE` relation exists | `grep HAS_TITLE relation_types.toml` → only `HAS_TITLECASE_MAPPING` (`:720`) |
| Lane invisible to generated inventory | `scripts/docs-inventory.py:83` scans only `app/Laplace.Decomposers`; `DocumentDecomposer` lives in `app/Laplace.Substrate/Abstractions/` |
| Lane invisible to modality counts | GH #660 |
| `.md`/`.rst`/`.html` no-op | `DocumentDecomposer.cs:89` is `*.txt` only; GH #418 (*"the T-SQL docs ingest was a NO-OP because .md went through document(UAX29)"*) |
| One bad file kills the run | GH #596 (`laplace_content_root_id rc=-5`), priority:high |
| Format router written, never called | `DocumentRouter.cs:15` — only caller is its own test |
| Idempotency check vacuous for this lane | `_ingest.yml:102-148` compares `evidence_count(source_id('UserPrompt'))` before/after — comparing 1 to 1 |

## 3. How it should work

A document is a witness like any other source. It should enter with:

- **its own source identity** — `substrate/source/Document/v1` (or per-corpus:
  Gutenberg, Britannica), not the conversational `UserPrompt` class. A book is
  not a user utterance and must not compete in the same trust band;
- **a name**, so `canonical_names` can answer "what is this document" without
  rendering the document (`realize_batch.c:103` joins that table);
- **trunk-grain typed edges** — title, author, edition, license, source URL —
  attested **on the document root**, O(1) per file. This does not reopen
  Pillar 3a: 3a forbids per-node distributional edges (word→word PRECEDES), not
  facts about the document itself;
- **content and trajectory exactly as today.** The content DAG is correct and
  is the thing that makes `containers_of` work. Do not touch it.

Design rule to preserve: **identity is content**, so finishing the lane does not
re-mint anything. Already-ingested books keep their ids; the new work only adds
testimony about them. A re-ingest is safe for rows and **doubles observation
counts by design** — so the marker guard must stay (`CLAUDE.md`, Writes).

## 4. What to consider

| Decision | Options | Notes |
|---|---|---|
| Source identity granularity | one `Document` source vs per-corpus sources | Per-corpus is better epistemics — Britannica 1911 and a scraped forum dump should not share a trust class — and costs one manifest row each. Check `SourceVocabularyBootstrap.RegisterManifestAsync` (`:108-126`) for the pattern; `RepoSource.cs:6-25` is the cleanest example (`SourceId`, `SourceName`, `TrustClass`, `Relations`, `TypeNodeNames`, `License`). |
| Where titles come from | Gutenberg headers (structured), filename, first line | Gutenberg headers are parseable and reliable; filename is a fallback, not a source of truth. `FileMetadata` (`FileEntity.cs:14-33`) is explicitly designed to be extended with format-native metadata. |
| `HAS_TITLE` vs reuse `HAS_NAME_ALIAS` | new relation vs existing | A title is not an alias — an alias implies co-reference. Append `HAS_TITLE` with an explicit `bit = N`; append-only, **no reseed owed** (ADR 0001). Also consider `AUTHORED_BY` (`:1813`) and `HAS_LICENSE` (`:1691`), which already exist. |
| Gate amendment | loosen to bounded vs remove | Replace `content_only: true` with per-node = 0 / trunk-grain > 0. Removing the gate loses the protection that caught the original Pillar-3a error. |
| Intake breadth | fix router now or later | `.md` silently no-opping (#418) means whole corpora *appear* ingested and are not. This is the same class of error as "documents were never ingested" — it should be fixed with the lane, not after. |
| Malformed files | skip vs abort | Skip with a logged, counted failure. #596 currently aborts a 199-file run on one bad encoding. An ingest that dies on file 5 of 199 and reports success-shaped output is worse than one that skips loudly. |

## 5. Where to look

| Concern | File |
|---|---|
| Decomposer + source stamp | `app/Laplace.Substrate/Abstractions/DocumentDecomposer.cs:9-13,22-23,89-108` |
| The two markers | `app/Laplace.Substrate/Abstractions/DocumentIngestAdapter.cs:58-82` |
| Attestation suppression + rationale | `app/Laplace.Substrate/Abstractions/TextEntityBuilder.cs:241-248` |
| Native emit (no attestations) | `engine/core/src/content_witness_batch.c:276-336` |
| The bootstrap path to copy | `app/Laplace.Substrate/Abstractions/SourceVocabularyBootstrap.cs:108-175`, `BootstrapIntentBuilder.cs:32-95` |
| Canonical-name registration | `app/Laplace.Substrate/Crud/Npgsql/NpgsqlCanonicalRegistry.cs:7`, `IngestCommands.cs:741-749` |
| A finished lane for reference | `app/Laplace.Decomposers/Code/RepoSource.cs:6-25` |
| The CI gate to amend | `scripts/decomposer-gates.json:45-51`, `scripts/decomposer-gate-check.py:91-114` |
| Tests pinning current behavior | `app/Laplace.Substrate.Tests/Abstractions/DocumentIngestPipelineTests.cs:78-80,145-155` |
| Workflow | `.github/workflows/seed-documents.yml:33-40` → `_ingest.yml:96-166` |

## 6. Acceptance

1. `containers_of(word_id('gambit'), 2, 500)` tier-4 rows resolve to **titles**
   via `canonical_names`, in one batched read, under the statement timeout.
2. A document carries attested source identity, license, and attribution;
   `source_roster` for that source returns document facts (requires #760).
3. The amended gate is green: per-node attestations 0, trunk-grain > 0.
4. A deliberately malformed file is **skipped and counted**, and the run
   completes (#596).
5. A `.md` file produces grammar-container content rather than a silent no-op
   (#418).
6. `docs/INVENTORY.md` lists the document decomposer (scan fix).
7. Re-ingesting the same corpus does not double trunk attestations (marker
   guard holds).

## 7. Risks

- **Trust-class inflation.** Giving documents their own source is correct; give
  them a *higher* trust weight than curated lexical sources and the fold will
  start preferring Britannica prose over WordNet structure. Set it deliberately
  and record why.
- **Re-ingest cost.** Adding trunk attestations to already-ingested books needs
  a re-run of the corpus. Rows are idempotent; testimony is not — the marker
  guard is what keeps observation counts honest.
- **Scope creep into Pillar 3a.** The temptation is to also emit per-sentence
  structure. That is the error the tree already deleted once
  (`TextEntityBuilder.cs:241-248`). Trunk-grain only.
