# Ingestion

Ingestion IS training. This document is the operational law of how witnesses enter the substrate, and the measured record proving the identity.

## The identity, made operational

Gradient descent and attestation both consume corpora and accumulate sequence/co-occurrence/transformation structure into arena-shaped state. One does it by nudging anonymous floats; this system does it by witnessing explicit relations, adjudicated and attributed. The translation table is law:

| conventional | substrate |
|---|---|
| training run | `ingest <source>` — deterministic, resumable, minutes-scale |
| epoch | re-ingest = idempotent no-op (never overfitting) |
| curriculum | ladder order (layer gates) |
| learning rate | trust class → φ |
| checkpoint | the database (reproducible from sources) |
| fine-tuning | more witnesses |
| catastrophic forgetting | impossible — outvoted, never overwritten |
| knowledge cutoff | `max(last_observed_at)` per source |
| RLHF/alignment | trust-class policy |
| unlearning | per-source eviction (consensus-state paths: OPEN-PROBLEMS §3) |
| train/serve fleets | one database, MVCC-concurrent (measured under load) |

## The ladder

Dependency-ordered seed ingestion; each source's IngestRunner refuses to start until lower layers carry `HasLayerCompleted` markers:

```
L0 unicode      T0 atoms + UCD properties + byte tier        StandardsDerived
L1 iso639       languages/scripts/macrolanguage              StandardsDerived
L2 wordnet      synset/lemma/sense lexicon                   AcademicCurated
   ud           treebanks (POS/lemma/features/deps)          AcademicCurated
   verbnet/propbank  predicate-argument semantics            AcademicCurated
   tatoeba      sentence translations                        StructuredCorpus
   atomic2020   inferential commonsense                      AcademicCurated
   conceptnet   commonsense graph                            AcademicCuratedWithUserInput
   wiktionary   dictionary                                   StructuredCorpus
   opensubtitles 601M aligned pairs (day-scale)              StructuredCorpus
L3 omw          multilingual wordnet (binds L2 to languages) AcademicCuratedWithUserInput
   framenet     frame semantics                              AcademicCurated
   semlink      cross-resource alignment (needs vn+pb+fn)    AcademicCurated
```

Throughput law: stages run STRICTLY one at a time (a single source gets the whole machine; parallel sources merely split CPU+DB and slow every stage). `INGEST_FROM=<src>` is a manual skip-forward, not a resume journal — none is needed.

## The pipeline (one stage, end to end)

1. **Decompose** — the source's IDecomposer walks its data; text content goes through the engine TextDecomposer (UAX#29 segmentation → tier tree), structured claims through AttestationFactory/TextEntityBuilder.
2. **Dedup** — candidate ids batched through `entities_exist_bitmap` (engine merkle_dedup; LSB-first bitmap) so only novel rows ship.
3. **COPY** — entities + physicalities (trajectories mantissa-packed via the intent_stage COPY-binary builder) bulk-load; attestations upsert by content-addressed id (re-observation bumps count/timestamp).
4. **Accumulate** — each witnessed magnitude becomes a Glicko game (s = ½(1+tanh(m/M)); trust→φ) folded into per-relation period partials in unlogged staging partitions (`LAPLACE_FOLD_WORKERS` parallel sessions, disjoint by relation identity).
5. **Materialize** — `materialize_period_consensus`: batch kernel `accumulate_games(n, Σs)` per relation (bit-identical to per-game replay), upsert into consensus, staging dropped. φ-mixed within a period is a hard exception (invariant, not warning).
6. **Mark** — layer completion attested; counts reported (`substrate_counts`).

Idempotency end-to-end: content addressing + ON CONFLICT. A killed run resumes by re-running; completed work short-circuits. Re-ingesting a MODEL is refused outright (double-counting guard) — reset is per-source eviction or db-fresh, never a bypass.

Worker knobs: `LAPLACE_INGEST_WORKERS` / `LAPLACE_DECOMPOSE_WORKERS` / `LAPLACE_FOLD_WORKERS` (measured tuning: 2/2/4 on the 6-core Linux runner; 4/4 on Windows 24-core ingest jobs).

## The document path (books, prompts, any text)

`Laplace.Cli db-roundtrip <file>` — the full ceremony per document:
1. **Record**: TextDecomposer → tier tree (doc→sentences→words→T0), entities + CONTENT physicalities + trajectories via the writer (durable `laplace.*` rows; pg_temp appears only as COPY staging transit).
2. **Attest**: adjacency bigrams under `PRECEDES`, source `substrate/source/UserPrompt/v1` (context_id stamping with the document entity = open work; until then per-author conditioning derives from trajectories).
3. **Fold**: period consensus materialized immediately (the document's sequence statistics become adjudicated arena state — visibly, e.g. Alice → 13,281 relations).
4. **Prove**: reconstruct the document FROM the database and byte-compare. Pass line: `BIT-PERFECT FROM DATABASE`.

This is the storage/learning duality in one command: perfect archive AND semantic deposition, deduplicated against the whole substrate (repetition becomes witness_count, not bytes).

Wrapper: `scripts\win\ingest-text.cmd <files...>` (sidecar-built CLI to dodge binary locks from concurrent runners).

## Conversation ingestion (infinite context)

Design: every prompt (and reply) is a document-tier deposition under `UserPrompt/v1` — context as biography, recall at any age = one indexed descent. Current state: `converse_turns` (UNLOGGED) is the session cursor; durable turn attestation is open wiring (OPEN-PROBLEMS §4). The trust class and the path both exist; the call is one emitter hookup.

## Measured record (2026-06-07, Windows, i9-14900KS, NO GPU, concurrent with serving and desktop use)

Cold start: `createdb` 01:15:04 → WordNet-complete (converse-capable) 01:19:59 = **4 m 54.86 s**.

| source | evidence rows | wall | rate |
|---|---|---|---|
| unicode | 873,823 | 1:41 | 8.7 k/s |
| iso639 | 34,424 | 0:02 | 17 k/s |
| wordnet | 1,277,341 | 1:36 | **13.3 k/s** |
| omw | 3,648,432 | 5:34 | 10.9 k/s |
| ud | 33,342,802 | 33:18 | **16.7 k/s** |

Documents (db-roundtrip, fresh): Alice (26,543 words ≈ 35 k tokens): record 1.8 s + fold ~1.2 s ≈ **12 k tok/s all-in / ~19.6 k tok/s record-phase**; library pass sustained 40–70 k tok/s on larger books (second-pass dedup tier). 16/16 BIT-PERFECT (incl. a .py and a .json — the text law doesn't care what the text means).

End-of-session substrate: **9,043,015 entities · 50,313,627 attestations · 6,028,704 consensus relations · 58,652 document-tier entities** — all rates measured UNDER concurrent ingest/serving ("benchmarked under YouTube"), on the slow tier (see OPEN-PROBLEMS §11 headroom).

Context for the rates: single-device GPU pretraining ingests ~10–25 k tok/s — this CPU matched it while ALSO doing identity, dedup, placement, attribution, adjudication, and archival per token.

## The flip (runtime learning protocol)

T0: prove ignorance — closed-world zero over the target relation (7.2 ms).
T1: one-sentence document through db-roundtrip (~1 s actual; first run pays sidecar build).
T2: prove knowledge — evidence rows with witness name and timestamp; consensus updated at one-witness humility.
First performed 02:35:51.07, "Ahab admired Darcy beyond all reason." → `Ahab→admired`, `admired→Darcy` under UserPrompt/v1. NOTE the adjacency law: PRECEDES is bigram adjacency — probe learned pairs, not skip-grams.
