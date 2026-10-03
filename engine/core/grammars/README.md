# Tree-sitter grammar wiring

Tree-sitter is a grammar-provider and compatibility parsing estate for the substrate.
It is not the universal container-format engine and its CST is not Laplace's canonical
knowledge representation. Canonical structure is tiered entity composition plus exact
physicality trajectories; grammar productions/fields/precedence/conflicts can be admitted
as ordinary knowledge that constrains higher-tier composition.

Use these parsers when their grammar contributes source structure, validation or realization
that the active recipe needs. Do not route a bulk/structured source through Tree-sitter merely
because a grammar exists: standards/streaming readers (for example the UCD XML reader) are
preferred when they recover the same source facts without materializing a redundant tree.
This directory declares the Tree-sitter grammar object libraries available to
`grammar_registry.c`.

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

   **Structural tags (`tags.scm`):** Laplace-owned queries live at
   `engine/core/grammars/<modality>/queries/tags.scm` (e.g. `sql/queries/tags.scm`).
   `GrammarTags.LocateTagsScm` prefers that path before the upstream submodule
   `queries/` tree — SQL upstream ships highlights/indents but no tags, and W3/#765
   needs DEFINES/CALLS captures on the generated SQL parser.

3. **`generated/pgn` (homegrown grammar).** Chess PGN has no vendored upstream — the
   `grammar.js` here is Laplace's own, kept alongside its generated parser. PGN game
   results are a first-class attestation source (chess outcomes share the
   `{Loss=0,Draw=1,Win=2}` encoding with `attestations.outcome`), hence a real grammar
   rather than ad-hoc parsing.

## The rest of the estate is already usable

`external/tree-sitter-grammars/` registers **299** grammar submodules; **35** are wired
into this directory and `grammar_registry.c`. The other checkouts are not debris, and they
are not waiting on a hand-written decomposer.

On this machine the sources are `/vault/Data/TreeSitter` (303 checkouts, 325 `grammar.js`).
`Laplace-Engine/tools/build_grammars.sh` compiles each `src/parser.c` to
`$LAPLACE_GRAMMARS/libtree-sitter-<name>.so`. **316** of those libraries are built.

Tree-sitter parses. It returns a concrete syntax tree: a grammar-rule name and a byte span
per node. That tree names the parts of a modality. UAX #29 still decomposes every span
the tree (or a record rule) has decided is text: an XML text node, a TSV field, a source
leaf, a PDF text run once a reader has extracted it. Whole-file UAX #29 is only the case
where nothing else names the parts, such as a book with no recipe. The record is the
composition the engine already runs for a recipe that names a grammar and no `node`
lines: each node is the composition of its children, the bytes between children stay
text, a leaf is UAX #29, and the file recomposes byte for byte. The caller keeps the
trunk id and fetches the record for its physicality. See `Laplace-Wiki/Corpora/TreeSitter.md`.

XML is the stock example: `grammar xml`, leaves are text, `libtree-sitter-xml.so` is
built. A recipe is Laplace's grammar of a standardized format, whether or not a
tree-sitter library exists for it: `grammar NAME` when one does, `tier` lines when the
standard is records and separators, `grammar text` only when no format recipe exists.
PDF (ISO 32000) and Word (ECMA-376, a package of XML parts) have neither a library in
the vault nor a recipe. They need recipes. Admitting them as basic text is the wrong
parse. The text those recipes recover is still UAX #29.

Use that path for a language or a format whose only structure on this machine is one of
those libraries:

```
grammar ada
```

A curated `node` or `format` file comes later, when a node kind is a key, a skip, or a
reference. Until then the grammar-only recipe is the admission. Do not send the file
through `grammar text` while the node lines are unwritten.

A record standard still beats the grammar. IANA `text/tab-separated-values` and RFC 4180
already say what a row and a field are. `tree-sitter-csv` is the wrong reader for a slot
table: materializing a concrete tree of the rows copies a structure the line already has.
The UCD XML reader is the same kind of exception, above.

Wiring one of the 35 into this C++ registry is a separate act, for callers that link
`grammar_registry.c`. It is two lines when you need it: `laplace_add_grammar` here and a
row in `grammar_registry.c`. The Engine recipe path does not wait on that wiring. It loads
the `.so` by name.

Do not deregister a submodule without the author's sign-off. If clone weight is a
concern, use shallow/on-demand submodule fetch (`git submodule update --depth 1` or
`submodule.<name>.update=none` locally) rather than removing them.
