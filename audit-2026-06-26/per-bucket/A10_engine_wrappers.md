## Bucket: A10_engine_wrappers

C# P/Invoke wrappers over the native engine libs (laplace_core / laplace_dynamics /
laplace_synthesis) + their unit tests. Per invariant 5 these must be THIN marshalling layers.

### Files read (coverage — all 55 read in full)
- [x] app/Laplace.Engine.Core.Tests/ByteAtomsTests.cs
- [x] app/Laplace.Engine.Core.Tests/CodepointPerfcacheTests.cs
- [x] app/Laplace.Engine.Core.Tests/CpuTopologyTests.cs
- [x] app/Laplace.Engine.Core.Tests/Glicko2Tests.cs
- [x] app/Laplace.Engine.Core.Tests/Hash128Tests.cs
- [x] app/Laplace.Engine.Core.Tests/HashComposerTests.cs
- [x] app/Laplace.Engine.Core.Tests/Hilbert128Tests.cs
- [x] app/Laplace.Engine.Core.Tests/IntentStageTests.cs
- [x] app/Laplace.Engine.Core.Tests/Laplace.Engine.Core.Tests.csproj
- [x] app/Laplace.Engine.Core.Tests/MerkleDedupTests.cs
- [x] app/Laplace.Engine.Core.Tests/NativeInteropTests.cs
- [x] app/Laplace.Engine.Core.Tests/PerfcacheTestFixture.cs
- [x] app/Laplace.Engine.Core.Tests/TextDecomposerTests.cs
- [x] app/Laplace.Engine.Core.Tests/TierTreeTests.cs
- [x] app/Laplace.Engine.Core.Tests/TrailingNewlineRoundtripTests.cs
- [x] app/Laplace.Engine.Core.Tests/TrajectoryTests.cs
- [x] app/Laplace.Engine.Core/AttestationAggregatedCellNative.cs
- [x] app/Laplace.Engine.Core/AttestationStagedNative.cs
- [x] app/Laplace.Engine.Core/ByteAtoms.cs
- [x] app/Laplace.Engine.Core/CodepointPerfcache.cs
- [x] app/Laplace.Engine.Core/CodepointRecord.cs
- [x] app/Laplace.Engine.Core/CpuTopology.cs
- [x] app/Laplace.Engine.Core/Glicko2.cs
- [x] app/Laplace.Engine.Core/GrammarDecomposer.cs
- [x] app/Laplace.Engine.Core/GrammarTags.cs
- [x] app/Laplace.Engine.Core/GraphemeFloor.cs
- [x] app/Laplace.Engine.Core/Hash128.cs
- [x] app/Laplace.Engine.Core/HashComposer.cs
- [x] app/Laplace.Engine.Core/Hilbert128.cs
- [x] app/Laplace.Engine.Core/IntentStage.cs
- [x] app/Laplace.Engine.Core/Laplace.Engine.Core.csproj
- [x] app/Laplace.Engine.Core/LaplaceAstNode.cs
- [x] app/Laplace.Engine.Core/LaplaceTag.cs
- [x] app/Laplace.Engine.Core/Math4d.cs
- [x] app/Laplace.Engine.Core/MerkleDedup.cs
- [x] app/Laplace.Engine.Core/NativeInterop.cs
- [x] app/Laplace.Engine.Core/ScoreLaw.cs
- [x] app/Laplace.Engine.Core/SuperFibonacci.cs
- [x] app/Laplace.Engine.Core/TestimonyWalk.cs
- [x] app/Laplace.Engine.Core/TextDecomposer.cs
- [x] app/Laplace.Engine.Core/TierNodeView.cs
- [x] app/Laplace.Engine.Core/TierTree.cs
- [x] app/Laplace.Engine.Core/Trajectory.cs
- [x] app/Laplace.Engine.Core/UnicodeSeed.cs
- [x] app/Laplace.Engine.Dynamics.Tests/Laplace.Engine.Dynamics.Tests.csproj
- [x] app/Laplace.Engine.Dynamics.Tests/NativeInteropTests.cs
- [x] app/Laplace.Engine.Dynamics/Laplace.Engine.Dynamics.csproj
- [x] app/Laplace.Engine.Dynamics/MklAvailability.cs
- [x] app/Laplace.Engine.Dynamics/NativeInterop.cs
- [x] app/Laplace.Engine.Synthesis.Tests/Laplace.Engine.Synthesis.Tests.csproj
- [x] app/Laplace.Engine.Synthesis.Tests/NativeInteropTests.cs
- [x] app/Laplace.Engine.Synthesis.Tests/QkPairsThresholdParityTests.cs
- [x] app/Laplace.Engine.Synthesis/Laplace.Engine.Synthesis.csproj
- [x] app/Laplace.Engine.Synthesis/NativeInterop.cs

