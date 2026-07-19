# 19 — Factor-Storage Research (facts only, no design)

Research date: 2026-07-09. Question served: where can per-circuit factor matrices
(vocab × rank floats — the lossless token→token operators of ingested transformers) live
so that PG-extension native code can score arbitrary token pairs at query time. Two
candidate homes: (a) physicality trajectory payloads in-DB, (b) a perfcache-class mmap
blob. Every file below was read IN FULL; every claim carries a file:line citation. All
paths existed exactly as stated in the task except none — no globbing fallbacks were
needed (the qk headers and the extension parent CMakeLists were read in addition to the
stated list because the stated files reference them).

---

## 1. `engine/core/src/mantissa.c` — the bit-packing channel

### 1.1 The slot primitive: `laplace_slot_to_fp` / `laplace_fp_to_slot`

- A "slot" is a 53-bit integer smuggled through one float8: bit 52 of the slot becomes
  the IEEE-754 sign bit, bits 0–51 become the 52-bit mantissa, and the exponent is
  FORCED to biased-zero `0x3FF` (`mantissa.c:5-7, 25-35`). Every packed double therefore
  has magnitude in [1.0, 2.0) — never NaN, never Inf, never denormal.
- The inverse (`laplace_fp_to_slot`, `mantissa.c:37-43`) discards the exponent field
  entirely: only sign + mantissa round-trip. **Constraint: exactly 53 payload bits per
  double; the 11 exponent bits are structurally unusable.**
- Round-trip is exact `memcpy` bit surgery (`mantissa.c:32-33, 38-39`) — deterministic,
  lossless for the 53 carried bits.

### 1.2 `mantissa_pack` — exact field layout (per 4-double vertex)

Payload struct (`mantissa.h:11-16`): `hash128_t entity_id` (128b), `uint16 ordinal`,
`uint16 run_length`, `uint64 flags` (only low 52 bits used, `mantissa.c:9-10, 48`).

| Slot | Bits (53 each) | Contents | Source lines |
|------|----------------|----------|--------------|
| X (`vertex[0]`) | 0–52 | `entity_id.lo` bits 0–52 (`LAPLACE_X_HASH_BITS=53`) | `mantissa.c:16-17, 50` |
| Y (`vertex[1]`) | 0–10 | `entity_id.lo` bits 53–63 (`Y_LO_BITS=11`) | `mantissa.c:18-19, 52` |
| Y | 11–52 | `entity_id.hi` bits 0–41 (`Y_HI_BITS=42`) | `mantissa.c:20-21, 53-54` |
| Z (`vertex[2]`) | 0–21 | `entity_id.hi` bits 42–63 (`Z_HASH_BITS=22`) | `mantissa.c:22-23, 56` |
| Z | 22–52 | `flags` bits 0–30 (`FLAGS_Z_BITS=31`) | `mantissa.c:11-12, 57-58` |
| M (`vertex[3]`) | 0–15 | `ordinal` (uint16) | `mantissa.c:60, 63` |
| M | 16–31 | `run_length` (uint16) | `mantissa.c:61, 63` |
| M | 32–52 | `flags` bits 31–51 (`FLAGS_M_BITS=21`) | `mantissa.c:13-14, 62-63` |

Unpack is the exact mirror (`mantissa.c:118-138`).

**Total payload per vertex: 4 × 53 = 212 bits** = 128 (entity_id) + 16 (ordinal)
+ 16 (run_length) + 52 (flags). Storage cost per vertex is 4 float8 = 256 bits, so the
channel is 212/256 ≈ 82.8% dense.

### 1.3 Flag-bit allocation (the vertex "classes"), `mantissa.h:18-32`

- bit 0: `LAPLACE_VFLAG_HAS_ATOM` (`mantissa.h:18`)
- bits 1–5: tier, 5 bits (`TIER_SHIFT=1`, `TIER_MASK=0x1F`, `mantissa.h:19-20`)
- bit 6: `LAPLACE_VFLAG_TESTIMONY` (`mantissa.h:30`)
- bits 7–42: testimony score, 36 bits (`SCORE_SHIFT=7`, `SCORE_MASK=0xFFFFFFFFF`,
  `mantissa.h:31-32`)
- bits 31–51: atom codepoint, 21 bits (`ATOM_SHIFT=31`, `ATOM_MASK=0x1FFFFF`,
  `mantissa.h:21-22`)

