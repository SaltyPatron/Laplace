# Tree-sitter grammar wiring

Tree-sitter is the container-format parsing engine for the substrate: its only job is to
strip raw content out of structured formats before handing off to the tiered decomposer
pipeline (see `grammar_registry.c`). This directory is where grammar object libraries are
declared for the build.

## Where grammar sources come from

**35 grammars are wired** into `CMakeLists.txt` here and registered in
`engine/core/src/grammar_registry.c`. They come from three places:

1. **Vendor submodules (32 grammars, the normal case).** Built directly from
   `external/tree-sitter-grammars/<repo>/src` — the upstream repo's own committed
   `parser.c`/`scanner.c`. No generation step, no drift. (Note some submodules provide
   multiple grammars: `tree-sitter-csv` provides both `csv` and `tsv`,
   `tree-sitter-typescript`/`tree-sitter-xml`/`tree-sitter-php`/`tree-sitter-markdown`
   build from subdirectories.)

2. **`generated/sql` and `generated/swift` (checked-in generated parsers).** These are
   NOT forks. Upstream `derekstride/tree-sitter-sql` and `alex-pinkus/tree-sitter-swift`
   deliberately gitignore `src/parser.c` — it must be generated from `grammar.js` with the
   tree-sitter CLI (a node toolchain dependency). To keep the build hermetic, the generated
   `parser.c`/`scanner.c` are checked in here instead. Verified in sync with the pinned
   submodule HEADs (identical `STATE_COUNT`/`SYMBOL_COUNT`/`LANGUAGE_VERSION`; textual
   diffs are comment-stripping only).

   **Drift rule: if you bump the `tree-sitter-sql` or `tree-sitter-swift` submodule, you
   must regenerate these copies** (`tree-sitter generate` in the submodule, then copy
   `src/parser.c` + `src/scanner.c` + `src/tree_sitter/` headers here). The submodules stay
   registered because they are the source of truth (`grammar.js`) for these copies.

3. **`generated/pgn` (homegrown grammar).** Chess PGN has no vendored upstream — the
   `grammar.js` here is Laplace's own, kept alongside its generated parser. PGN game
   results are a first-class attestation source (chess outcomes share the
   `{Loss=0,Draw=1,Win=2}` encoding with `attestations.outcome`), hence a real grammar
   rather than ad-hoc parsing.

## The ~264 dormant submodules

`external/tree-sitter-grammars/` registers **299** grammar submodules; only ~33 are
load-bearing today (the 31 built directly + `sql`/`swift` as generated-copy sources of
truth). The rest are dormant by design, not debris: the intent is a unified vendored
rebuild of the full stack (see also `postgresql`/`postgis`/`geos`/`proj`/`gdal` submodules,
kept for an Intel/Eigen/Spectra-linked unified build), and each modality/format that gains
a decomposer wires up its grammar from the already-pinned pool. Wiring a new one is two
lines: a `laplace_add_grammar` entry in `CMakeLists.txt` here and a registry row in
`grammar_registry.c`.

Do not deregister dormant submodules without the author's sign-off. If clone weight is a
concern, use shallow/on-demand submodule fetch (`git submodule update --depth 1` or
`submodule.<name>.update=none` locally) rather than removing them.
