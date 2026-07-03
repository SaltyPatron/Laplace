# Laplace

Content-addressable geometric-attestation substrate. Full architectural writeup lives in
`.scratchpad/01_Initial_review.txt` and `.scratchpad/02_Identified_Issues.txt` (issue
tracker, kept up to date across sessions) — read those before doing deep work here.
`.scratchpad/05_Substrate_Invariants.txt` (what must be true about the data/model — e.g. what
a centroid/hilbert collision does and does not certify) and `.scratchpad/06_Engineering_Ruleset.txt`
(how code touching the substrate must be written) are the same kind of living doc, split out
because they answer different questions. This file is the fast-start operational reference.

## What this is, in one paragraph

Every fact from every source (WordNet, ConceptNet, a chess game result, a user chat prompt, a
probed neural network's own weights) reduces to the same 5-tuple: an **attestation**
`(subject, relation_type, object, source, outcome)`. Attestations fold into **consensus**
using literal Glicko-2 fields (`rating`, `rd`, `volatility`, `witness_count`) — the same
rating math that scores chess players scores epistemic confidence; `attestations.outcome`
and chess's `PlyOutcome` share the exact `{Loss=0,Draw=1,Win=2}` encoding on purpose.
Retrieval/reasoning is A* graph search over that consensus-weighted graph (`recall()`,
`generate_walk()`), not vector similarity — there is no GPU code anywhere in `engine/` or
`extension/` and that's structural, not an oversight. Content is content-addressed (BLAKE3
hash of canonical form): identical content always produces identical entity ids, so
cross-source merging is a hash collision, not an entity-resolution pass. Geometry
(`physicalities.coord`/`trajectory`, PostGIS `GeometryZM`) is **not** a semantic embedding —
it's two unrelated things wearing the same column type: (1) a lossless, deterministic,
deduplicating identity/serialization system (tier-0 codepoints get fixed positions on the S³
hypersphere seeded by Unicode's own UCA collation order; composed entities store an ordered,
exactly-invertible sequence of their children's coordinates, so `ContentRoundtrip.cs`
reconstructs original bytes from nothing but a document id), and (2) a raw bit-packed payload
channel (`engine/core/src/mantissa.c`) that smuggles hash ids + scores + counts through the
same `PointZM` columns for entirely non-spatial evidence-log data, disambiguated only by a
flag bit. GGUF export (`engine/synthesis`) builds real transformer tensors deterministically
from consensus Gram matrices (no gradient descent) — treat it as a distribution/GPU-interop
format, not the live serving path. The live serving path (`SubstrateClient.cs` →
`laplace.recall()`) queries the substrate directly.

## Build / deploy / seed — READ THIS BEFORE RUNNING ANYTHING

**Two parallel toolchains exist and are not interchangeable:**
- `Justfile` + root `CMakeLists.txt` — Linux-shaped (apt, systemd, oneAPI Linux paths). Not
  what this machine uses day to day.
- `scripts/win/*.cmd` — the real, actively-used Windows workflow. `rebuild-all.cmd` (clean +
  codegen + build engine/extension/app + perfcache), `db-reset.cmd` (drop/recreate `laplace`
  DB + install extensions), `seed-foundation.cmd` (10 core layers: unicode, iso639, cili,
  wordnet, verbnet, propbank, framenet, mapnet, wordframenet, semlink), `seed-step.cmd
  <source> [path]` (single source, see `seed-step.cmd --list`), `seed-everything.cmd` (full
  ladder including large corpora — long-running, hours-scale).