**Note the overlap**: score bits 31–42 collide with atom bits 31–42. The two classes
(atom-bearing vs testimony) are mutually exclusive by construction — nothing in the
code sets both bit 0 and bit 6.

### 1.4 Testimony vertex class (`laplace_testimony_pack_walk` / `unpack_vertex`)

Per vertex a testimony walk carries (`mantissa.c:79-99`):
- `object_id` — full 128-bit hash (in the entity_id field);
- `ordinal` — walk position `i & 0xFFFF` (`mantissa.c:92`);
- `run_length` — `games[i]` or 1 (`mantissa.c:93`);
- flags: bit 6 set + a **zigzag-encoded int64 score in fixed-point 1e-9**, capped at 36
  bits post-zigzag (`mantissa.c:87-95`). Pack returns `-2` if `zigzag(score) >
  SCORE_MASK` (`mantissa.c:89-90`) → representable score range is
  |score_fp1e9| ≤ (2^36−1)/2 = 34,359,738,367, i.e. **±34.359738367 at 1e-9 resolution**.
- Unpack rejects any vertex without bit 6 (`mantissa.c:108-109`) and returns
  object_id / score / games / ordinal (`mantissa.c:101-116`).

### 1.5 Answers to the stated questions

- **Exact per-vertex payload capacity**: 212 bits (128 id + 16 ordinal + 16 run + 52
  flags), of which the flags word is the only "free-form" region and it is 52 bits.
