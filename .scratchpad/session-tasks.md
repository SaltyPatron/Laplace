# Session task list — ingest lane / substrate (main @ #344)

Load-bearing first. The ROOT fix (bounded working set + client-dedup + clean COPY offload +
per-file producer/queue) cascades into most of the "perf" rows — do it once, several fall.

## Shipped (main)
- [x] MCP stdio server; retire laplace-dev — #341
- [x] Pillar 3a — text emits 0 distributional attestations (content-only) — e5e1b42
- [x] Parallel multi-file pool (RunMultiFileAsync) — #343 (OMW 5.4/6 cores)
- [x] Order-independent Tatoeba (kill IdToRoot, deterministic anchor, no phase) — #343
- [x] Content-only decomposer gate (document seed) — #344
- [x] geometry_successors native primitive + eff_mu descale — 6dd483d
- [x] Branch cleanup (15 merged deleted)
- [x] Memory: flow-map, ingestion-trunk-convergence, never-cut-corners

## THE ROOT (do first — cascades)
- [x] Bound the working set — flush on a memory/row envelope, not 2.85M-record blobs (kills compose collapse 30k→1.8k, shrinks probe/fold)
- [ ] Client-dedup + ON CONFLICT offload — drop the 12M-id / 37s DB existence probe (DB = lookups)
- [~] Producer/queue/consumer — N parallel file-producers → bounded queue → one continuous consumer (subsumes core-saturation + per-file identity)

## Perf (measured)
- [~] Physicality GIST COPY 77k rows/s (4× slow) — cycle/defer geometry index during COPY
- [x] Consensus fold single-threaded 49k cells/s — K-partition parallel fold
- [x] Native UAX#29 word-break ~2 MB/s — distinct scan → precompute wb[]/gb[] arrays (bit-identical)
- [x] Big-file straggler — intra-file parallelism (English OMW / 28MB Webster)

## Telemetry / pillars / model
- [~] Pillar 4 — per-file telemetry (files=N/M live, names, per-file done/evict)
- [ ] Pillar 0 — per-file provenance actually lands (source on the row, walkable file-entity + metadata DAG)
- [ ] Pillar 1 — bulk-native/SIMD Wiktionary parse (off per-line tree-sitter)
- [x] Model ingest — resolve token-index → existing entity; couplings = attestations between existing entities (no N² dump)

## Runs / branches
- [ ] Verified at-scale runs: UD, Tatoeba, chess-ANALYZE profile (not the game ws-apply)
- [ ] Bring in keyed-probe-pg-wrappers CI fixes (resolve conflict)
- [ ] Retire superseded branches on explicit ok (chess-analysis-rerun, foundry, ingestion-trunk-convergence)