**CRITICAL: never invoke `scripts/win/*.cmd` files through the PowerShell tool.** This
Windows 11 build's PowerShell 7 (pwsh) has a real, confirmed regression
([PowerShell/PowerShell#27634](https://github.com/PowerShell/PowerShell/issues/27634), tied
to KB5095093) in how it launches `.cmd`/`.bat` files — the constructed command line gets
mis-sliced regardless of invocation style (`cmd /c "..."`, direct invocation, unquoted
variables all fail identically), producing a bogus `'ocal' is not recognized as an internal
or external command` error before the script even runs. This is not a bug in this repo — it
cost most of a session to root-cause. **Always use the Bash tool** (git-bash) instead:
```
cmd //c "scripts\\win\\seed-step.cmd wordnet"
```
Minor known side-effect of that workaround: git-bash's `/usr/bin/find.exe` (GNU findutils)
shadows Windows' `find.exe` for the `tasklist | find` mutex-guard inside
`seed-step.cmd:run_ingest_impl` — produces cosmetic `find: '/I': No such file or directory`
lines but does not block ingestion. Also note: that mutex guard checks for a process named
`Laplace.Cli.exe`, but `dotnet run` actually launches as `dotnet.exe` — so the guard does not
reliably prevent two concurrent ingests. (Concurrent ingestion was observed to work for a
while but one run hit `PostgresException 23505: duplicate key value violates unique
constraint "entities_pkey"` followed by an `ObjectDisposedException` on `IntentStage` —
concurrent-ingest safety is not proven; see `.scratchpad/02` for the live incident.)

`env.cmd` needed a real fix this session: it unconditionally prepended 5 PATH segments (and
re-ran Intel oneAPI's `setvars.bat`, which does the same) on every `call`, with no guard
against being sourced more than once per process. `seed-foundation.cmd`'s loop calls it once
per step in a single cmd.exe process, so PATH grew ~600 chars/call and blew past cmd.exe's
~8191-char line limit by step 5-6. Fixed with a `LAPLACE_ENV_LOADED` idempotency guard at the
top of `env.cmd` — if you ever see PATH-length-related corruption again, check that guard is
still there.

DB connection for direct queries: `psql -h localhost -U postgres -d laplace` (password
`postgres`, matches `env.cmd` defaults). Use `SET search_path = laplace, public;` first.
`SELECT * FROM api('<substring>');` is the schema's own self-introspection catalog — lists
matching function names/args/return types. Useful before assuming a helper doesn't exist —
though note a real gap found this session: there's no function to decompose an arbitrary
string into its constituent codepoint/tier rows (`api('codepoint')` / `api('grapheme')` only
turn up reverse-lookup and aggregate-vocabulary functions), so single-character `word_id()`
lookups had to be hand-rolled via `generate_series` + `substring`.

## Core concepts worth knowing before reading code

- **Tiers**: 0 = raw Unicode codepoint (the "Rosetta stone" / "null sector" — nothing
 composes below it), 1 = grapheme (UAX29), 2 = word, 3 = sentence, 4 = document. Each
 modality (image/audio/code) gets its own analogous tiered ladder, not literal UAX29 reuse.
 Tier is a **floor**, not identity: `entities.tier` records the lowest form of the content,
 and the same content keeps the same id at every tier above it ('cat' has no separate
 "sentence" entity — a one-word reply *is* the sentence). same content = same hash; tier is
 never mixed into the id (see Rule #1b in `.scratchpad/05_Substrate_Invariants.txt`).
  Tree-sitter's job is narrowly scoped: strip raw content out of a container format (only
  ~37 of ~300 vendored grammars are actually wired into `grammar_registry.c`), then hand off
  to the same tiered decomposer pipeline everything else uses.
- **`highway_mask`**: 256-bit bitmask (on both `entities` and `attestations`), one bit per
  canonical relation *type* (153 assigned, generated from `engine/manifest/relation_types.toml`
  into `highway_manifest.h`) — coarse (type-level, not value-level; dynamic `DEP_*`/`FEAT_*`
  relations collapse onto their family-root bit). **Confirmed NULL on real production
  attestations** (ISO639Decomposer output) this session — the population path
  (`HighwayPerfcache.MaskForRelationType` in `SubstrateChangeBuilder.cs`) exists in code but
  isn't reliably reaching the DB. `extension/laplace_substrate/sql/39_highway.sql.in` is 0
  bytes — likely related.
- **perfcache**: two build-time-generated, mmap'd, deterministic binary blobs —
  `laplace_t0_perfcache.bin` (every Unicode codepoint's geometry + hash + segmentation
  properties, generated from pinned UCD/DUCET files with a CI determinism gate) and
  `laplace_highway_perfcache.bin` (relation-type bit/rank/band table). Both required at
  runtime; Postgres side gates on `laplace_substrate.perfcache_path` GUC.
- **relation salience ranks**: 13 bands in `relation_types.toml` (`mandate`=1.0 down to
  `probationary`=0.05), drives both live read-time confidence weighting and GGUF export-plane
  weighting from the same number.

## Known gaps (see `.scratchpad/02_Identified_Issues.txt` for the live, itemized list)

`feature_extractor.cpp` is a full stub;
`astar.cpp`'s heuristic is hardcoded `0.0` (uniform-cost search today, not true A*); MoE
expert weights / attention bias fall through to zero in GGUF synthesis; zero documentation
existed anywhere in this repo before this session.

**No native (C/SPI) geometric KNN function exists anywhere.** Every "nearest neighbor" /
"shape peer" SQL function (`laplace_nearest_entity`, `nearest_neighbors_4d`,
`word_shape_peers`) is pure SQL leaning on the `physicalities_coord_gist` index for
`ORDER BY coord <<->> point LIMIT k` — zero custom native traversal. The real 4D/geometric
native code (`hilbert4d.c`, `math4d.c`, `procrustes.cpp`, `astar.cpp`) is never exposed to
SQL. This is the concrete instance of a repeated architectural point from the author: SQL/C#
should orchestrate, native C/C++/SPI should do the heavy lifting — for the substrate's single
most load-bearing query class (recall/converse/word-relations all depend on it), there's
currently nothing to orchestrate.

**Concurrent writes were unsafe until this session** — `apply_batch.c`'s anti-join dedup has
a check-then-act race (two concurrent decomposers can both see the same content-addressed id
as novel before either commits, one hits a raw unique-violation). Fixed with
`pg_advisory_xact_lock(hashtextextended('laplace_apply_batch', 0))` right after `SPI_connect()`
in `apply_batch.c` — serializes concurrent `apply_batch` calls (transaction-scoped, released
automatically). If you see `entities_pkey`/`physicalities_pkey` unique-violations again,
check this lock is still there before assuming something else broke.

**`descent_probe.c`'s content-descent traversal was O(n²)** (rescanned the full candidate
array per visited node instead of building a children-index once) — fixed to O(n) this
session. Not yet verified against a real large-document ingest post-fix. This fix is
*narrower* than it should be: each document in a batch still gets its own separate probe
call/SPI round-trip rather than being batched in bulk across the whole in-flight corpus,
scoped by tier — see `.scratchpad/02` Issue 19 for the distinction. Don't conflate "fixed the
algorithmic complexity of one call" with "implemented O(tier) bulk dedup" — they're different
fixes at different scope, and only the narrower one has been done.

**Project consolidation is DONE (two waves, author-approved)**: the app tree is now 13
projects — 4 libraries (`Laplace.Core` = old Engine.Core/Dynamics/Synthesis + Modality;
`Laplace.Substrate` = SubstrateCRUD + Ingestion + Decomposers.Abstractions +
Containers.Abstractions; `Laplace.Decomposers` = all 19 source decomposers;
`Laplace.Chess` = Modality.Chess + Chess.Service), 4 deployables (`Laplace.Cli`,
`Laplace.Endpoints.OpenAICompat` with Api.Contracts folded in, `Laplace.Chess.Uci`,
`Laplace.Migrations`), and 5 test projects mirroring the libraries. **Namespaces were
preserved byte-identically** (`Laplace.Engine.Core` etc. still exist as namespaces inside
the merged assemblies) — content-addressed SourceId/SourceName strings never changed, so
substrate ids are untouched; verified via isolated re-ingest producing hash-identical ids
and 0 novel rows on forced re-run. Assembly names DID change (`Laplace.Core.dll` etc.);
nothing binds to them. One merge-specific trap to remember: xunit suites that used to be
separate processes now share process-global native state (T0 perfcache) — fixtures must not
`CodepointPerfcache.Unload()` on dispose (see GrammarPerfcacheFixture).
