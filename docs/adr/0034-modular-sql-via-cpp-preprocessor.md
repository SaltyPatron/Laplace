# ADR 0034: Modular extension SQL via `.sql.in` + C preprocessor (PostGIS pattern)

## Status

**Accepted** — 2026-05-22

Companion to [ADR 0025](0025-pg-extension-modularization.md) (PG extension modularization) and [ADR 0032](0032-unified-cmake-build-pipeline.md) (unified CMake pipeline). Where 0025 decided we have two extensions, this ADR decides how each extension's SQL is organized at the source level.

## Context

Each of our two extensions has ~10–15 functions plus types, opclasses, aggregates, and (in `laplace_substrate`'s case) tables and indexes. Per [DESIGN.md](../../DESIGN.md):

- `laplace_geom`: `hash128` type + I/O + opclass, Hilbert encoder/decoder, mantissa pack/unpack, the `ST_*_4d` function family, the `laplace_gist_s3_ops` GIST opclass.
- `laplace_substrate`: the three substrate tables (entities/physicalities/attestations), their CHECK constraints, the multi-index strategy, the Glicko-2 SFUNC + FINALFUNC + `CREATE AGGREGATE`, the cascade A* SRF, the SP-GiST + BRIN opclasses.

Held in a single hand-edited `.sql` file per extension, these would be 1000+-line monoliths. Concretely, a single-file layout fights us in three ways:

1. **Review:** diffs across a 1000-line SQL file lose locality. A change to one opclass touches the same file as a change to the Glicko-2 aggregate.
2. **Compose:** there's no way to reuse macro-level patterns (e.g., function-volatility-modifier shortcuts, version-check guards). Every CREATE FUNCTION repeats `IMMUTABLE LEAKPROOF PARALLEL SAFE` verbatim.
3. **Conditional content:** if a future build wants to conditionally include AVX-512-specialized opclass functions only when `LAPLACE_TARGET_ISA=AVX512`, there's no mechanism in plain SQL to express that.

PostGIS — the most architecturally complex PostgreSQL extension in production, sitting in our tree as `external/postgis/` per [ADR 0033](0033-all-deps-as-submodules.md) — solves all three of these with a `.sql.in` + C preprocessor pipeline that's been stable for over a decade. The mechanism is in our submodule and we can inspect it directly:

- Source files are named `*.sql.in`. They contain SQL mixed with C-preprocessor directives (`#include`, `#define`, `#ifdef`).
- A shared header `sqldefines.h` defines macros that all `.sql.in` files `#include`.
- A build step runs the C preprocessor over each `.sql.in`, then filters the output to produce the `.sql` artifact PostgreSQL actually loads.

The authoritative invocation (from `external/postgis/configure.ac:58` + `external/postgis/postgis/Makefile.in:242-249`):

```makefile
SQLPP = cpp -traditional-cpp -w -P -Upixel -Ubool

%.sql: %.sql.in
    $(SQLPP) -I<includedir> $< > $@.tmp
    grep -v '^#' $@.tmp | \
        perl -lpe "s'MODULE_PATHNAME'\$(MODULEPATH)'g" | \
        perl -lpe "s'@extschema@\.''g" > $@
```

What each piece does:

- `cpp -traditional-cpp`: K&R-style C preprocessing. Avoids the modern cpp's token-pasting / stringizing behaviors that would mangle SQL syntax.
- `-w -P`: suppress warnings; no line markers in output.
- `-Upixel -Ubool`: undefine macros that PostgreSQL or system headers might leak; both are common SQL identifiers.
- `grep -v '^#'`: strip stray `#` lines (cpp leaves some even with `-P`).
- First `perl s/MODULE_PATHNAME/...`: substitute the placeholder PostgreSQL expects in `CREATE FUNCTION ... LANGUAGE 'C' AS 'MODULE_PATHNAME', 'fn_name'` with the actual `$libdir/<extension_name>` path.
- Second `perl s/@extschema@\.//`: strip explicit schema prefixes so PostgreSQL's relocatable-extension machinery can place objects in the resolved schema.

## Decision

Adopt the PostGIS `.sql.in` + cpp preprocessor pattern wholesale for both `laplace_geom` and `laplace_substrate`. Locked details below.

### Source file naming

| File | Role |
|---|---|
| `extension/<name>/sql/sqldefines.h.in` | Shared macros (CPP-substituted at build time for version strings, paths) |
| `extension/<name>/sql/<name>.sql.in` | Main entry; `#include`s the numbered module files in order |
| `extension/<name>/sql/<NN>_<module>.sql.in` | Module file. Numeric prefix locks load order. |
| `extension/<name>/sql/uninstall_<name>.sql.in` | Drop script (mirrors install in reverse) |

The built artifact `<name>--<version>.sql` lands in the build tree and is what `make install` (or in our case, `cmake --install`) places under `<pgprefix>/share/postgresql/extension/`.

### Module breakdown

**`laplace_geom`** (general-purpose 4D PostGIS additions):

```
extension/laplace_geom/sql/
├── sqldefines.h.in
├── laplace_geom.sql.in              # entry — #includes the modules below
├── 01_meta.sql.in                   # laplace_geom_version() + identity checks
├── 02_hash128_type.sql.in           # CREATE TYPE hash128 + I/O functions
├── 03_hash128_ops.sql.in            # laplace_btree_hash128_ops opclass (ADR 0029)
├── 04_hilbert.sql.in                # laplace_hilbert_encode/decode
├── 05_mantissa.sql.in               # laplace_mantissa_pack/unpack
├── 06_st_4d.sql.in                  # ST_*_4d function family
├── 07_s3_opclass.sql.in             # laplace_gist_s3_ops opclass (ADR 0029)
└── uninstall_laplace_geom.sql.in
```

**`laplace_substrate`** (substrate domain — entities, attestations, dynamics):

```
extension/laplace_substrate/sql/
├── sqldefines.h.in
├── laplace_substrate.sql.in         # entry
├── 01_schema.sql.in                 # CREATE SCHEMA laplace
├── 02_entities.sql.in               # entities table + checks + pg_extension_config_dump
├── 03_physicalities.sql.in          # physicalities table
├── 04_attestations.sql.in           # attestations table + dedup constraint
├── 05_indexes.sql.in                # all CREATE INDEX (per DESIGN.md V)
├── 06_glicko2.sql.in                # SFUNC + FINALFUNC + CREATE AGGREGATE
├── 07_cascade.sql.in                # laplace_astar_path SRF
├── 08_sp_trajectory_ops.sql.in      # SP-GiST opclass (ADR 0029)
├── 09_brin_tier_ops.sql.in          # BRIN opclass (ADR 0029)
└── uninstall_laplace_substrate.sql.in
```

Numeric prefixes lock load order. Extension SQL runs in a single transaction; DDL ordering matters (you can't `CREATE INDEX` before `CREATE TABLE`; you can't reference an opclass before defining its support functions).

### Build invocation

Per [ADR 0032](0032-unified-cmake-build-pipeline.md) the extension build is CMake-driven, not PGXS. The preprocessor invocation lives in `extension/<name>/CMakeLists.txt`:

```cmake
find_program(SQLPP NAMES cpp gpp_
             DOC "C preprocessor used for .sql.in → .sql preprocessing")

set(LAPLACE_GEOM_MODULES
    01_meta.sql.in
    02_hash128_type.sql.in
    03_hash128_ops.sql.in
    04_hilbert.sql.in
    05_mantissa.sql.in
    06_st_4d.sql.in
    07_s3_opclass.sql.in
)

add_custom_command(
    OUTPUT  ${CMAKE_CURRENT_BINARY_DIR}/laplace_geom--0.1.0.sql
    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/sql/laplace_geom.sql.in
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/sqldefines.h.in
            ${LAPLACE_GEOM_MODULES}
    COMMAND ${SQLPP} -traditional-cpp -w -P -Upixel -Ubool
            -I${CMAKE_CURRENT_SOURCE_DIR}/sql
            ${CMAKE_CURRENT_SOURCE_DIR}/sql/laplace_geom.sql.in
            > ${CMAKE_CURRENT_BINARY_DIR}/laplace_geom.tmp
    COMMAND grep -v "^#" ${CMAKE_CURRENT_BINARY_DIR}/laplace_geom.tmp
            | perl -lpe "s'MODULE_PATHNAME'$$libdir/laplace_geom'g"
            | perl -lpe "s'@extschema@\\.''g"
            > ${CMAKE_CURRENT_BINARY_DIR}/laplace_geom--0.1.0.sql
    COMMENT "Preprocessing laplace_geom SQL modules"
)

install(FILES ${CMAKE_CURRENT_BINARY_DIR}/laplace_geom--0.1.0.sql
        DESTINATION ${LAPLACE_PG_PREFIX}/share/postgresql/extension)
```

The SQLPP detection prefers Intel's preprocessor when available (icx supports `-E` for preprocessing), falling back to system `cpp`. Both work; we don't depend on Intel-specific behavior at the preprocessor layer.

### `sqldefines.h.in` content

Shared macros that simplify SQL. Example content:

```c
/* Function modifiers — use as: CREATE FUNCTION ... LAPLACE_VOLATILE_STRICT; */
#define LAPLACE_IMMUTABLE_STRICT   IMMUTABLE STRICT PARALLEL SAFE
#define LAPLACE_IMMUTABLE          IMMUTABLE        PARALLEL SAFE
#define LAPLACE_STABLE_STRICT      STABLE   STRICT  PARALLEL SAFE
#define LAPLACE_VOLATILE_STRICT    VOLATILE STRICT

/* PostgreSQL version gate — fail at install time if PG version too low. */
#define LAPLACE_MIN_PG_MAJOR 18

/* Extension version — sourced from CMake at preprocessing time. */
#define LAPLACE_GEOM_VERSION     "@LAPLACE_GEOM_VERSION@"
```

CMake `configure_file()` produces `sqldefines.h` from `sqldefines.h.in`, substituting `@LAPLACE_GEOM_VERSION@` etc. The resulting `sqldefines.h` is what `.sql.in` files `#include`.

### Versioning + upgrade scripts

A future migration from `0.1.0` → `0.2.0` follows PostgreSQL convention: a new `laplace_geom--0.1.0--0.2.0.sql` script ships alongside `laplace_geom--0.2.0.sql`. Both are produced from `.sql.in` modules — the upgrade script's source files (e.g., `upgrade/0.1.0--0.2.0.sql.in`) `#include` the same shared header and reuse macros.

We defer the upgrade-script story until Chunk 8+ — the substrate is pre-1.0 and we accept clean-start migrations until then (per Story B.11).

## Consequences

- **Per-module diffs.** Changes to the Glicko-2 aggregate touch `06_glicko2.sql.in` only. Changes to the SP-GiST opclass touch `08_sp_trajectory_ops.sql.in` only. PR review locality restored.
- **Shared macros, single source of truth.** Function-volatility shortcuts, version gates, `MODULE_PATHNAME` references are defined once in `sqldefines.h.in`. A discipline change (e.g., adopting LEAKPROOF on a class of helpers) is a one-line edit.
- **Conditional content available.** If we ever want AVX-512-specialized opclass functions, `#ifdef LAPLACE_TARGET_AVX512` works at the SQL layer just like it does at the C layer. We don't expect to use this immediately, but the mechanism is now there.
- **PostgreSQL convention preserved.** The built `laplace_geom--0.1.0.sql` is a single conventional SQL artifact. PostgreSQL doesn't know we used a preprocessor; it just loads the resulting SQL. No special PG version requirements, no runtime overhead.
- **No hand-edits of the built artifact.** Per RULES.md R17 (added by this ADR), the `<name>--<version>.sql` artifact in the build tree is never hand-edited. The source-of-truth is `.sql.in`. CI verifies the built artifact is reproducible from the `.sql.in` modules.
- **PostGIS lineage.** When something breaks, the diagnostic path goes through PostGIS's well-trodden build patterns. The submodule (`external/postgis/`) is right there as reference.
- **Build dependency on `cpp`.** Standard on every Linux distribution; provided by `build-essential` (already in `bootstrap_build_environment`). Intel's `icx -E` also works as a drop-in.
- **Cost: build time.** CPP preprocessing is microseconds per module file. Negligible.
- **Cost: a small mental model shift.** New contributors writing SQL must know that `.sql.in` is where you edit, not the `<name>--<version>.sql` in the build tree. Mitigated by RULES.md R17 + clearly-named modules + a CMake target failure if someone edits the built artifact directly (verified via timestamp + SHA on CI).

## Alternatives considered

- **Single hand-edited `.sql` per extension.** Rejected — does not scale past ~10 functions; review locality lost; no macro shortcuts; no conditional inclusion.
- **Plain Makefile `cat` concatenation.** Considered. Rejected — gives module-split without macros or conditional inclusion. The cost of upgrading to cpp later (when we want a macro shortcut or ISA-conditional opclass) is higher than the cost of starting with cpp now.
- **m4 preprocessor.** Considered. Rejected — m4 is more powerful but its quoting story is famously brittle. `cpp -traditional-cpp` is "good enough" with simpler semantics. PostGIS's choice of cpp over m4 is informative.
- **Build SQL via a code generator (Python, Perl).** Considered. Rejected — adds a runtime dependency where cpp already does what we need. Code-generator approaches are common in newer extensions (e.g., TimescaleDB uses a Python codegen layer); they're better when you need *semantic* templating (e.g., generating per-type variants). We don't have that need.
- **PL/pgSQL DO-blocks for orchestration.** Considered. Rejected — extension SQL can't use transaction control; DO-blocks would interleave with CREATE FUNCTION calls awkwardly.

## References

- [PostGIS submodule — Makefile.in:242-249](../../external/postgis/postgis/Makefile.in) — the authoritative SQLPP recipe
- [PostGIS submodule — configure.ac:58](../../external/postgis/configure.ac) — SQLPP variable definition
- [ADR 0025 — PG extension modularization](0025-pg-extension-modularization.md) — sibling decision: two extensions
- [ADR 0029 — Custom indexing strategy](0029-custom-indexing-strategy.md) — opclasses that motivate the modular SQL structure
- [ADR 0032 — Unified CMake build pipeline](0032-unified-cmake-build-pipeline.md) — sibling decision: PGXS retired
- [ADR 0033 — All direct deps as submodules](0033-all-deps-as-submodules.md) — sibling decision: PostGIS in our tree, inspectable
- [PostgreSQL — Packaging Related Objects into an Extension](https://www.postgresql.org/docs/current/extend-extensions.html)
- [RULES.md R17](../../RULES.md) — added by this ADR: don't hand-edit the built `<name>--<version>.sql`