- **Is there an existing vertex class that carries raw float data losslessly?** No.
  The only scalar channel is the testimony class's 36-bit zigzagged fixed-point-1e-9
  score (`mantissa.c:87-95`) — one scalar per vertex, precision 1e-9, range ±34.36.
  Factually: any 32-bit pattern (e.g. a raw float32's bits reinterpreted as int32)
  survives the zigzag round-trip inside the 36-bit field, since zigzag of any int32 is
  ≤ 2^33−1 < 2^36 (`mantissa.c:71-77, 89`), so the EXISTING testimony class can carry
  one lossless 32-bit scalar per vertex alongside a 128-bit object id, 16-bit ordinal,
  and 16-bit run_length. It cannot carry more than one.
- **Bits free for a NEW vertex class**: with bit 0 and bit 6 clear, flag bits 7–51 are
  unclaimed (45 bits); bits 1–5 (tier) are also semantically free if the class doesn't
  claim tier semantics (50 bits total). If a new class additionally redefines the
  entity_id/ordinal/run_length fields (nothing in `mantissa_pack` interprets them —
  interpretation is entirely caller-side, `mantissa.c:45-69`), the ceiling is 212 bits
  minus whatever discriminator bits the class reserves. E.g. 6 × float32 = 192 bits
  fits per vertex with 20 bits left for class tag + indices.

---

## 2. `engine/core/src/trajectory.c` — trajectory build/unpack (full inventory)

Four functions; the file is 74 lines total, all read.

- `trajectory_build_flagged(hashes, flags, n, out_xyzm)` (`trajectory.c:4-21`): one
  vertex per constituent; `ordinal = i+1` (1-based), `run_length = 1`, flags passthrough
  (0 if the flags array is NULL). **Hard cap `n > 0xFFFF → -1`** (`trajectory.c:10`) —
  max 65,535 vertices per trajectory, forced by the 16-bit ordinal.
- `trajectory_build(hashes, n, out)` (`trajectory.c:23-27`): the flags=NULL wrapper.
- `trajectory_build_rle(constituents, n, out_xyzm, out_vertex_count)`
  (`trajectory.c:29-57`): collapses consecutive-identical hash runs into one vertex with
  `run_length = run` (uint16 cast at `trajectory.c:49`), `ordinal = i+1` = 1-based index
  of the run START in the pre-RLE sequence, flags = 0. Same `n > 0xFFFF` cap
  (`trajectory.c:35`). Output vertex count ≤ n.
- `trajectory_constituents(xyzm, n_points, out_hashes, cap)` (`trajectory.c:59-73`):
  unpacks entity ids only (drops ordinal/run/flags); errors if `n_points > out_cap`.

C# bindings exist for all three builders: `NativeInterop.cs:49-56`,
`Trajectory.cs:12-52` (each throws on non-zero rc).

**Size arithmetic**: max trajectory = 65,535 vertices × 32 bytes = 2,097,120 bytes
(~2.0 MB) of XYZM, carrying 65,535 × 212 bits ≈ 1.66 MB of true payload.

---

## 3. `extension/laplace_substrate/src/perfcache.c` — the blob-loading pattern

### 3.1 GUC contract (`perfcache.c:37-70`)

`laplace_substrate_perfcache_init()` registers, in `_PG_init`
(`laplace_substrate.c:713-724`):
- `laplace_substrate.perfcache_path` — string, default `""`, **PGC_SIGHUP**
  (`perfcache.c:40-48`). Empty disables; consumers fall back or error per their contract.
- `laplace_substrate.highway_perfcache_path` — string, default `""`, PGC_SIGHUP
  (`perfcache.c:49-57`).
- `laplace_substrate.native_mkl_threads` — int, default 1, range 1–64, **PGC_SUSET**
  (`perfcache.c:58-68`); consumed by `laplace_runtime_init(LAPLACE_RUNTIME_HOST_PG, n)`
  at `_PG_init` (`laplace_substrate.c:726-737`).
- `MarkGUCPrefixReserved("laplace_substrate")` (`perfcache.c:69`) — a third blob's GUC
  must be registered in this same init to live under the reserved prefix.

### 3.2 Load mechanics: mmap, in engine/core, not in the extension

The extension file only orchestrates; the actual load lives in engine/core (statically
linked into the extension DLL — `extension/CMakeLists.txt:70-80`):

- **T0 blob** (`codepoint_table_load_perfcache`, `codepoint_table.c:105-148`):
  - `pc_map` = `CreateFileA`/`CreateFileMappingA(PAGE_READONLY)`/`MapViewOfFile` on
    Windows (`codepoint_table.c:37-57`), `open`/`mmap(PROT_READ, MAP_PRIVATE)` on POSIX
    (`codepoint_table.c:66-83`). **mmap, read-only, never palloc'd, never a file read.**
  - Validation ladder with distinct rc codes: rc −1 open/stat/mmap; −2 bad
    magic/version (`codepoint_table.c:114-116`); −3 record count/size or section-offset
    overrun (`codepoint_table.c:117-127`); −4 **BLAKE3 CRC over the whole body vs a
    16-byte trailer** (`codepoint_table.c:129-133`; trailer size
    `perfcache_format.h:90`). The extension's error message documents exactly this rc
    ladder (`perfcache.c:94-95`).
  - State is a file-scope global struct of pointers into the mapping
    (`codepoint_table.c:20-33`); `codepoint_table_is_loaded()` = records ptr non-NULL
    (`codepoint_table.c:101-103`).
  - Blob format: 128-byte header (magic `0x4652504C`, version 2, record
    count/size/offsets, `perfcache_format.h:64-88`), fixed 1,114,112 × 80-byte records
    (`perfcache_format.h:15, 25, 83-87`), decomp/compose sections, BLAKE3 trailer.
- **Highway blob** (`highway_table_load`, `highway_table.c:102-148`): identical
  mmap-per-platform code (`highway_table.c:37-85`); validates magic `0x5957484C`,
  version, counts vs compile-time caps (189 relations / 13 bands,
  `highway_manifest.h:5-8`), and section bounds (`highway_table.c:111-124`).
  **No CRC check** — that is a difference from the T0 blob. After mapping it builds a
  small in-memory derived index: BLAKE3 type-ids of all canonical names + a 1024-slot
  linear-probe hash bucket (`highway_table.c:31-32, 134-144`). Header is 128 bytes
  (static assert `highway_table.c:20`), records 32 bytes (`highway_table.h:25-34`).

### 3.3 When loading happens: lazy-per-backend OR postmaster prewarm

- **Lazy path**: `laplace_perfcache_ready()` / `laplace_highway_ready()`
  (`perfcache.c:78-117`) — first native call in a backend loads the blob; failure is
  `ereport(ERROR)` with rc detail and an errhint pointing at the GUC and
  `install-extensions.cmd` (which stages the blob, `perfcache.c:96-97, 114-115`).
  Unconfigured (empty path) returns false, no error — the CALLER decides
  (e.g. `word_id` errors with `ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE`,
  `perfcache.c:249-254`).
- **Prewarm path** (`laplace_substrate_perfcache_prewarm`, `perfcache.c:159-197`): runs
  only `if (process_shared_preload_libraries_in_progress)` — i.e. only under
  `shared_preload_libraries`. The postmaster loads once and **every forked backend
  inherits the mmap'd blobs, the CRC validation, and the reverse-index copy-on-write**
  (comment `perfcache.c:163-170`). Prewarm failures are `WARNING`, never `ERROR` — a
  stale path must not stop the cluster from starting; the lazy path still errors with
  full detail on first real use (`perfcache.c:168-170, 185-196`).
- Derived per-process indexes go in `TopMemoryContext`
  (`MemoryContextAlloc(TopMemoryContext, ...)`, `perfcache.c:147-156`) so they survive
  transactions.

### 3.4 What a third blob (model factor cache) must replicate — factual checklist

1. A binary format header with magic + version + counts/offsets, validated before use
   (`codepoint_table.c:112-127`); the T0 blob adds a BLAKE3 body-CRC trailer
   (`codepoint_table.c:129-133`), the highway blob does not.
2. Loader code in **engine/core** (both blob loaders live there so the CLI, tests, and
   the extension share one implementation via the static lib link,
   `extension/CMakeLists.txt:70-74`), file-scope global state, mmap read-only,
   `*_is_loaded()` / `*_load(path)` / `*_unload()` surface
   (`codepoint_table.c:91-103`, `highway_table.c:91-100`).
3. A `laplace_substrate.<name>_path` string GUC (PGC_SIGHUP, default empty) registered
   inside `laplace_substrate_perfcache_init` (`perfcache.c:37-70`).
4. A `laplace_<name>_ready(void)` lazy gate in `perfcache.c` that ereports ERROR with
   an errhint naming the GUC + install-extensions.cmd (`perfcache.c:78-117`).
5. A branch in `laplace_substrate_perfcache_prewarm` (WARN-not-ERROR,
   postmaster-only, `perfcache.c:159-197`).
6. Staging/config by `install-extensions.cmd` (referenced at `perfcache.c:96-97,
   253-254`, `highway_mask.c:187-189`).
7. Blob generation at build time — CLAUDE.md documents both existing blobs as
   build-products deployed by the perfcache phase of rebuild-all
   (also `.scratchpad/03_Chess.txt:419`: "both build-time-generated, mmap'd, loaded
   once at process start").

---

## 4. `extension/laplace_substrate/src/highway_mask.c` — the in-extension perfcache consumer pattern

Full file read (342 lines). How native extension code resolves relations at query time:

- Pure-compute SQL functions over bytea masks with NO table access:
  `pg_laplace_highway_match` (64-bit-word AND-accumulate over two bytea,
  `highway_mask.c:59-104`), `pg_laplace_highway_popcount` (`:106-136`),
  `pg_laplace_highway_mask_bits` (bit positions → int4[] for GIN indexing,
  `:138-178`). These replaced SQL implementations that were "the canonical Rule #1
  violation" (`:20-33`). Byte-order contract: mask bytea is the raw little-endian
  `uint64 w[4]` struct memory, memcpy is the identity mapping (`:28-33`).
- Perfcache-table consumers gate on `require_highway_table()` →
  `laplace_highway_ready()` → ereport with GUC errhint (`:180-190`).
  `pg_laplace_highway_band_mask` (`:192-214`), `pg_laplace_relation_highway_bit`
  (`:216-232`), `pg_laplace_relation_highway_band` (`:234-249`) are **pure in-memory
  lookups against the mmap'd blob** — hash-bucket probe by BLAKE3 type id
  (`highway_table.c:150-169`) or direct record index by bit (`highway_table.c:171-182`).
- The hybrid pattern (`pg_laplace_consensus_band_edges`, `:251-341`): resolve the
  band's ≤256 relation-type ids ENTIRELY in memory from the blob ("zero DB round
  trips", `:300-318`), then run ONE indexed SPI query with the id array as a bound
  parameter (`:263-272, 326-340`). Comment: "bit → canonical name → BLAKE3 type id via
  the static relation law, then ONE indexed SPI query" (`:257-262`). This is the
  existing template for "blob supplies the operands, SPI supplies the rows."

---

## 5. The qk kernels (`engine/synthesis/src/qk_*.cpp`) and extension linkability

### 5.1 Exact math (identical numerics in all three files)

- `project_token`: `proj[d] = Σ_m E_row[m]·W[d·d_model+m]`, accumulated in double with
  **Neumaier compensated summation** (`qk_project_cached.cpp:14-32`,
  `qk_pairs_threshold.cpp:13-31`, `qk_pairs_threshold_pruned.cpp:14-32`). W is laid out
  row-major `[head_dim][d_model]`.
- Score: Neumaier dot product of projected q and k (`qk_project_cached.cpp:42-48`).
- Emission condition is strictly `fabs(score) > noise_floor`
  (`qk_pairs_threshold.cpp:85, 123`; `qk_project_cached.cpp:172`;
  `qk_pairs_threshold_pruned.cpp:138, 178`).
- **No BLAS/GEMM anywhere in these three files** — all math is hand-rolled scalar
  loops. `LAPLACE_HAS_MKL` gates ONLY the TBB `parallel_for_size` wrappers
  (`qk_project_cached.cpp:7-10, 81-89`; same pattern in both threshold files); the
  `#else` branches are complete serial implementations.

### 5.2 Memory layout of q_cache/k_cache

`project_qk_layer` (`qk_project_cached.cpp:57-91`):
- inputs: `E_f32` vocab×d_model float32 row-major; `Wq` = n_heads consecutive
  `[head_dim][d_model]` float32 blocks; `Wk` = n_kv blocks likewise (`:69-79`).
- `q_cache_out`: **double**, indexed `[(t·n_heads + h)·head_dim + d]` (`:71-74, 116-118`)
  → size vocab·n_heads·head_dim·8 bytes.
- `k_cache_out`: **double**, `[(s·n_kv + kh)·head_dim + d]` (`:75-78, 113-115`)
  → vocab·n_kv·head_dim·8 bytes.

`score_qk_head_cached` (`qk_project_cached.cpp:93-205`) scores one (head, kv_head)
pair over query rows [q0,q1) against ALL vocab keys, writing
`qk_pair_f64_t {uint32 query_idx; uint32 key_idx; double score}` (16 bytes,
`qk_pairs_threshold.h:10-14`), rows sorted by key_idx within a query (`:174-179`),
whole-row-granular overflow truncation with `*overflow = 1` (`:190-199`).

### 5.3 The pruning contract

Both `score_qk_head_cached` and `compute_qk_pairs_above_threshold_pruned` use the same
Cauchy–Schwarz prune (`qk_project_cached.cpp:120-149`,
`qk_pairs_threshold_pruned.cpp:73-109`):
1. Compute every key's projected L2 norm; sort keys by norm descending (tie → lower
   index first).
2. For a query of norm `qnorm`, binary-search the prefix of keys with
   `knorm ≥ noise_floor/qnorm` (`candidate_prefix_len`); only that prefix is scored.
3. Since `|q·k| ≤ qnorm·knorm`, every skipped pair provably has
   `|score| ≤ noise_floor`, and the emission test is strict `>` — **the prune is
   exact/lossless with respect to the > noise_floor contract**, not approximate.
4. `noise_floor ≤ 0` disables the prune (prefix = whole vocab,
   `qk_project_cached.cpp:140-141`); NaN or negative floor is rejected up front
   (`:99, qk_pairs_threshold_pruned.cpp:67`).

`compute_qk_pairs_above_threshold` (unpruned, `qk_pairs_threshold.cpp:42-143`) is the
two-pass count-then-write variant: K projected once into a `std::vector<double>
K_cache(vocab·head_dim)` (`:58-73`), q re-projected per row in BOTH passes
(`:77-88, 114-130`) — it never materializes a q_cache.

### 5.4 Thread behavior

Under `LAPLACE_HAS_MKL`, all loops run through
`laplace::tbb_ops::parallel_for_size(...)` inside a TBB `performance_arena`
(P-cores-only task arena, `tbb_parallel.h:12-35`); the entire header is empty unless
`LAPLACE_HAS_MKL` is defined (`tbb_parallel.h:3, 41`). Without the define, all three
files are single-threaded scalar C++ (only `<vector>`, `<algorithm>`, `<cmath>`).

### 5.5 Could they compile into `laplace_substrate` as-is? Build facts

- The extension module compiles **only C sources** today: `EXT_C_SOURCES` is 19 `.c`
  files (`laplace_substrate/CMakeLists.txt:49-70`); the qk files are `.cpp` living in
  the `laplace_synthesis` shared lib (`engine/synthesis/CMakeLists.txt:26-33`).
- The extension links `laplace_core`, `laplace_dynamics`, `postgres.lib`
  (`laplace_substrate/CMakeLists.txt:85-89` Windows; `:99` non-Windows).
  **`laplace_synthesis` is NOT linked** — the qk symbols are unreachable from the
  extension today.
- On Windows those link names are IMPORTED STATIC libs defined in the extension
  superbuild: `laplace_core_static.lib` + blake3 + libxml2
  (`extension/CMakeLists.txt:70-74`) and **`laplace_dynamics_pg_static.lib`**
  (`extension/CMakeLists.txt:76-80`). Critically, `laplace_dynamics_pg_static`
  contains ONLY `src/init.cpp` (`engine/dynamics/CMakeLists.txt:38`) compiled with
  `LAPLACE_HAS_MKL=1` + `LAPLACE_RUNTIME_NO_TBB=1` (`:42-46`) — i.e. the extension gets
  the MKL runtime-init shim, none of the dynamics math.
- **MKL IS available in-extension, statically and sequentially**:
  `mkl_intel_lp64.lib`, **`mkl_sequential.lib`**, `mkl_core.lib` are interface-linked
  into the imported dynamics target (`extension/CMakeLists.txt:60-68, 80`), MKLROOT is
  a hard configure requirement (`:60-64`). `_PG_init` calls
  `laplace_runtime_init(LAPLACE_RUNTIME_HOST_PG, laplace_substrate_native_mkl_threads())`
  and FATALs if MKL is unavailable (`laplace_substrate.c:726-737`); in PG the thread
  count must come from the GUC — autodetect is refused (`dynamics/src/init.cpp:86-88`).
- **TBB is NOT available in-extension** (sequential MKL + `LAPLACE_RUNTIME_NO_TBB`;
  no TBB lib in the extension link set anywhere in `extension/CMakeLists.txt`).
- CXX is enabled for the extension superbuild
  (`project(laplace_extensions ... LANGUAGES C CXX)`, `extension/CMakeLists.txt:14`),
  so adding `.cpp` files to the module target is toolchain-supported.
- Net factual answer: the three qk files compile as-is ONLY with `LAPLACE_HAS_MKL`
  **undefined** (scalar single-threaded; that path has zero external dependencies
  beyond the C++ stdlib). Compiling them WITH `LAPLACE_HAS_MKL` requires TBB headers
  and the `tbb_ops` arena machinery (`tbb_parallel.h:7-9, 21`), which the extension
  build deliberately excludes. There is no NO_TBB-but-MKL variant of these files —
  their only switch is `LAPLACE_HAS_MKL`, and they make no MKL library calls that the
  in-extension sequential MKL could serve anyway (§5.1). Standard PG/C++ caveats
  (std::vector allocates via malloc outside memory contexts; C++ exceptions vs
  ereport longjmp) are properties of the runtime, not of these files' code.

---

## 6. The physicality write path — wire format and limits

### 6.1 `PhysicalityRow` (`app/Laplace.Substrate/Crud/SubstrateChange.cs:47-61`)

```
PhysicalityRow(Hash128 Id, Hash128 EntityId, Hash128 SourceId, PhysicalityType Type,
               double CoordX, CoordY, CoordZ, CoordM, Hilbert128 HilbertIndex,
               double[]? TrajectoryXyzm, int NConstituents, double? AlignmentResidual,
               int? SourceDim, long ObservedAtUnixUs)
```
`TrajectoryXyzm` is a flat `double[]` of 4-tuples (XYZM per vertex), nullable.
Note `SourceId` exists on the record but is NOT a COPY column (see 6.3 column list).
Sibling `TestimonyWalkRow` (`SubstrateChange.cs:17-25`) carries pre-packed vertices as
`byte[] PackedVertices` — walks are journaled by the accumulating writer and are
rejected if they reach the evidence writer (`NpgsqlSubstrateWriter.cs:45-49`).

### 6.2 C# → native staging

`NpgsqlSubstrateWriter.ApplyManyInternalAsync` dedups by physicality id and calls
`managedStage.AddPhysicality(..., p.TrajectoryXyzm.AsSpan(), ...)` — null trajectory
becomes an empty span (`NpgsqlSubstrateWriter.cs:86-97`). `IntentStage` is a SafeHandle
over the native `intent_stage_t` (`NpgsqlSubstrateWriter.cs:158-163`), whose buffers
are unmanaged COPY-tuple arenas (`intent_stage.c:180-187, 44-49`).

### 6.3 Exact wire format (`engine/core/src/intent_stage.c`)

Physicality COPY column list (`intent_stage.c:33-35`):
`id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
alignment_residual, source_dim, observed_at` — 10 columns (`:41`).

Each row is PG COPY BINARY: int16 field count, then per field int32 length (−1 = NULL)
+ big-endian payload. The trajectory field (`intent_stage.c:347-353`):
- NULL if no vertices;
- **1 vertex → WKB PointZM**: int32 len=37, then 1 byte `0x01` (little-endian flag),
  uint32 LE `WKB_POINT_TYPE|Z|M` (`0x01|0x80000000|0x40000000`), 4 × float8 LE
  (`intent_stage.c:25-29, 151-160`). No SRID flag is ever written.
- **≥2 vertices → WKB LineStringZM**: int32 len = `1 + 4 + 4 + 32·n`, then `0x01`,
  uint32 LE `WKB_LINESTRING_TYPE|Z|M`, uint32 LE vertex count, then n × 4 float8 LE
  (`intent_stage.c:162-178`). The XYZM doubles are copied bit-exact
  (`buf_append_le_double`, `:100-105`) — the mantissa payload survives untouched.
- The `coord` column always uses the 37-byte PointZM form (`:345`).

Emission: signature + flags + 0-extension header, tuple bytes, `0xFF 0xFF` trailer
(`intent_stage.c:18-23, 657-679`); or raw tuple pointer for streaming
(`intent_stage.c:681-689`). Transport is
`COPY laplace.physicalities (...) FROM STDIN (FORMAT BINARY)` via
`BeginRawBinaryCopyAsync` (`NpgsqlWorkingSetApply.cs:546-549`), parallel over id-range
groups with `session_replication_role=replica`, `synchronous_commit=off`, `jit=off`
(`NpgsqlWorkingSetApply.cs:497-538`). Partitioning is by referenced `entity_id` for
referential co-location, Hilbert-sorted within a partition (`intent_stage.c:456-504,
537-580, 633-642`).

### 6.4 Size limits on this path

- 65,535 vertices per trajectory (builder cap, `trajectory.c:10, 35`) → max ~2.097 MB
  WKB per row from `trajectory_build*`. `intent_stage_add_physicality` itself takes
  `uint32 trajectory_n_vertices` with no cap of its own (`intent_stage.c:328-338`) —
  the `field_len` uint32 arithmetic (`:166`) is the only in-file bound.
- DB column: `trajectory geometry(GeometryZM)` — accepts both Point and LineString
  (`extension/laplace_substrate/sql/schema/tables/physicalities.sql.in:7`); PostGIS
  geometry is a varlena (1 GB PG hard cap; TOASTable). Stats are disabled on the
  column (`physicalities.sql.in:23-29`: ANALYZE 137s → 0.87s on 88M rows) and
  autoanalyze thresholds tightened (`:31-32`).
- `CopyBlobValidator` walks the unmanaged blob with long offsets — explicitly **no
  2 GiB ceiling** per stage buffer any more (`CopyBlobValidator.cs:36-45`); validation
  is default-ON (`:8-15`). Total apply footprint is budgeted at 1 GiB by
  `MemoryTopology` so no single-table buffer approaches the 2 GiB int wall
  (`app/Laplace.Core/Core/MemoryTopology.cs:18-19`, `IngestSizing.cs:82-83`).

### 6.5 Read-side reality for candidate (a)

The only native read path over trajectories today is GenCorpus (§7): per-backend SPI
scans of `corpus_sentence_constituents_since` cursors at 65,536 rows/fetch
(`trajectory_corpus.c:263-272`), interning 16-byte ids into a vocab HTAB. Reading a
trajectory's payload in-extension means an SPI fetch of the geometry varlena and
`mantissa_unpack`/`laplace_testimony_unpack_vertex` over its vertices — there is no
existing native bulk-mmap path over physicalities. (No function in the extension
currently unpacks testimony vertices from SQL-fetched trajectories; the unpack helpers
live in engine/core, linked and available.)

---

## 7. Issue 49 and the GenCorpus "perfcache-class mmap blob" precedent

### 7.1 Issue 49 verbatim substance (`.scratchpad/02_Identified_Issues.txt:197-202`)

> ISSUE 49 (update 2026-07-09) — walk_text cold path now >240s at 135M attestations
> (statement_timeout fired inside corpus_sentence_constituents_since). recall chat
> (recall_session) unaffected — /v1/chat/completions answers in seconds.
> /v1/completions rides walk_text and now returns an honest 503 at the API's 30s
> command budget instead of hanging the client. **The prescribed fix stands:
> perfcache-class mmap'd GenCorpus blob (walk-engine findings), engine-side.**

Issue 52 lists /v1/completions among the endpoints killed by this
(`02_Identified_Issues.txt:204-209`).

### 7.2 What GenCorpus is today (the thing the blob would replace)

- A **per-backend, in-memory, SPI-built** token-stream cache: static
  `GenCorpus *gen_corpus` (`trajectory_corpus.c:63`), allocated in a child of
  `TopMemoryContext` (`:419`), so it survives transactions but dies with the backend
  and is REBUILT per backend.
- Struct (`trajectory_corpus.h:23-49`): int32 token `stream` (+`sep_after`), lazy
  suffix array for prefix search (`trajectory_corpus.c:483-495`), 16-byte-id vocab
  array + HTAB, separator flags, parents HTAB, watermark fields. Huge allocations via
  `MemoryContextAllocHuge`/`repalloc_huge` (`:363-370, 487`).
- Built by cursoring `laplace.corpus_sentence_constituents_since($src,$cap,$since)`
  (`:263-272`); staleness probe = `laplace.corpus_trajectory_probe()` rows/max_us
  (`:118-131`); incremental refresh from the timestamp watermark with
  discard-on-error (`:460-533`); GUC-config change forces full rebuild (`:507-514`).
- GUCs: `corpus_max_rows` (deprecated), `corpus_max_orphan_sentences`,
  `corpus_document_source` (`trajectory_corpus.c:43-61`).
- Search-side constants: `GEN_COMPARE_CAP 64`, `GEN_MAX_ORDER 16`,
  `GEN_MAX_STEPS 2048`, sentinel −1 (`trajectory_corpus.h:12-15`).

### 7.3 Precedent statements on record

- `.scratchpad/03_Chess.txt:419`: the two existing blobs are "build-time-generated,
  mmap'd, loaded once at process start" — the pattern named as reusable.
- Walk-engine findings (memory): the 14s cold walk was per-backend GenCorpus rebuild;
  walk itself ~1 ms/step; "fix = perfcache-class mmap blob". No engine/ code contains
  a GenCorpus blob implementation yet (grep of `engine/` for GenCorpus: no files).
- `docs/specs/14_Foundry_Root_Cause_and_Research.txt:536` flags "per-call GUC/mmap
  check (Bug A class)" as a suspect pattern — the known hazard note attached to
  perfcache-GUC access from parallel workers (Bug A: recall/label perfcache-GUC
  parallel-worker segfault, memory `project_native_crash_bugs_2026-07-06`).

---

## 8. Decisive facts side-by-side (report, not recommendation)

| Fact | (a) trajectory payloads in-DB | (b) perfcache-class mmap blob |
|---|---|---|
| Float capacity | 212 payload bits/vertex; no existing raw-float class; testimony class = ONE 36-bit fixed-point scalar (±34.36 @1e-9) + 128-bit id per vertex (`mantissa.c:79-99`); a new class has 45–50 free flag bits and can repurpose the 160 id/ordinal/run bits (`mantissa.h:18-32`) | Arbitrary — raw float32/float64 arrays verbatim; both existing blobs store raw doubles/structs (`perfcache_format.h:17-25`) |
| Per-unit size cap | 65,535 vertices/trajectory (`trajectory.c:10`), ~2 MB WKB; 1 GB varlena ceiling; multi-row spill = one row per shard | File-size bound only; T0 blob already ships 1,114,112 × 80-byte records ≈ 85 MB (`perfcache_format.h:15, 83`) |
| Query-time access from extension | SPI fetch of geometry + in-memory unpack; no existing native bulk reader; GenCorpus shows per-backend SPI materialization costs (Issue 49: >240 s cold at 135M attestations) | Direct pointer arithmetic on mmap; postmaster prewarm inherits mapping into every backend for free (`perfcache.c:159-197`) |
| Consistency with ingest | Rides the existing COPY/dedup/content-addressing spine (`intent_stage.c:321-368`); content-addressed id; transactional | Out-of-band artifact; regenerated at build/export time; version/CRC-gated (`codepoint_table.c:112-133`); stale-path failure mode is WARN-then-lazy-ERROR |
| Scoring kernels | qk kernels not linked in extension; scalar variants compile dependency-free; sequential MKL IS linked in-extension but unused by these kernels (`extension/CMakeLists.txt:65-80`, §5) | Same kernel facts; blob supplies E/W or precomputed q/k caches as raw float arrays in the layouts of §5.2 |
| Existing in-extension consumer template | `consensus_band_edges`: blob resolves operands in memory, ONE bound-param SPI query fetches rows (`highway_mask.c:251-341`) | Same file is the template; plus known hazard: per-call GUC/mmap check from parallel workers = Bug A class (`14_Foundry...:536`) |
