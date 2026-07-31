# Laplace

A content-addressed knowledge substrate on PostgreSQL with a native C/C++ engine.

Facts from any source — a lexicon, a corpus, a chess game, a user's prompt, an AI
checkpoint — are recorded in one shape: an assertion between content-addressed
entities, carrying who said it and how it came out. Assertions fold into a Glicko-2
rating per distinct triple. Reads are indexed graph traversal over those ratings.

No GPU, no gradient descent, deterministic end to end. SQL and C# orchestrate; the math
is native.

## The shape of a fact

```
(subject, relation_type, object, source, context, outcome, score)
```

`outcome ∈ {Refute = 0, Draw = 1, Confirm = 2}` — a source can refute a triple, not just
assert or omit it. `score` is continuous; the source's own trust enters the rating math
as the opponent's RD rather than as a filter around it.

Three layers keep deduplication, provenance and aggregation from fighting each other:

- **Content** — `entities`, keyed by a 16-byte content hash. Same content, same id,
  from every source at every tier. Cross-source merging is a hash collision, not an
  entity-resolution pass.
- **Evidence** — `attestations`, one provenanced row per assertion. Two witnesses of the
  same fact stay two rows.
- **Consensus** — `consensus`, keyed by `blake3(subject‖type‖object)`, holding
  `rating`, `rd`, `volatility`, `witness_count`. Every witness of a triple folds onto
  exactly one row. Reads rank by `eff_mu = rating − 2·rd`.

Geometry (`physicalities`) is a parallel identity and serialization system: S³
coordinates, a Hilbert index, and trajectories for order-sensitive structure. It
addresses and reconstructs content; the relatedness signal is the rating, not distance.

Chess is the proving domain because its ground truth is checkable, and because a ply's
outcome and an epistemic claim's outcome are the same three values fed to the same math.

## Where things are

```
app/          10 projects + 5 test projects (app/Laplace.slnx)
engine/       native core, dynamics (eigenmaps/procrustes), synthesis (GGUF), manifest
extension/    the laplace_substrate PostgreSQL extension — 29 SQL families, 26 native sources
scripts/      build, seed, deploy and CI entry points
web/          Vite/React SPA
docs/         ARCHITECTURE.md, INVENTORY.md (generated)
```

Deployables: `Laplace.Cli`, `Laplace.Endpoints.OpenAICompat`, `Laplace.Endpoints.Mcp`,
`Laplace.Chess.Uci`, `Laplace.Migrations`.

## Build

**Linux** — `sudo bash scripts/setup-host.sh` once, then `scripts/pipeline.sh` (what CI
runs). Change-aware: fingerprints in `build/.stamps/` skip unchanged domains; override
with `--force-all`.

**Windows** — `scripts/win/*.cmd`, driven through Bash rather than PowerShell:

```
cmd //c "scripts\win\rebuild-all.cmd"      # build
cmd //c "scripts\win\test-all.cmd"         # the gate
cmd //c "scripts\win\seed-step.cmd <src>"  # seed one source
cmd //c "scripts\win\cli.cmd"              # CLI
```

After any engine rebuild run `build-extensions` **and** `install-extensions` — the
extension links the engine statically, so engine freshness is not extension freshness.
`pg_regress` tests the installed extension, not an edited `.sql.in`.

## Run

```
psql -h localhost -U postgres -d laplace
SET search_path = laplace, public;
SELECT * FROM api('walk');     -- the schema introspects itself
```

Two mmap'd perfcache blobs are required at runtime (`laplace_t0_perfcache.bin`,
`laplace_highway_perfcache.bin`), located via the `laplace_substrate.perfcache_path` GUC.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the system as built, with file citations
- [docs/INVENTORY.md](docs/INVENTORY.md) — generated counts and listings, CI-gated
- [CLAUDE.md](CLAUDE.md) — working rules for coding agents

## License

See [LICENSE](LICENSE). Seeded sources carry their own licenses; the substrate records
license and attribution attestations per source.
