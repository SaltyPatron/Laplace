# Perfcache blob law

PostgreSQL is the system of record. A perfcache blob is a deterministic, derived,
one-way runtime acceleration structure. It is never independent truth, never the only
copy of testimony, and never a manual data store.

Every blob format defines:

- magic, version, byte order, record layout, and bounds;
- source database generation/fingerprint;
- relation/manifest/recipe compatibility inputs;
- complete-file checksum and, where needed, section checksums;
- deterministic ordering and duplicate handling;
- writer, loader, validation, and stale-file behavior;
- the canonical PostgreSQL operation that rebuilds or verifies it.

Loaders reject missing, truncated, corrupt, incompatible, or stale blobs explicitly.
They do not partially load and continue. Publication uses write-to-temporary, flush,
checksum, and atomic replace. Readers map immutable files and never mutate their source.

Cache contents preserve typed meaning. A point cache stores point facts; a trajectory
cache stores ordered manifests; a model-factor cache stores versioned factor records.
Packing values into one binary format does not erase their semantic classes.

Perfcache usage must preserve parity with the canonical database/native reference path.
Parity includes ids, ordering, scores, unknown behavior, and source scope. Performance
tests do not replace semantic parity tests.

A green CI job, a file on disk, or a PostgreSQL GUC pointing at a path is not proof
that the serving process can load the blob. Process-local native loaders, PostgreSQL
`MODULE` bindings, and prefix libraries must identify the same build.

## Compositional cache lattice

A perfcache is not scoped by a top-level modality name. It accelerates a deterministic reusable canonical substructure or deterministic calculation.

Higher structures may depend on lower caches:

~~~text
T0/codepoints
-> scalar/number roots
-> channel/sample roots
-> pixel / audio-window roots
-> patch / segment roots
-> region / track roots
-> image / frame roots
-> video timing/synchronization composition
~~~

Chess similarly layers piece/square vocabularies, positions, transitions and lines. Software/model domains may publish their own finite or admitted deterministic structural ROMs.

When a legal state space is finite and serviceable, a cache may cover the entire domain and use direct addressing. When the possible universe is too large, a cache may cover the finite admitted/hot canonical estate with a declared deterministic lookup structure. Higher-tier caches are lawful; Tier-0 is not the only thing worth mmapping.

Cross-modality consumers reuse lower caches. Video does not need a private copy of image pixels/patches/images or audio samples/windows/tracks. Its video-specific work composes those already-canonical roots with timing and synchronization state.

Every higher cache binds the exact generations/recipes it depends on. A dependency change invalidates affected descendants, not unrelated caches.

### Trunk-first lookup

Higher-tier cache lookup should short-circuit lower recomputation.

For a canonical image recipe, probe complete image/frame first; on miss probe regions, then patches, then pixels. A hit owns the already-verified descendant composition for that cache generation. Only missed branches descend.

A deterministic materialization fingerprint may be used as an acceleration lookup key before the canonical root is known, provided the record binds the exact shape/recipe/dependency generation and collision/verification semantics cannot return an unrelated canonical object. The lookup fingerprint is not canonical identity.

This makes repeated video frames image-cache hits rather than repeated pixel-tree construction, and lets partially novel images reuse known patch/region subtrees.

### Index-friendly lookup law

Cache lookup should normally transform request/input state into canonical keys **before** indexed SQL/SPI access:

~~~text
request value
-> mmap/native lookup
-> id / coord / Hilbert / range / typed key
-> prepared indexed database probe
~~~

Do not wrap indexed database columns in a cache/function call per row when the request-side transform can be performed once. A cache-backed function must not claim PostgreSQL IMMUTABLE semantics unless its mapped generation truly makes that promise valid for the lifetime of the index.

See `docs/guides/compositional-perfcache.md`.

## Segmented / profile-scoped ROMs

A cache generation may materialize only a declared subset of a canonical tier while preserving the same identities/coordinates/recipes as the full canonical world.

Lawful selectors include:

~~~text
range
  U+0000..U+007F                 # ASCII subset of the Unicode generation

explicit set / palette
  #000000 #FFFFFF #FF0000 ...   # selected exact colors/pixels

generated finite set
  every legal value under a declared compact format/bit depth

band / interval
  selected frequency bins or filter-bank bands under one analyzer recipe

predicate over deterministic typed fields
  selected entity/type/recipe classes

hot/admitted set
  structures already present or measured hot

dependency closure
  all lower records required by the selected higher structures
~~~

The selector is part of the blob generation/receipt. A subset cache never renumbers or re-identifies its members. For example, ASCII U+0041 retains the same canonical T0 identity and placement it has in the full Unicode generation; a local dense slot may accelerate lookup but is not a new Tier-0 rank.

### Modular composition

Several cache modules may be mapped together as one runtime profile:

