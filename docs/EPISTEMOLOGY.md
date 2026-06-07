# Epistemology

How the substrate knows things. This is the core invention's theory of truth, stated as the laws the code enforces.

## First principles

1. **Knowledge is testimony, not weights.** Every fact in the system exists because some witness asserted it. There is no anonymous knowledge.
2. **Identity is content.** What a thing IS is computed from its bytes; who mentioned it cannot change what it is. This is what makes testimony from different witnesses *about the same thing* comparable at all.
3. **Truth is adjudicated, not declared.** No witness's word is final; claims hold tournament standings (Glicko-2 state), updated by every observation, forever revisable.
4. **Provenance is inalienable.** Evidence rows are provenance-only and permanent; the system can always answer "who says so, since when, how often."
5. **Dissent is preserved.** Refutation prunes traversal but remains visible to ranked reads. Losing an argument does not erase you from the record.
6. **Ignorance is first-class.** Absence is provable (closed world over attestations); uncertainty is a number (RD); gaps are witnessable objects.

## The witness model

A witness is anything that asserts relations: a standards body's data file, a curated lexicon, a treebank, a corpus, a single document, a user's prompt, a deposed neural network. All enter through identical machinery; they differ only in **trust class**, which determines the force (φ) of their testimony in adjudication.

Trust ladder (entities in the seed, descending typical force):
`SubstrateMandate` → `StandardsDerived` (Unicode, ISO) → `AcademicCurated` (WordNet, UD, VerbNet, PropBank, FrameNet, SemLink) → `AcademicCuratedWithUserInput` (OMW, ConceptNet) → `StructuredCorpus` (Tatoeba, Wiktionary, OpenSubtitles) → `UserCuratedResource` → `UserPromptContent` (conversations, documents fed by the operator) → `AppDerived` → `AIModelProbe` (deposed models) → `AdversarialUntrusted`.

The deliberate inversion vs. the industry: **a transformer's testimony is admissible and outranked by the dictionary.** Models are witnesses to be cross-examined, not oracles.

Context (`context_id`) refines the witness without entering relation identity: for models, the layer/head circuit entity (per-circuit testimony enables interpretability queries); for documents, the containing document (stamping in the text path is open work — OPEN-PROBLEMS).

## The evidence law

`attestations` records THAT a witnessing happened: (subject, kind, object, source, context), an outcome **class** (refute/draw/confirm), a count, a timestamp. Never a magnitude: a stored per-witness score is mathematically invertible to the raw weight — value-channel smuggling wearing provenance as a costume — and is banned. The witness's magnitude is consumed at ingest into adjudication and not persisted.

Consequences:
- Re-observation is idempotent (content-addressed 5-tuple id → UPSERT bumps count/timestamp).
- Evidence cannot be replayed into consensus (by design there is nothing to replay) — which is why unlearning consensus state requires one of the documented resolution paths (OPEN-PROBLEMS §3; replay-from-sources is preferred now that full-ladder re-ingestion is minutes-scale).

## Adjudication: Glicko-2 as the truth engine

Why a *rating* system: truth-from-many-witnesses is structurally a tournament — repeated paired comparisons under uncertainty, with confidence that tightens under consistent results and loosens under volatility. Glicko-2 provides exactly: strength (rating), uncertainty (RD), and surprise-tracking (volatility), with principled updates.

Mechanics (engine kernel `glicko2.c`, int64 ×1e9, single source of math truth):
- Every relation starts at the neutral prior: rating 1500e9, RD 350e9, vol 0.06e9; τ = 0.5e9.
- A witness observation is a **game vs. the neutral 1500 line** with score `s = ½(1 + tanh(m/M))` where m is the witness's magnitude and M the arena normalizer (e.g., pooled tensor RMS for model cells); witness trust sets opponent φ via WitnessPhi.
- Within a period, observations are commutative (pinned by determinism vectors); periods fold via the batch kernel `accumulate_games(n, Σs)` — bit-identical to one-by-one replay.
- The accumulation invariant: one φ per relation per period (mixed-φ trips a hard exception, not a silent average).

Reading the state:
- **Belief** = `eff_mu = rating − 2·RD` — the conservative bound. THE one definition, planner-inlined, mirrored exactly by the ranking indexes. All ranked reads order by it.
- **Uncertainty** = RD. `ORDER BY rd DESC` is the introspection query transformers cannot express.
- **Refuted** = `rating + 2·RD < 1500e9`: even read optimistically, the witnesses net-deny it. Pruned from cascade/realization; visible (bottom-ranked) in reads. Continuing a walk across a refuted edge would assert what the consensus denies.
- **witness_count** = how many distinct witnessings accumulated — corroboration breadth, distinct from strength.

Consensus identity excludes source and context: ONE row per (subject, kind, object). Witnesses affect the state, never the identity — this is what makes "what does *everyone* think" a single indexed lookup.

## Geometry's epistemic role

Strictly instrumental (ruling 2026-06-07): truth lives in relations; geometry is the comparative/forensic lens. Per-witness placements (fireflies) are SPECIMENS — Llama's king vs Qwen's king — supporting audit (belief distance, lineage forensics, drift, bias geodesics, Voronoi territories) without ever voting on truth. The dual engine's orthogonality is itself epistemically loaded: structural similarity without relational testimony = a hypothesis candidate (frayed edge), and relational strength without structural similarity (whale~ship) is the signature of *learned* rather than *formal* association.

## Capabilities unique in principle (each demonstrated 2026-06-07)

| capability | mechanism | receipt |
|---|---|---|
| Prove a negative | closed-world count over attestations | 0 rows, 7.2 ms |
| Runtime learning, attributed + timestamped | one db-roundtrip; idempotent fold | the flip, 02:35:51.07 |
| Name teachers per claim | source/context on every evidence row | 50.3M rows |
| Per-claim confidence with visible dissent | μ ± RD; refuted retained | whale arenas |
| Exact recall of training data | Merkle reconstruction | 16/16 BIT-PERFECT |
| Witness removal | per-source eviction (+§3 paths for folded state) | CASCADE design |
| Cross-toolchain identical answers | determinism law | 8/8 byte-identical regress |

## Self-application

The epistemology applies to the project itself: claims about the system earn standing through witnesses (reproduction runs), receipts are deposition kits (scripts/, RECEIPTS.md), and this documentation records rulings with dates so future revision is itself an audit trail. Open problems are filed as witnessed gaps, not hidden.
