# ADR 0053: Perfcache compile-time build pipeline — CMake stage producing the binary as a deployment artifact

## Status

**Accepted** — 2026-05-24 (amended 2026-05-25 with as-built deltas; see Amendment below)
**Authors:** Anthony Hart

> **Amendment (2026-05-25) — as-built.** The pipeline landed with the shape
> below, but several specifics in the original Decision drifted during
> implementation. The authoritative format contract is now
> `engine/core/include/laplace/core/perfcache_format.h` (with `_Static_assert`
> guards on struct sizes); this ADR records the deltas:
>
> - **Magic** is `0x4652504Cu` (`"LPRF"` little-endian), not the originally
>   stated `0x4654504C` — that hex spelled `"LPTF"` (a transposition bug).
> - **Format version is 2**, not 1. v2 adds a **section directory** to the
>   header for the NFC canonical-decomposition + composition side-tables the
>   runtime NFC path needs (variable-length, so they don't fit a fixed-width
>   record).
> - **Record is 80 bytes**, not 64. `coord[4]` f64 (32) + hilbert128 (16) +
>   hash128 (16) is already 64 before `codepoint`/`uca_order`/`flags`/`_pad`;
>   the original 64 was arithmetically impossible. The `flags` u32 packs
>   GB/WB/SB/InCB/CCC (the scalar UAX#29/UAX#15 properties the state machines
>   consult); fixed value-ids live in `ucd_property_values.h`.
> - **Header is 128 bytes** (two cache lines), not 64 — it carries the
>   section directory (records / decomp-records / decomp-data / compose-records
>   offsets + counts) plus `ucd_hash` and reserved padding.
> - **Emit tool is `laplace_ucd_tables_emit`** (CLI: `--ucdxml --ducet
>   --ucd-version --uca-version --output`), reading **UCDXML (UAX#42) via
>   libxml2 SAX** + **DUCET (`allkeys.txt`) for UCA collation rank** — not the
>   `--ucd-path/--uca-path` form, and not a `laplace_core_internal` static
>   library. To break the build-graph cycle the tool compiles the pure math
>   kernels (`super_fibonacci.c` / `hilbert4d.c` / `hash128.c`) directly as
>   source instead of linking a core variant.
> - **`uca_order` is the DUCET collation rank**: the emitter ranks all
>   1,114,112 codepoints by their DUCET sort key (UCA §10.1.3 implicit weights
>   for codepoints absent from `allkeys.txt`), then assigns super-Fibonacci S³
>   coordinates **by that rank** — so collation-adjacent codepoints land near
>   each other on the glome.
> - **liblaplace_core is fully decoupled from UCD source**: it compiles **no**
>   generated tables and has no UCD/UCA build dependency. The runtime
>   `codepoint_table` loader mmaps the blob; the UAX#29/UAX#15 state machines
>   read every property through `codepoint_table_*(cp)`. (The earlier
>   `ucd_tables.generated.{h,c}` codegen that compiled tables *into* the .so is
>   retired.) Verified: `ldd liblaplace_core.so` shows no MKL/TBB/xml/blake3
>   runtime dep, and `nm -D` shows no `laplace_ucd_*` symbols.
> - **Deployment-profile embedding (`.rodata` via objcopy, `LAPLACE_PERFCACHE_
>   DEPLOYMENT` modes) is not yet wired.** The blob currently installs
>   side-by-side at `share/laplace/laplace_t0_perfcache_<ver>.bin` and is
>   consumed by the conformance test suite (a GoogleTest global environment
>   mmaps it before any test runs). `codepoint_table_load_perfcache(NULL)`
>   reserves the future embedded-section path (returns -1 until wired).
> - **Justfile target is `laplace_t0_perfcache`** (`just build-perfcache` runs
>   `cmake --build build --target laplace_t0_perfcache`). The
>   `laplace_verify_perfcache_vs_db` cross-verify target is still future work
>   (needs the DB-seed half of UnicodeDecomposer).
> - **Pinned version is UCD/UCA 17.0.0** (latest stable), not the 16.0.0 shown
>   in the original Justfile snippet — per RULES R7, the pin MUST be a stable
>   release (18.0.0 is alpha until ~2026-09).

## Context

Per [ADR 0006](0006-perfcache-and-db-seed-siblings.md): the T0 perf-cache + the DB seed are sibling artifacts, both derived from UCD/DUCET via `laplace_unicode_seed_compute` — neither feeds the other. `UnicodeDecomposer` seeds the DB; the emit tool builds the blob. Per [RULES.md R7](../../RULES.md): same UCD + UCA version → byte-identical perfcache on every machine.

Per the 2026-05-24 conversation: **the perfcache is a compile-time artifact, not an install-time one.** *"The unicode perf-cache generation is a one-time build and then compile (because we modularize and make it selectively deployable (think embedded devices like a raspberry pi with limited ROM))."*

The deployment shape per the conversation has two profiles:

- **Full server**: ships `liblaplace_core.so` + perfcache binary + DB seed (loaded into the running custom PG cluster at `/opt/laplace/pgsql-18/`)
- **Embedded** (Raspberry Pi / edge): ships `liblaplace_core.so` + perfcache binary, no DB, no `liblaplace_dynamics.so` (MKL is huge), no `liblaplace_synthesis.so` (writers + extractors aren't needed for read-only edge inference), no PG extension

In both profiles the perfcache must arrive on disk (or embedded in the binary) at deployment time, *not* generated by the deployment target itself. The deployment target may be a CPU-constrained edge device that can't run UnicodeDecomposer's full ingest in reasonable time + doesn't have `/vault/Data/Unicode/` (37 GB) staged.

That means: the perfcache binary is **built once at build time** by the unified CMake tree per [ADR 0032](0032-unified-cmake-build-pipeline.md) + [ADR 0038](0038-unified-deps-cmake-pipeline-gcc-toolchain.md), cached on subsequent rebuilds (stamp-based like the `build-deps` pattern), and **shipped as either a side-by-side file or a `.rodata` section in `liblaplace_core.so`** depending on the deployment profile.

The currently-stubbed `codepoint_table_build_from_ucd` / `codepoint_table_load_perfcache` / `codepoint_table_lookup` functions in `engine/core/src/codepoint_table.c` (22 LOC stub) await this build pipeline. Until it lands, the runtime `lookup` path has no bytes to load.

The 3 fabricated shell scripts (`scripts/build-perfcache.sh`, `scripts/seed-t0.sh`, `scripts/verify-perfcache.sh`) deleted 2026-05-24 had the build framed as a runtime invocation. Wrong shape. This ADR replaces them with the correct compile-time integration.

## Decision

**The perfcache binary is produced by a CMake stage during `cmake --build`, integrated into the top-level CMake tree per ADR 0032, and shipped per the deployment-profile rules in [ADR 0054 selective deployment profiles](0054-selective-deployment-profiles.md).**

### Build pipeline shape

```cmake
# engine/core/CMakeLists.txt (new perfcache section)

# Stage 1: Build the perfcache-emit tool — a small C++ executable that
# UnicodeDecomposer's compile-time invocation runs to produce perfcache.bin.
# Static-links liblaplace_core (without itself depending on a populated
# codepoint_table — bootstrap concern). Reads UCD/UCA from $LAPLACE_EXTERNAL/ucd/
# (or whichever path the project pins via -DLAPLACE_UCD_PATH=...).
add_executable(laplace_perfcache_emit
    src/perfcache_emit_tool.cpp)
target_link_libraries(laplace_perfcache_emit PRIVATE
    laplace_core_internal)  # internal-only library variant without codepoint_table_lookup

# Stage 2: Invoke the emit tool at build time to produce perfcache.bin.
# Stamp-cached: re-builds only when $LAPLACE_UCD_PATH content or the emit
# tool itself changes. Same pattern as build-deps.
set(LAPLACE_PERFCACHE_OUTPUT "${CMAKE_BINARY_DIR}/perfcache.bin")
add_custom_command(
    OUTPUT  "${LAPLACE_PERFCACHE_OUTPUT}"
    COMMAND laplace_perfcache_emit
            --ucd-path "${LAPLACE_UCD_PATH}"
            --uca-path "${LAPLACE_UCA_PATH}"
            --unicode-version "${LAPLACE_UNICODE_VERSION}"
            --output "${LAPLACE_PERFCACHE_OUTPUT}"
    DEPENDS laplace_perfcache_emit "${LAPLACE_UCD_PATH}/.gitkeep"
    COMMENT "Building Laplace perfcache binary from UCD ${LAPLACE_UNICODE_VERSION}"
    VERBATIM)
add_custom_target(laplace_perfcache ALL DEPENDS "${LAPLACE_PERFCACHE_OUTPUT}")

# Stage 3: Embedding mechanism — selected by LAPLACE_PERFCACHE_DEPLOYMENT
# per ADR 0054.
if(LAPLACE_PERFCACHE_DEPLOYMENT STREQUAL "EMBEDDED_IN_BINARY")
    # Embed perfcache.bin into liblaplace_core.so as a .rodata section
    # via objcopy. The runtime loader sees a constant byte array at a
    # known symbol (_binary_perfcache_bin_start / _binary_perfcache_bin_end).
    # codepoint_table_load_perfcache(NULL) detects the embedded section
    # and uses it directly; codepoint_table_load_perfcache(path) loads
    # from filesystem instead.
    add_custom_command(
        OUTPUT "${CMAKE_BINARY_DIR}/perfcache.o"
        COMMAND ${CMAKE_OBJCOPY}
                --input-target binary
                --output-target ${LAPLACE_OBJCOPY_TARGET}
                --binary-architecture ${LAPLACE_OBJCOPY_ARCH}
                "${LAPLACE_PERFCACHE_OUTPUT}"
                "${CMAKE_BINARY_DIR}/perfcache.o"
        DEPENDS "${LAPLACE_PERFCACHE_OUTPUT}"
        WORKING_DIRECTORY "${CMAKE_BINARY_DIR}")
    target_sources(laplace_core PRIVATE "${CMAKE_BINARY_DIR}/perfcache.o")
elseif(LAPLACE_PERFCACHE_DEPLOYMENT STREQUAL "ALONGSIDE_BINARY")
    # Install perfcache.bin as a side-by-side file at
    # ${CMAKE_INSTALL_PREFIX}/share/laplace/perfcache.bin. codepoint_table_
    # load_perfcache(path) mmap's it at runtime.
    install(FILES "${LAPLACE_PERFCACHE_OUTPUT}"
            DESTINATION share/laplace
            PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS})
elseif(LAPLACE_PERFCACHE_DEPLOYMENT STREQUAL "BOTH")
    # Both embedded and side-by-side: codepoint_table_load_perfcache(NULL)
    # uses the embedded copy; codepoint_table_load_perfcache(path) overrides
    # with the on-disk one (useful for hot-swap testing of perfcache rebuilds).
    # ... combination of the two above
elseif(LAPLACE_PERFCACHE_DEPLOYMENT STREQUAL "OFF")
    # No perfcache embedded or shipped — runtime must supply via
    # codepoint_table_load_perfcache(path). Used for testing the load-from-
    # arbitrary-path path + for development where perfcache rebuilds
    # frequently.
endif()
```

### Justfile integration

```just
# Build perfcache as part of the engine build (default — invoked transitively)
build:
    cmake -B build -G Ninja \
          -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
          -DLAPLACE_UCD_PATH=${LAPLACE_UCD_PATH:-/opt/laplace/external/ucd} \
          -DLAPLACE_UNICODE_VERSION=${LAPLACE_UNICODE_VERSION:-16.0.0} \
          -DLAPLACE_PERFCACHE_DEPLOYMENT=${LAPLACE_PERFCACHE_DEPLOYMENT:-BOTH} \
          # ... other flags per existing Justfile
    cmake --build build

# Just the perfcache (for iterating on UnicodeDecomposer's emit logic)
build-perfcache:
    cmake --build build --target laplace_perfcache

# Verify perfcache integrity (compares the emitted bytes to the DB seed values per ADR 0006)
# Real implementation lands once Chunk 3's DB seed path is live; this replaces
# the deleted scripts/verify-perfcache.sh stub.
verify-perfcache:
    cmake --build build --target laplace_verify_perfcache_vs_db
```

### Determinism contract

Per [RULES.md R7](../../RULES.md): same `$LAPLACE_UCD_PATH` content + same `$LAPLACE_UNICODE_VERSION` + same emit-tool source → byte-identical perfcache.bin on every machine. Verified by CI gate: build perfcache on two different runners (matching toolchain), `cmp` the resulting binaries → must match exactly. Drift caught at PR time.

The cross-verification against the DB seed (per ADR 0006 — "Perf-cache vs DB seed cross-verified byte-for-byte") happens in a separate stage that runs at install time against a populated DB; replaces the deleted `scripts/verify-perfcache.sh` stub. That cross-verify is a CI integration test, not a build-time gate (since the DB seed requires a running PG).

### Perfcache binary format (locked here for the first time)

```text
Header (64 bytes, cache-line aligned):
    [0..4)    magic       u32   ASCII "LPRF" (0x4654504C little-endian)
    [4..8)    version     u32   format version (currently 1)
    [8..16)   ucd_version utf8 (8-byte fixed length, zero-padded; e.g., "16.0.0\0\0")
    [16..24)  uca_version utf8 (8-byte fixed length)
    [24..32)  record_count u64  number of codepoint_entry_t records (= 1,114,112)
    [32..40)  record_size  u64  bytes per record (= 64, sizeof(codepoint_entry_t))
    [40..56)  ucd_hash     u128 BLAKE3-128 of the UCD source bytes (deterministic fingerprint)
    [56..64)  reserved     u64  zero-padding

Records (record_count * 64 bytes):
    Each record is one codepoint_entry_t per
    engine/core/include/laplace/core/codepoint_table.h:
        codepoint    u32
        uca_order    u32
        coord[4]     f64 (XYZM, matches POINT4D layout)
        hilbert      u128 (16 bytes)
        hash         u128 (16 bytes, BLAKE3-128 of canonical codepoint bytes)
        flags        u32
        _pad         u32

Trailer (16 bytes):
    [-16..0)  body_crc    u128  BLAKE3-128 of (header || records); detects corruption
```

Endianness: little-endian on x86_64. Embedded ARM targets would need byteswap-on-load if/when they ship.

### Validation at load

`codepoint_table_load_perfcache(path)` (or the embedded variant):
1. Read header → verify magic + version.
2. Verify `ucd_version` + `uca_version` match `LAPLACE_UNICODE_VERSION` build-time constant.
3. Verify `record_count == 1114112` + `record_size == 64`.
4. Compute BLAKE3-128 of (header || records); compare to trailer's body_crc — refuse to load on mismatch.
5. mmap the file (or `.rodata` section) as a `const codepoint_entry_t*` array of length `record_count`.
6. Return.

Load is ~ms for the integrity check + mmap; subsequent `codepoint_table_lookup(cp)` is L2/L3 cache hit per ADR 0006 + ADR 0048.

## Consequences

- **The perfcache becomes a normal CMake artifact**, cached + reproducible + verifiable like any other build output.
- **`codepoint_table.c` stub can be replaced with real impl** that loads from either the embedded section or a side-by-side file path. Story 3.7/3.8/3.9 per Chunk 3 (#3) closes against this.
- **Embedded deployment becomes feasible**: ship liblaplace_core.so with perfcache linked in → small read-only inference binary suitable for edge.
- **Build-time determinism CI gate enforces R7** across builders.
- **Justfile gets `just build-perfcache` + `just verify-perfcache` as real targets** (replacing the deleted exit-1 stubs).
- **The format is locked + versioned**, so future format changes follow a versioning protocol (bump version field + version-aware load path) instead of breaking compatibility silently.
- **UnicodeDecomposer's perfcache-emit path becomes a build-time tool** (`laplace_perfcache_emit`), not a runtime ingestion job. The DB-seed half of UnicodeDecomposer's responsibility (per ADR 0006 sibling) still runs as an install-time `IngestRunner` invocation per [ADR 0052](0052-ingest-pipeline-orchestration.md).

## Alternatives considered

- **Generate perfcache at install time on each deploy host.** Rejected — embedded targets can't run the full UnicodeDecomposer ingest (no `/vault/Data/Unicode/`, possibly no RAM/CPU budget). Compile-time generation makes the perfcache a portable deployment artifact.
- **Skip embedding; always ship side-by-side.** Rejected — embedded deployments benefit from one-file-binary shipment + `.rodata` section means one-page-fault startup. `BOTH` mode is the right default; `ALONGSIDE_BINARY` is the simpler fallback.
- **Use a third-party format (FlatBuffers / Cap'n Proto / etc.) for the perfcache.** Rejected — the format is dead-simple (one fixed-size struct × 1.1M records); third-party serialization adds a dependency for no win. Custom format is ~30 LOC of read/write.
- **Generate perfcache from the DB seed** (instead of independently from UCD). Rejected — violates [ADR 0006](0006-perfcache-and-db-seed-siblings.md) sibling invariant. The cross-verification step depends on independent derivation.
- **Skip the build-time determinism CI gate.** Rejected — without it, drift between runners produces inconsistent perfcache and breaks R7 cross-machine reproducibility.

## References

- [RULES.md R6](../../RULES.md) — DB as dumb columnar store
- [RULES.md R7](../../RULES.md) — determinism by construction
- [STANDARDS.md Build standards](../../STANDARDS.md)
- [STANDARDS.md File and directory layout](../../STANDARDS.md)
- [ADR 0006](0006-perfcache-and-db-seed-siblings.md) — perfcache + DB seed sibling invariant
- [ADR 0024](0024-engine-modularization.md) — engine modularization (perfcache lives in liblaplace_core)
- [ADR 0032](0032-unified-cmake-build-pipeline.md) — unified CMake build (the tree this stage integrates into)
- [ADR 0033](0033-all-deps-as-submodules.md) — all-deps-as-submodules (UCD submodule under external/)
- [ADR 0038](0038-unified-deps-cmake-pipeline-gcc-toolchain.md) — stamp-cached ExternalProject pattern (this ADR follows the same caching shape)
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (perfcache is Stage 5 sibling; DB seed is the other half)
- [ADR 0046](0046-persistent-submodule-cache.md) — `/opt/laplace/external/` as canonical dep checkouts (UCD path source)
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — T0 lookup consumer
- [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) — DB-seed half of Stage 5 runs through this
- [ADR 0054 selective deployment profiles](0054-selective-deployment-profiles.md) — chooses LAPLACE_PERFCACHE_DEPLOYMENT mode
- [Issue #3 Chunk 3](https://github.com/SaltyPatron/Laplace/issues/3) — perfcache + T0 seed sibling deliverable
- [Issue #183 UnicodeDecomposer](https://github.com/SaltyPatron/Laplace/issues/183) — owns the emit tool implementation
- `engine/core/include/laplace/core/codepoint_table.h` — codepoint_entry_t struct (the perfcache record format)
- Conversation 2026-05-24: perfcache compile-time + selectively-deployable + embedded-target framing