~~~text
profile "terminal/code":
  ASCII/basic-text segment
  common scalar segment
  Bash grammar/AST segment

profile "speech":
  selected textual/phonetic segment
  scalar/sample segment
  declared speech-frequency/filter-bank segment
  hot audio-window/segment records

profile "restricted-palette video":
  selected color/pixel palette
  patch/region/image closure over those colors
  selected audio profile
  video timing/synchronization structures
~~~

Overlapping modules converge by canonical key/record identity; they do not duplicate semantic content.

A higher module may declare dependency closure over lower modules. A palette-specific patch ROM can therefore depend on the exact selected pixel palette generation rather than requiring the complete image universe.

### Cache scope is not knowledge authority

A cache profile answers **what is resident/accelerated here**, not **what the principal is allowed to know**.

If an authorized value is absent from a mapped cache, the normal canonical path may calculate/read it and optionally publish it into a later cache generation. If the deployment intentionally lacks a fallback (for example, a constrained offline device), the result is a declared local capability/cache miss or remote-fetch requirement, not a different canonical identity and not evidence that the knowledge does not exist.

Knowledge-package grants remain governed by the authority layer. A package may recommend or ship a matching cache profile for deployment economics, but the two objects stay distinct.

## Roster

This table is the current blob catalog. It is law for *what exists as a
perfcache class*, not a claim that every row is loaded on the live host.
Observed install state belongs in CI receipts and `scripts/check-deployed-revision.sh`.

Tier-0 media scalar leaves use the shared Unicode codepoint floor; image/audio/video do not mint arbitrary amplitude/color/sample values as private atoms. Numeric values compose above T0 (digit/punctuation → canonical scalar/number → channel/sample occurrence → higher structure).

The 0..255 numeric ROM is an acceleration of canonical composition, not the numeric domain. A value such as 0.34567 is represented by the exact ordered codepoint composition ['0','.','3','4','5','6','7'] through the ordinary content path when its declared source recipe uses that exact scalar surface. Repeated occurrences reuse the same scalar root. A long finite pi prefix is the same structural law at larger width and does not require a ROM entry. Glicko standing is not a perfcache. Attestation ids are not
codepoint ids.

| Blob | Role | Lookup | Rebuild | Notes |
|---|---|---|---|---|
| `laplace_t0_perfcache_<ucd>.bin` | Unicode 0..0x10FFFF: id, UCA order, PointZM, Hilbert, UAX flags, NFC compose/decomp | `records[cp]` | UCD emit (`codepoint_table` / Unicode decomposer) | Format v4 (`LPRF`). Legacy v3/banded geometry is rejected. |
| `laplace_highway_perfcache.bin` | Relation-law bit plane: canonical name, band, bit, rank | bit test / 256-bit mask | relation-manifest codegen | Does not populate live consensus band counts. |
| `laplace_modality_number_perfcache.bin` | Canonical integer roots 0..255 | `records[value]` direct index | `modality_number_tables_emit` from T0 | Shared by image/audio/video and any other exact integer consumer. This is the first higher-tier shared scalar ROM, not a modality-private cache. |
| `laplace_chess_position_perfcache.bin` | Piece×square vocab and catalog boards | id → coord/hilbert/tier | recorded-floor export | GUC `laplace_substrate.chess_position_perfcache_path`. |
| `laplace_chess_transition_perfcache.bin` | Deterministic `(from, move) → to` | mmap search | recorded-floor export | Reused by replay/line consumers; not a Glicko dump. |
| Pixel / patch / region / image ROMs | Deterministic image compositions at successively higher reusable tiers | direct index where dense; deterministic sparse lookup where admitted/hot | image recipe + lower cache generations | Prescribed by compositional cache law; image records are reusable by video/document/multimodal consumers. |
| Audio sample/window/segment/track ROMs | Deterministic audio scalar/composition tiers | direct index where dense; deterministic sparse lookup where admitted/hot | audio recipe + lower cache generations | Prescribed by compositional cache law; reusable by video and other multimodal consumers. |
| Factor ROM | Versioned model-factor trajectories | pointer arithmetic | deposited factor physicalities | Designed (#526). Not installed. |
| Generation-corpus ROM | Cold `walk_text` / generation lane | mmap | generation corpus | Prescribed (#409). Not installed. |
| Separator-id ROM | Alphabet-bounded separator atoms/clusters | compiled set | T0 + grapheme law | Named in `docs/sql-cascade.md`; still a scan. |

New blobs land only with: a row here, a one-way rebuild path (blob never seeds
Postgres), determinism/staleness gates, a loader that refuses unknown versions,
and a serving-process probe that the deployed revision check actually runs.

The 2026-07-18 table in `docs/archive/specs-v1/33_Perfcache_Blob_Law.md` is
historical. Do not treat its “live/landing” column as current host state.