General note: the wrappers are, on the whole, genuinely thin and correctly written —
`fixed`-pinned spans, SafeHandle ownership (TierTree, IntentStage), checked length/`%4`/
capacity guards, return-code-to-exception translation, two-call size-then-fill buffer pattern.
The tests are mostly REAL (bitwise parity vs a managed reference for the QK kernels; struct-size
and field-offset assertions; lossless trajectory round-trip incl. ulong.MaxValue/zero; Merkle
order/tier semantics). Findings below are the exceptions.

---

### IntentStage.cs:185-219 — HIGH — invention-violation / fork
CLAIM: `_bulkFreshBypass` is a static, flag-gated SECOND write path for the content-witness emit,
toggled by `SetBulkFreshBypass(bool)` and wired live from `IngestRunner.cs:83`
(`SetBulkFreshBypass(options.BulkFresh)`). When set, `TryAddContentWitness` skips the native
content bank and calls `EmitContentTree(tree, sourceId, ReadOnlySpan<byte>.Empty, ...)`. An empty
bitmap means (per the method's own XML doc at line 246-249) "An empty bitmap emits all nodes" — i.e.
NO dedup descent, every node is emitted. The comment (183-184, 196-198) states uniqueness is then
"guaranteed by ON CONFLICT DO NOTHING / NOT EXISTS." This is two things the invariants forbid at
once: (8) a flag-gated parallel lane in the canonical write path ("the disease"), and (7) the brute
bulk-insert that leans on `ON CONFLICT`/anti-join — exactly the path the invariant says is the
mistake ("Conflicts firing = the descent was skipped"). The trunk-shortcircuit dedup the engine
provides (EmitContentTree WITH a bitmap, via BuildContentTree + probe) is bypassed entirely on this
lane.
VERIFIED: read IntentStage.TryAddContentWitness (188-219) + EmitContentTree doc/impl (244-280);
confirmed the toggle call site at IngestRunner.cs:83 and ResetContentBank at :82. CONFIDENCE: high
that the fork+ON-CONFLICT-reliance exists; med on calling it the single worst architectural call
(there is a real OOM motivation behind it — the bank grows monotonically).

### Glicko2.cs:65-104 (AccumulateGames) — MEDIUM — altitude / correctness
CLAIM: `AccumulateGames` reconstructs `games` individual Glicko2Observation rows in MANAGED code
(splitting an aggregate `sumScoreFp` into `q = sumScoreFp/games` ×(games-1) + remainder) and feeds
them one-by-one to native `Glicko2UpdatePeriod`. This duplicates aggregation logic that already
exists natively (`laplace_attestation_aggregated_build` takes `games` + `sumScoreFp1e9` directly —
declared at NativeInterop.cs:555). Two concrete defects: (a) `games` is `long` but is cast to int
for the buffer (`stackalloc Glicko2Observation[(int)games]` / `new Glicko2Observation[games]`) and
indexed with `obs[(int)i]`; for `games > int.MaxValue` the array alloc throws / the int cast wraps
negative → IndexOutOfRange. Occurrence counts are Glicko game-counts and can be very large. (b) it
allocates and fills an O(games) observation array per call — confirmed hot-ish: CalibratedInverse.cs:28
calls it 4001× per distinct opponent-count `n`, each allocating an n-length array.
VERIFIED: read Glicko2.cs:82-102; confirmed the native aggregated entrypoint at NativeInterop.cs:555-568
and the caller at CalibratedInverse.cs:28. CONFIDENCE: high on the O(games)/int-truncation facts;
med on severity (cold/cached path today, but it is real heavy logic that belongs in the native
aggregated builder).

### NativeInterop.cs (Synthesis) — MEDIUM — correctness (no MKL gate for synthesis)
CLAIM: `TensorSvdTruncate` (48) and `ComputeSubstrateGram` (86) are the MKL-backed synthesis kernels
that (per the sibling auditor / optional-MKL CMake) return -2 when MKL is absent. There is an
MklAvailability.EnsureOrThrow gate for laplace_dynamics (probing bilinear_edges_tile), wired at
Cli/Program.cs:46 — but there is NO analogous availability gate for laplace_synthesis. The wrappers
themselves correctly stay thin (raw `int` passthrough), and FoundryExport.cs DOES `throw` on `rc!=0`
(896/1006/1071/1097/1331/1369) — so the -2 surfaces there as an opaque mid-export
"tensor_svd_truncate rc=-2". However ModelTokenEdgeETL.cs:399-401 does `rank = rc==0 ? (int)r : 0;
return rc;` — on -2 it produces a rank-0 (empty) result and hands rc back to its caller; if that
caller ignores rc the model silently degrades to empty SVD factors. The gap at the wrapper-layer
altitude: synthesis has no up-front "MKL required" gate, unlike dynamics.
VERIFIED: read MklAvailability.cs (only bilinear_edges_tile, only Dynamics); grepped the
TensorSvdTruncate/ComputeSubstrateGram call sites (FoundryExport throws; ModelTokenEdgeETL zeroes
rank on failure). CONFIDENCE: high that no synthesis gate exists; med that the -2 is "silently"
ignored (FoundryExport does check; ModelTokenEdgeETL's zero-on-fail is the soft spot).

### GraphemeFloor.cs:54-60 — MEDIUM — correctness / memory-safety
CLAIM: `LeafByteOffset(int cp)` / `LeafByteLength(int cp)` / `GraphemeOfCodepoint(int cp)`
dereference native arrays (`_leafTextOff[cp]`, `_leafTextLen[cp]`, `_cpToGraph[cp]`) with NO bounds
check against `CpN`. These are public methods; a caller-supplied `cp >= CpN` or negative is an
out-of-bounds read straight into native memory (no managed guard, raw `uint*` indexing). The sibling
floor methods (SpanToGraphemes/LowerBoundCp) clamp; these direct accessors do not.
VERIFIED: read GraphemeFloor.cs; `_leafTextOff` etc. are raw `uint*` from native (33-35), never
length-validated in the indexers. CONFIDENCE: high.

### NativeInterop.cs (Core):548-553 & 593-594 — LOW — fork / dead-ish duplication
CLAIM: `laplace_score_batch_fp` is bound twice — `LaplaceScoreBatchFp` (40-41) and `ScoreBatchFp`
(548-553); `laplace_score_fp` is bound twice — `LaplaceScoreFp` (34-35) and `ScoreFp` (593-594).
Two managed declarations of the same native entrypoint is a small fork (ScoreLaw.cs uses the
`Laplace*` pair; the duplicates near the attestation block are redundant).
VERIFIED: read both declaration sites; identical EntryPoint strings. CONFIDENCE: high (factual
duplication); severity low (functionally harmless, maintenance smell).

### AttestationAggregatedCellNative.cs:7-14 — LOW — correctness (interop layout)
CLAIM: this struct is passed by pointer to native `laplace_attestation_aggregated_batch_build`
(`AttestationAggregatedCellNative* cells`, NativeInterop.cs:536-546) but carries NO
`[StructLayout(LayoutKind.Sequential)]` attribute, unlike every sibling interop struct
(AttestationStagedNative, Glicko2State, etc.). C# defaults structs to Sequential so this is
functionally OK today, but the field order `Hash128 Subject; Hash128 Object; byte ObjectIsNull;
long Games; long SumScoreFp1e9;` inserts 7 bytes of alignment padding after `ObjectIsNull`; if the
native C struct packs/orders differently the marshalled cell array is silently corrupt. Needs a
cross-check against the C header (out of bucket). The inconsistency (missing explicit attribute)
should be fixed regardless.
VERIFIED: read the struct + its only consumer at NativeInterop.cs:536. CONFIDENCE: high on the
missing attribute / padding; low on whether it actually mismatches native (header not in bucket).

### ByteAtoms.cs:10 — LOW — invention-violation (invented namespace), partial
CLAIM: `TypeId = Hash128.OfCanonical("substrate/type/Byte/v1")` is exactly the invented
`substrate/type/X/v1` namespace invariant 6 warns against (concepts should anchor on real external
ids). Caveat: a raw UTF-8 byte atom genuinely has no external registry id (no ILI/ISO/UPOS for "byte
0xC2"), so this is the least-bad case of the pattern — but it is still a hand-coined canonical type
key, and the same shape ("substrate/source/UnicodeDecomposer/v1") recurs in tests (Hash128Tests.cs:55).
Worth flagging because the pattern is what the convergence-index work is trying to eliminate.
VERIFIED: read ByteAtoms.cs:10; cross-checked invariant 6. CONFIDENCE: high it matches the
flagged pattern; low that bytes are a true violation (no external anchor exists for them).

### NativeInterop.cs (Dynamics):18-21 — LOW — correctness (swallowed init rc)
CLAIM: the static constructor runs `_ = LaplaceDynamicsInit();` and discards the return code. If
init fails (e.g. MKL/TBB threading runtime not initializable) the failure is silently dropped; the
first real kernel call then fails with a less-diagnosable error. (The dedicated MklAvailability gate
mitigates this for the wired Cli path, but the cctor itself hides its own status.)
VERIFIED: read Dynamics/NativeInterop.cs:18-21. CONFIDENCE: high.

### IntentStage.cs:157-168 — LOW — correctness (under-fill not signalled as error)
CLAIM: the `EmitCopyBinary(table, Span<byte> dest)` overload calls the native emit with `dest`'s
capacity and returns the `required` byte count, but does NOT throw when `required > dest.Length` —
it just returns the larger number. The caller must remember to compare return vs `dest.Length`; a
caller that treats the return as "bytes written" would read a partially/empty-filled buffer. The
single-arg overload (133-147) is safe (sizes first). Minor; the span overload is a footgun.
VERIFIED: read both overloads. CONFIDENCE: high (behavior), low (severity — depends on caller).

### INFO — tests are real (no fake-test findings)
The interop tests assert genuine invariants, not tautologies:
- QkPairsThresholdParityTests.cs — bitwise (DoubleToInt64Bits) parity of the native QK kernels vs a
  Neumaier-summation managed reference, plus pruned-vs-allpairs and cached-projection parity.
- CodepointPerfcacheTests.cs / Hash128Tests / Hilbert128Tests / TierTreeTests — struct size + exact
  C field-offset reads, Blake3 known-answer (empty-input vector), Merkle order-matters /
  tier-is-not-identity (confirms tier is NOT in the id — invariant 3), 16-byte layout.
- TrajectoryTests.cs — lossless Build→Constituents round-trip including ulong.MaxValue and zero ids.
- Version tests (`Assert.Equal("0.1.0", ...)`) are weak but harmless smoke checks that the native lib
  loads and is callable.

### Bucket summary
- HIGH: 1 (IntentStage `_bulkFreshBypass` flag-gated brute write lane relying on ON CONFLICT)
- MEDIUM: 3 (Glicko2.AccumulateGames managed aggregation + int-truncation; no synthesis-side MKL
  gate; GraphemeFloor unchecked native index)
- LOW: 5 (duplicate score P/Invokes; AttestationAggregatedCellNative missing StructLayout;
  invented "substrate/type/Byte/v1" namespace; Dynamics cctor swallows init rc; EmitCopyBinary span
  overload under-fill)
- INFO: tests verified real.

Single worst issue: **IntentStage.cs:185-219 `_bulkFreshBypass`** — a live, ingest-wired second
write path that abandons the merkle trunk-shortcircuit dedup and leans on DB `ON CONFLICT`,
violating both invariant 7 (no ON CONFLICT / dedup-before-compute) and invariant 8 (no flag-gated
parallel lanes).
