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
L2 wordnet      synsets + senses + lemma arena (HUB)         StandardsDerived
L3 omw          binds to WordNetSynset IDs (en-filtered)    AcademicCuratedWithUserInput
   verbnet      lemma → wordnet/sense (CORRESPONDS_TO)       AcademicCurated
   propbank     lemma → roleset → verbnet (HAS_SENSE, …)     AcademicCurated
L3 framenet     lemma → frame (EVOKES_FRAME)                 AcademicCurated
L3 semlink      propbank↔verbnet↔framenet alignment          AcademicCurated
   [proof path: tiny-codes, stack, document/image/audio, repos]
   [deferred lexical: conceptnet, atomic2020, ud, wiktionary]
   [proof sandbox: ingest-text.cmd / e2e-full.cmd — db-roundtrip, not seed]
   [deferred usage: tatoeba, opensubtitles]
```

Seed order (see `witness-manifest.json`): **wordnet (synset hub)** → **omw → verbnet → propbank → framenet → semlink** → **tiny-codes → stack → document/image/audio → repos → models** → **[deferred] conceptnet → atomic2020 → ud → wiktionary**. Proof path runs before multi-hour lexical bulk so code/repo/modality claims are testable first. Resume scripts: `seed-resume-prove.cmd` (proof only), `seed-deferred-lexical.cmd` (lexical bulk). Tatoeba/OpenSubtitles deferred unless `LAPLACE_SKIP_USAGE=0`; models deferred unless `LAPLACE_SKIP_MODELS=0`; lexical bulk skippable via `LAPLACE_SKIP_LEXICAL_BULK=1`. **db-roundtrip is not seed** — run `scripts\win\ingest-text.cmd` or `e2e-full.cmd` separately to prove bit-perfect document round-trip.

| skip flag | effect |
|---|---|
| `LAPLACE_SKIP_USAGE=1` | skip tatoeba, opensubtitles (translation-pair usage; default at seed) |
| `LAPLACE_SKIP_MODELS=1` | skip safetensor snapshot deposition (default at seed) |
| `LAPLACE_SKIP_LEXICAL_BULK=1` | skip deferred conceptnet/atomic2020/ud/wiktionary at end of seed |
| `LAPLACE_INGEST_LANGS=en` | default in `env.cmd`; UD without this ingests all ~686 treebanks (~38M sents) instead of 29 `en_*` dialects (~1.1M) |

UD treebank filter: `en_ewt-ud-train.conllu` → base lang `en` → matches `en`/`eng` filter; all English dialect treebanks (`en_gum`, `en_lines`, …) are included.

Safetensor deposition is a **separate witness pass**, not required to prove the invention: lexical + structural + code/document attestations supply converse, generate, and the substrate-side recipe for custom export. Deposit models when you want tensor-role testimony stacked on the same entities — or run `ingest safetensors` later against a seeded DB.

## ETL progress (CI-parseable)

Each ingest emits structured key=value lines (stderr):

- `INGEST_START` — scanned inventory: `unit_type`, `input_units`, `files` (from disk, not output-row guesses)
- `INGEST_PROGRESS` — `input_done/input_total` and `input_pct`, or `files_done/files_total` and `file_pct`; `rows_new` is throughput only, never used for %
- `INGEST_BATCH` — per-commit batch stats
- `INGEST_COMPLETE` — final counts + `status=ok|failed`

Input completion uses `InputUnitsConsumed` on each intent (sentences, synsets, records) where the decomposer reports it; file boundaries use `period-boundary/{file}` markers (UD).

Throughput law: stages run STRICTLY one at a time (a single source gets the whole machine; parallel sources merely split CPU+DB and slow every stage). `INGEST_FROM=<src>` is a manual skip-forward, not a resume journal — none is needed.

## The pipeline (one stage, end to end)

1. **Decompose** — the source's IDecomposer walks its data. Delimited vault corpora (tsv/csv) use `StructuredGrammarIngest`: chunked row parse in `laplace_core`, native `laplace_grammar_compose`, then thin `IGrammarWitness.WalkRow` for semantic attestations — no `line.Split('\t')` on hot paths. Free text still uses TextDecomposer (UAX#29 → tier tree); structured claims use AttestationFactory/TextEntityBuilder.
2. **Dedup** — candidate ids batched through `entities_exist_bitmap` (engine merkle_dedup; LSB-first bitmap) so only novel rows ship.
3. **COPY** — entities + physicalities (trajectories mantissa-packed via the intent_stage COPY-binary builder) bulk-load; attestations upsert by content-addressed id (re-observation bumps count/timestamp).
4. **Accumulate** — each witnessed magnitude becomes a Glicko game (s = ½(1+tanh(m/M)); trust→φ) folded into per-relation period partials in unlogged staging partitions (`LAPLACE_FOLD_WORKERS` parallel sessions, disjoint by relation identity).
5. **Materialize** — `materialize_period_consensus`: batch kernel `accumulate_games(n, Σs)` per relation (bit-identical to per-game replay), upsert into consensus, staging dropped. φ-mixed within a period is a hard exception (invariant, not warning).
6. **Mark** — layer completion attested; counts reported (`substrate_counts`).

Idempotency end-to-end: content addressing + ON CONFLICT. A killed run resumes by re-running; completed work short-circuits. Re-depositing a safetensor snapshot is refused outright (double-counting guard) — reset is per-source eviction or db-fresh, never a bypass.

## Safetensor snapshot deposition (not "model file ingest")

CLI: `laplace ingest safetensors <snapshot-dir>` (`ingest model` is a legacy alias).

| | Safetensor snapshot (ingest) | GGUF (synthesize) |
|---|---|---|
| **Unit** | HF snapshot **directory** | Single **file** |
| **Self-contained** | No — needs `config.json`, `tokenizer.json`, weight blobs | Yes — llama.cpp runs it alone |
| **What happens** | `ModelDecomposer`: recipe → named tensor ETL → Glicko testimony (`AIModelProbe`) | `synthesize substrate <config.json> <out.gguf>` pours arenas into render target |

Validation (`SafetensorSnapshotWitness`): rejects directories missing recipe or tokenizer. Source identity hashes `config.json` + weight files together.

Seed witness set (see `witness-manifest.json`): TinyLlama, Phi-2, Qwen2.5-Coder-3B snapshots under `D:/Models/hub`. Skipped when `LAPLACE_SKIP_MODELS=1` (default seed path for attestation-only proof).

Worker knobs: `LAPLACE_INGEST_WORKERS` / `LAPLACE_DECOMPOSE_WORKERS` / `LAPLACE_FOLD_WORKERS`.

`LAPLACE_INGEST_WORKERS` controls **parallel DB commit** within a source run. It was pinned to 1 because many decomposers emit phased intents (entities first, attestations second) and naive parallel consumers committed out of order → `SubstrateReferentialIntegrityException`. The runner now uses **epoch barriers**: intents carry `CommitEpoch` in metadata; parallel commits are allowed within one epoch, never across epochs. Model ETL, Unicode aliases, and Tatoeba (sentences then links) use epoch 0 then epoch 1. WordNet's five-phase entity/attestation stream implements `IIngestCommitPolicy.StrictSerial` (pipelined decompose+commit overlap only). Default policy when absent: `EpochBarrier`. Measured starting point: 2–4 on a multi-core box after epoch tagging is in place.

## Language scope (full witness stack)

Every witness source in the manifest stays. Language filters control **which languages** multilingual corpora emit — not which witnesses exist. Tatoeba and Wiktionary are usage witnesses; WordNet, VerbNet, PropBank, FrameNet, ConceptNet, and the rest are lexical/structural witnesses. All run through the same machinery.

| control | effect |
|---|---|
| `LAPLACE_INGEST_LANGS=en` | global default; `en`/`eng`/`en-US` resolve via `LanguageReference` |
| `LAPLACE_{SOURCE}_LANGS` | per-source override (`LAPLACE_TATOEBA_LANGS`, `LAPLACE_UD_LANGS`, …) |
| `LAPLACE_EMIT_CROSS_LANG=1` | emit `IS_TRANSLATION_OF` and other cross-language edges when a language filter is active (default off) |
| `laplace ingest <src> --langs en` | CLI override for one run |

Per-source filter law:

| source | filter point |
|---|---|
| **ConceptNet** | both `/c/{lang}/…` endpoints must match |
| **OMW** | skip `wn-data-{lang}.tab` files outside scope |
| **UD** | conllu filename prefix (`en_ewt`, `en_gum`, … when `en` specified) |
| **Tatoeba** | `sentences.csv` lang column; translation links only when both sentence ids were ingested |
| **Wiktionary** | `lang_code` on each record; translations gated on `EmitCrossLanguageLinks`; prefers `kaikki.org-dictionary-English.jsonl` when scoped |
| **OpenSubtitles** | zip pair stem (`en-es`, `de-en`, …) — ingest when either side matches |

English-only sources (WordNet, VerbNet, PropBank, FrameNet, SemLink, Atomic2020) pass through unchanged. `seed-substrate.cmd` sets `LAPLACE_INGEST_LANGS=en` by default.

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
