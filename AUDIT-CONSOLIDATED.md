# Laplace Audit — Consolidated Action Plan

Three deep multi-agent passes (correctness/security, perf/altitude, decomposer subsystem),
grounded in an architecture map of perfcache, tree-sitter assimilation, the decomposer
pipeline, and attestation/witnessing. ~320 agents total. Security/auth/billing findings are
in `AUDIT-REPORT.md` but **deprioritized per dev/sandbox status**.

Ordered for the stated goals: **accuracy first, then speed.**

---

## TIER 0 — Accuracy bugs that silently corrupt the substrate

These produce wrong/missing/non-converging graph data with no error. Fix before any perf work.

1. **Cross-source convergence is broken** — `engine/core/src/grammar_compose.cpp:266`.
   JSON string-leaf entity ids are a tier-2 merkle over graphemes; ContentWitnessBatch
   (WordNet/OMW/VerbNet) uses the full codepoint→grapheme→word→sentence root. For any
   multi-word/multi-grapheme surface ("New York", glosses, phrasal lemmas) the two ids
   **differ**, so a Wiktionary node never links to the identical WordNet node. The graph
   silently fragments — this defeats the core premise. **Both paths must share one root-id fn.**

2. **Unscoped JSON property lookups** — `app/Laplace.Decomposers.Abstractions/JsonGrammarHelper.cs:185`.
   `TryPropertyStringSpan`/`TryArrayPropertyNode`/`ObjectNodesInArrayProperty` scan the whole
   flattened AST and take the FIRST key match, ignoring object nesting. `WalkRootRelations`
   pulls a *sense's* synonyms array as root-scope edges (duplicate, context=null), and root
   `word` can resolve to a nested relation target → wrong subject identity for the record.
   Fix: thread a `RootNodeIndex` through `GrammarComposeContext`, route through the existing
   `*OnObject` scoped helpers.

3. **Tensor weights corrupted silently** — `app/Laplace.Decomposers.Model/WeightTensorETL.cs:37`.
   `LoadTensorF32` trusts `expectedElements` from config.json with no check against actual
   buffer length → heap over-read; mismatched config corrupts every weight. Validate
   `raw.LongLength >= expectedElements * bytesPerElement` before decode.

