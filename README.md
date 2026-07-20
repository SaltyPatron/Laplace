# Laplace

A content-addressable geometric-attestation substrate; a construction (not training)
path from it to runnable transformer models; and a graph-walk inference engine that
closes a self-improvement loop. Omni-modal — text, chess, code, AI models each ride
their own tier ladder under one identity law — and omni-glottal: every language meshes
at the ILI concept hub.

Laplace replaces the two primitives modern ML is built on — GEMM similarity and
nearest-neighbor search in a trained embedding — with graph search over a
Glicko-weighted evidence graph and a deterministic, lossless, content-addressed
identity system. SQL and C# orchestrate; native C/C++/SPI does the math. No GPU, no
gradient descent, deterministic end to end.

## The three keys

Every fact from every source — WordNet, ConceptNet, a chess game, a user prompt, an AI
model's weights — reduces to one **attestation** 5-tuple:
`(subject, relation_type, object, source, outcome/score)`. Three layers resolve the
tension between deduping, isolating, and aggregating:

- **CONTENT** — entities keyed by BLAKE3 content hash. Identical content = identical
  id, at every tier, from every source. Cross-source merging is a hash collision, never
  an entity-resolution pass: "Pawn E2→E4" is ONE entity no matter who played it.
- **EVIDENCE** — one provenanced row per assertion. Provenance is never mashed:
  Magnus's E2E4 and yours are distinct witnessed rows.
- **CONSENSUS** — evidence folds into literal Glicko-2 per `(subject, type, object)`
  cell: rating, RD, volatility, witness count. `eff_mu = rating − 2·rd` is the
  conservative estimate everything ranks by. Source trust enters as the opponent RD —
  trust is inside the rating math, not a filter. The fold plus read-side RD IS the
  noise model: no operator-invented floors, caps, or top-k anywhere.

Geometry (S³ anchors, mantissa-packed trajectories, Hilbert indexing) is a lossless,
deterministic identity and serialization system — `ContentRoundtrip` rebuilds a
document's original bytes from its id alone. Geometry is identity and reconstruction,
NOT semantics. The semantics live in the rated attestation graph: a colony of spider
webs — pull one strand and Glicko-2 tells you what tugs back and how hard. That web is
a weighted graph Laplacian; tension = certainty; witnessing tightens the strand. Hence
the name.

Chess is the proving domain because its ground truth is objectively checkable:
`outcome ∈ {Loss, Draw, Win}` is bit-identical between chess plies and every epistemic
claim — the same math that rates chess players rates every fact.

## The two loops

**Foundry (Mold-A-Model).** Consensus + geometry are molded into a runnable
transformer, deterministically: consensus adjacency → weights/topology; relation types
and salience bands → attention heads; the normalized-Laplacian eigenmap of the
consensus graph → the constructed embedding (hidden dim = spectral rank); trajectories
→ sequence position; the continuation operator → lm_head. GGUF written closed-form.
Every exported weight decomposes back to its witnesses — deterministic provenance for
AI. Scoped synthesis (filter attestations by source/context, re-fold, synthesize) is
the custom-model product mechanism: training replaced by compilation.

**Gödel/OODA.** The walk IS the forward pass — indexed graph search carrying more per
step than a trained dot product: the full Glicko tuple, relation salience, highway
bits, geometry, source trust, provenance down to witnesses. Explainability is a
returned column, not a metaphor. A prompt is ingested as content, so attention over it
is unbounded retrieval — no context window. And the loop closes: prompts and responses
deposit as witnesses, feedback confirms or refutes the exact triples that produced an
answer, and the next walk reads the updated consensus. Evaluation IS ingestion.
Self-signals are structurally outranked so the engine cannot outshout its curated
sources. AI checkpoints are just another witness: a tensor row asserts "A attenuates
to B with this intensity," and that assertion is rated alongside WordNet's — one voice
among many, with mechanistic interpretability falling out as indexed queries.

## Epistemic status — what is proven and what is open

This section is deliberately honest; the project's own law is "verify against live
data," and that applies to its README.

**Proven, evidence on record:**

- Content-addressed identity with lossless byte-exact reconstruction from ids alone.
- The three-layer substrate, exercised end to end on the current partial seed
  (measured 2026-07-20: 6.28M attestations, 5.66M consensus cells, 4.34M entities —
  residency is a derived artifact of which sources are seeded, not a progress mark;
  the design target is order 10⁸ and above), with forced-rerun idempotency proofs
  (re-ingest → 0 novel rows, exact observation-count doubling).
- The chess lane end to end: ~10⁶ games, recorder/analyzer split, a UCI engine whose
  play is a read of the consensus, live Lichess integration.
- The model lane's recorder and factor storage: bit-exact readback of deposited factor
  matrices; factors ~477× smaller than materialized pair tiles; the decoder ring names
  circuits with 17–32× enrichment over random control.
- Retrieval serving in the 100–400 ms range with witness receipts.

**Open — the load-bearing research question (docs/specs/09, /14):** does
consensus × geometry × trajectory ROUTE as well as trained attention at depth? The
best current measurement is layer-replay composition decay of 100% → 57.5% → 26.8%
(layers 0/1/5) for static factors — the designed answer (frontier-as-residual replay,
`.scratchpad/26` item D) is specified but not yet built. Everything foundry-shaped
rides on this question, and it is falsifiable either way.

**Weakest live surface:** free-text generation. Retrieval answers in milliseconds;
the long-form generation lane still times out at scale and is served honestly as 503
rather than hung. The conversational engine (converse/chat) works with a native
steered walk; fluency beyond recombined attested material is open work.

## Layout

Counts and full listings are generated, CI-gated, and always current in
[docs/INVENTORY.md](docs/INVENTORY.md) — prose here deliberately avoids embedding
them.

| Path | What |
|---|---|
| `app/` | C# solution: substrate/pipeline libraries, decomposers, chess, CLI, OpenAI-compatible API, MCP server, migrations, tests |
| `engine/` | Native core: identity/geometry/Glicko-2 math, manifest codegen, foundry synthesis (GGUF), dynamics (eigenmaps) |
| `extension/` | PostgreSQL extensions: the SQL surface + native hot paths (recall, walks, fold, perfcache) |
| `web/` | SPA: chat, chess lab, explore, billing |
| `db/`, `deploy/`, `scripts/` | Migrations, deploy assets, the build/seed/CI entry points |
| `docs/`, `.scratchpad/` | Specs + generated inventory + invention catalog; historical session logs ([docs/INDEX.md](docs/INDEX.md) maps them) |

## Getting started

- Operating law, build/seed/deploy tables (Windows + Linux): [CLAUDE.md](CLAUDE.md)
- Doc map: [docs/INDEX.md](docs/INDEX.md) · Invention catalog:
  [docs/INVENTIONS.md](docs/INVENTIONS.md) · Binding specs: `docs/specs/`
- The schema introspects itself: `SELECT * FROM api('<substring>');`

## License

See [LICENSE](LICENSE). Seeded sources carry their own licenses; the substrate records
license/attribution attestations per source as compliance data.
