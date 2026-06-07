# Receipts

The measured record. Conditions apply to EVERYTHING below: Windows 11, i9-14900KS, NO GPU in any path, stock MSVC-built EDB PostgreSQL 18.1, the SLOW TIER (interpreted SQL, scalar per-row kernel calls, no SPI offload for the new queries, sequential static MKL, cold psql sessions), measured WHILE major ingestion ran concurrently and the machine was in normal desktop use. Dates: 2026-06-07 unless noted.

## Cross-platform behavioral determinism

- 289/289 engine unit tests (incl. full UAX#29 grapheme/word/sentence and UAX#15 NFC conformance against UCD 17.0.0). Serial 26.2 s. (One historical failure was ctest -j racing gguf tests on a shared temp path — test design, not code.)
- Perfcache emit byte-deterministic (re-emit + byte-compare target passes); blob 85.4 MB: 1,114,112 records + decomp 13,253 (data 36,567) + compose 12,133.
- pg_regress 8/8 byte-identical to LINUX-GENERATED expected files (different OS, compiler, FP-flag regime): hash128 265 ms · st_4d 51 ms · bootstrap 6,690 ms · glicko2_aggregate 45 ms · entities_exist_bitmap 54 ms · consensus_signed 67 ms · consensus_period 115 ms · converse 90 ms.

## The speedrun

`createdb` 01:15:04.40 → dog-capable (WordNet folded) 01:19:59.27 = **4 m 54.86 s** from empty database to English Q&A with provenance. (Timestamps from the substrate's own rows.)

## Ingestion (evidence rows; rates include adjudication + consensus folding)

| source | rows | wall | rate |
|---|---|---|---|
| unicode | 873,823 | 1:41 | 8.7 k/s |
| iso639 | 34,424 | 0:02 | 17 k/s |
| wordnet | 1,277,341 | 1:36 | 13.3 k/s |
| omw | 3,648,432 | 5:34 | 10.9 k/s |
| ud | 33,342,802 | 33:18 | 16.7 k/s |

Linux 6850K reference: the wordnet stage carried a 90-minute CI budget; UD measured 1 h 56 m. 

## Documents (db-roundtrip: record + attest + fold + bit-perfect reconstruct)

- Alice in Wonderland: 26,543 w / 151,191 B ≈ 35 k tokens — record 1.8 s, fold ~1.2 s → ≈ **12 k tok/s all-in**, ≈ 19.6 k tok/s record-phase; 13,281 consensus relations from its bigrams.
- **Moby Dick: 212,813 w / 21,936 lines / 1,256,545 B ≈ 283 k tokens — 9.8 s fresh ≈ 28.9 k tok/s** (7.7 s second pass ≈ 37 k). Note: 283 k tokens EXCEEDS most production context windows; here it became permanent, queryable, reconstructable state in under ten seconds.
- Library: 16/16 BIT-PERFECT (alice, dorian, dracula, euclid, frankenstein, galileo, moby_dick, mobydick, newton_principia_en, odyssey, pride, hello_world, pangram, great_test, code.py, data.json). Second-pass dedup tier sustained 40–70 k tok/s (dracula 4.1 s, euclid 2.2 s, frankenstein 2.3 s, galileo 1.2 s).
- Comparator: single-device GPU pretraining ingests ~10–25 k tok/s — matched/beaten on CPU while ALSO performing identity, dedup, placement, attribution, adjudication, archival.

## End-of-session substrate

9,043,015 entities · 50,313,627 attestations · 6,028,704 consensus relations · 58,652 document-tier entities. (~67 minutes of machine time from empty.)

## Query latencies (warm unless noted; slow tier; under load)

| query | time |
|---|---|
| converse('what is a dog') — 12-row dual hypernym chain | 14.5 ms warm (38.2 cold; plan 0.01 / exec 12.97) |
| converse('define dog') | 1.8 ms |
| converse('synonyms of dog') | 2.8 ms |
| whale PRECEDES collocates (ranked μ + witness counts) | ~6 ms |
| provable negative (closed-world zero) | 7.2 ms |
| learned-bigram provenance probe (2 rows + witness + timestamp) | 62.9 ms |
| word-curve Fréchet, 4 pairs incl. realization | 14.5 ms (~3.6 ms/pair) |
| anagrams_of('whale') via Hilbert-key btree | **31 ms** (same answer spatially: 27 s → 870×) |
| naive filtered 4D KNN (planner off index) | 27 s / pooled 5.9 s — TUNING ITEM |

Equivalence framing (billing-honest, tokens-out/wall): taxonomy ≈ 3.4 k t/s-equiv; define ≈ 16.7 k — vs 30–100 t/s/user conventional serving (35–500×). Linux 6850K-era rich respond() bundles measured ≈ conventional-equivalent 175 k t/s. EXPLAIN(BUFFERS) additionally offers customer-auditable per-page billing — unavailable in token-stream pricing by construction.

## Discoveries logged

- Position = constituent multiset (exact anagram collision: whale ≡ wheal ≡ waleh ≡ elhwa ≡ welah at geodesic 0); order lives in the curve.
- Hilbert key = free multiset index (the 870× anagram speedup).
- Dual-engine orthogonality numbers: whale~while Fréchet 0.1149 / no relation; whale~ship Fréchet 1.7156 / μ 2010.5 @ 116 witnesses.
- The flip: ignorance proven (0 rows, 7.2 ms) → one sentence → attributed knowledge at 02:35:51.07 (Ahab→admired, admired→Darcy under UserPrompt/v1). PRECEDES = adjacency (probe bigrams, not skip-grams).
- Concurrency law observed: training + serving + desktop use, one box, graceful contention throughout ("benchmarked under YouTube").

## Headroom disclosure

Everything above ran without: SPI walk offload (compiled cascade exists, unused by the new queries), batched/SIMD kernel entry points, threaded MKL, the icx-built custom PG, prepared/pooled connections, the SP-GiST trajectory opclass, GPU ingest math. Conservative composite headroom: 2–3 orders of magnitude (lever inventory: OPEN-PROBLEMS §11).