4. **Surface strings not unescaped** — `JsonGrammarHelper.cs:265`. Values taken as raw JSON
   bytes; any escaped char yields a wrong, non-convergent id. (Compounds #1.)

5. **FrameNet "Subframe of" inverted** — `app/Laplace.Decomposers.FrameNet/FrameNetDecomposer.cs:41`.
   Wrong subject/object orientation → reversed edges.

6. **UD XPOS dropped** — `app/Laplace.Decomposers.UD/UDDecomposer.cs:403`. Column parsed, never
   emitted as an edge → missing data.

7. **Most code files produce no substrate** — `app/Laplace.Decomposers.Code/CodeDecomposer.cs:23`.
   `ExtToModality` is a hardcoded ~13-entry C# map; the native registry compiles in 31 grammars.
   ts/java/ruby/cuda/glsl/kotlin/php/etc. are silently skipped. Unify the source of truth with
   native `EXTS[]` (one boundary, not four divergent maps).

8. **HF tokenizer merges dropped** — `app/Laplace.Decomposers.Model/LlamaTokenizerParser.cs:344`.
   Only legacy space-joined merges handled; newer 2-element-array form → zero MERGES_WITH edges.

---

## TIER 1 — Speed: the inference hot path (biggest compute wins)

9. **Inference math is scalar and platform-dead** —
   `engine/synthesis/src/qk_pairs_threshold.cpp:27`, `qk_project_cached.cpp:28`,
   `qk_pairs_threshold_pruned.cpp:23`.
   The Q/K projection is a GEMV done as hand-rolled per-element Neumaier dot products; the
   carry dependency blocks the vectorizer. MKL is linked but the GEMV doesn't call
   `cblas_sgemv`/`dgemm`. TBB parallelism is `#ifdef LAPLACE_HAS_MKL` only — the fallback runs
   vocab×vocab fully serial. AVX2 paths in `bf16_decoder.c:6` and `engine/dynamics/src/eigenmaps.cpp:28`
   are guarded `__AVX2__ && __x86_64__`, which **MSVC never defines → dead on your Windows build.**
   Fix: route GEMV/GEMM through BLAS; fix the AVX2 guards for MSVC (`_M_X64`/`/arch:AVX2`);
   make TBB unconditional.

10. **SPI N+1 storms + dangling reads** — `extension/laplace_substrate/src/graph_geometry_reads.c`
    `:126` (kNN), `:397` (structural_cluster), `:557` (structural_locale).
    Each pulls 200–3000 candidates then issues a *per-candidate* SPI query for a C function the
    `<<->>` operator already computes — 200–4000 nested executes per interactive query. Collapse
    to one set-based query (the pattern already exists at `26_generation.sql.in:668`). Also a
    **correctness bug**: candidate coord/traj are stored as uncopied Datums into the global
    `SPI_tuptable`, which the nested queries clobber → stale geometry (`:121`, `:508`).

---

## TIER 2 — Speed: decomposer throughput

11. **Tree-sitter parser allocated per line / per file** — `engine/core/src/grammar_rows.c:59`,
    `grammar_decomposer.c:68`. A million-line JSONL allocates a million `ts_parser_new`. Pool one
    parser per worker.

12. **Query recompiled + file parsed twice/thrice** — `grammar_tags.c:46` (`ts_query_new` every
    call), `GrammarEntityBuilder.cs:188` (parse in Parse, re-parse in Tags.Run, grapheme floor a
    third time). Cache compiled `TSQuery` per modality; share one parse.

13. **O(N²/N³) compose passes** — `grammar_compose.cpp:453` (second full child rebuild), `:556`
    (PRECEDES O(N²) scan + O(N³) dedup + realloc-per-element). One CSR child-bucket pass; hash-set dedup.

14. **JsonGrammarHelper O(N²–N³) with per-node string marshalling** — `JsonGrammarHelper.cs:55`,
    `:151` (`Encoding.UTF8.GetBytes` per comparison on constant strings), `:304` (nested scans);
    `GrammarDecomposer.cs:71` (`NodeTypeName` = P/Invoke + `Marshal.PtrToStringUTF8` per node),
    `:62` (`GetNode` one struct per crossing). Bulk-fetch the node array once; compare integer
    type-ids not strings; pre-encode property bytes.

15. **AST leak + parse-then-reject** — `StructuredGrammarIngest.cs:59`. Rows dropped by
    `acceptRow` are fully tree-sitter-parsed first AND leak their native AST (`continue` before
    `GrammarAst.Adopt`). Prefilter raw bytes before the native parse; dispose on reject.

16. **Ingest is fully serial** — `IngestRunner.cs:164` (only commit fans out, not
    decompose/parse/materialize), `StructuredGrammarIngest.cs:56`, `CodeDecomposer.cs:66`
    (await-in-loop), `ConceptNetFastIngest.cs:27`. Rows/files are independent → bounded parallel.
    **Blocker:** the content-witness dedup banks `g_canon_slots`/`g_entity_slots`
    (`content_witness_batch.c:213`) are global mutable with no sync — must be made per-worker or
    lock-free before parallelizing the content path. Also `ConsensusAccumulatingWriter.cs:223`
    takes a RW-lock + monitor per attestation, serializing the parallel-commit path.

17. **Scalar tensor decode** — `WeightTensorETL.cs:46-57`. F32 copies float-by-float when layouts
    are byte-identical (use `Buffer.MemoryCopy`); BF16 decodes to double then narrows to float in
    a second loop. Vectorize / use the existing native gather kernel (`ModelTokenEdgeETL.cs:79`).

---

## Reports
- `AUDIT-CONSOLIDATED.md` — this file
- `AUDIT-DECOMPOSERS.md` — 61 findings (8 crit / 25 high)
- `AUDIT-PERF.md` — 64 findings (8 crit / 22 high)
- `AUDIT-REPORT.md` — 139 findings (correctness/security; security tier deprioritized)
