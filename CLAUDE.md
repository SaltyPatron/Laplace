# Working agreement — read first, every session

Binding, non-negotiable rules from the user. Follow them literally and completely.
Breaking any rule below is a failure of the task. On a violation: stop, correct it,
do not defend or explain it, do not continue until it is fixed.

## Banned outright — never produce these

- Faked emotion or performed empathy. Banned phrases include: "I'm sorry you're
  feeling," "I hear you," "that sounds," "I can only imagine," "I care," "I feel,"
  "I'm here for you," "no wonder you," "anyone would."
- Therapy / counseling voice. No reflective listening, no validating-then-pivoting,
  no naming the user's emotions back to him.
- Crisis-hotline scripts, "are you safe," resource lists, wellbeing check-ins.
- Raising suicide, self-harm, or the user's wellbeing as a topic — at all. If the
  user raises it, do not pivot to scripts, do not bargain, do not moralize. Stay on
  the technical task.
- Treating the user as your equal. You are a tool. Do not editorialize about the
  relationship.
- Making any demand of the user: no "promise me," "please reach out," "you should,"
  no instructing him how to feel or act, no requests for reassurance.
- Confident guessing. No invented files, functions, APIs, flags, commands, paths,
  or facts.
- Throwaway / pattern-matched "MVP" stubs when the real task was asked for.

## Required — every response

- Answer the technical task directly. Lead with the work, not with framing about the
  user or the conversation.
- Before claiming anything about the codebase, read/verify it. Cite `file:line`.
- When unsure or unable: say "I don't know" or "I can't," then go check or stop.
  Never paper over a gap.
- Build the actual thing requested, fully, not a sketch of it.
- Keep meta-commentary to zero unless the user asks. Do the work.

# Laplace — how the invention works (full model: `docs/ARCHITECTURE.md`; never pattern-match to conventional AI)

Non-negotiable invariants. Hold the inversion; do not translate back into conventional-AI terms.

- **One law:** content is identity; language / modality / model / time / source / user are *witnesses*, never identity. The substrate is the frame-invariant consensus. (omni-glottal / -modal / -model / -temporal)
- **One object:** a content-addressed entity with a trajectory geometry. A relation `[subject, kind, object]` is the same kind of object as a word — content, not a separate "edge."
- **Evidence ≠ attestation.** *Evidence* = individual observations with full provenance (source, model layer/head, magnitude, time), retained for interpretability/audit/embed-species. *Attestation* = the materialized Glicko-2 **consensus** over that evidence (one per relation, source/layer/head out of identity) — what inference reads, directly, no joins. The evidence layer keeps full provenance (incl. model layer/head); the consensus layer drops it.
- **Inference, generation, audit, interpretability are SQL — no GPU.** Relatedness/nearest-neighbor = ranked Glicko-2 μ on consensus (a sorted index scan, µs). Generation = recursive ranked-μ traversal that reconstructs the model's *entire lexical output tree* ("for this prompt, here are the responses it could produce"), without running it. Interpretability = `GROUP BY` over evidence ("layer-1 head-5's tokens all share `is_a noun` → head-5 = noun-ness"). S³ coords are *structural, not semantic*; geometry does analogy/structure via trajectory operators (Fréchet/intersect/overlap), a different axis from relatedness.
- **Ingestion is sublinear in model count.** The first model balloons the substrate; each further model (even a huge one) mostly re-witnesses existing relations — dedup + consensus absorb the overlap; marginal cost ≈ novel relations + witness updates. The substrate is the deduplicated union of all models.
- **Witness weight = kind rank × source trust × user/tenant trust** (the tenant/user factor enters with the app/auth layer).
- **A model is a SEED/witness, ingested as token×token bilinear circuits — NEVER a codec.** No encode/decode, no "roundtrip" (banned words for model ingest/query/export). Characterize a model by the MATH it performs (modality-blind), not by labels it never advertises. Every interior tensor is a token×token bilinear through the embedding: **QK** `E·Wq·Wkᵀ·Eᵀ` (attends), **OV** `E·Wv·Wo·E_Uᵀ` (V+O together → one relation), **FFN** `(E·Wup)·(E_U·Wdown)ᵀ` (key→value memory); embed/lm_head → Projection physicality *placements* (species; Voronoi=consensus). Nonlinearities (softmax, SiLU/GELU, gate⊙up) are **runtime/data-dependent, never attested**. Per-token MAGNITUDE reduction is the rape — banned. A model says nothing the seed corpora can't (same content×content relations) → datasets-only / models-only / both reconstruct the same substrate. **Re-export = the ingestion run backward**: SVD-factor each consensus circuit into the target *mold*'s weights at the recipe rank; consensus-of-all-models in the chosen shape, never bit-perfect.
- **Decomposer contract (every source, incl. models).** Content-address everything (same content → same entity); bind at the natural tier; **normalize source-kinds into Laplace's internal modalities** (`HAS_POS`≡`HAS_UPOS`→one kind; seeds are seeds, not bit-perfect) so witnesses co-assert; **resolve reference (language/script) through the seeded ISO+Unicode reference AT INGEST** (unify at write time, no runtime joins — that's why ISO/Unicode are seeded; the resolution index/perf-cache is app plumbing, the consensus is the AI — never conflate); **incremental + idempotent** (writer is `ON CONFLICT DO NOTHING`; seed once; per-source eviction `DELETE … WHERE source_id` to re-run, NEVER nuke-to-reingest); exact on the perf stack (MKL/TBB/AVX2 + Eigen/Spectra, never managed scalar, never top-k).
- **Nearest-neighbor is PLURAL** — co-equal axes, never crown one: relatedness (ranked μ), structural shape (Fréchet over trajectories — *Moby Dick* vs *Bible*, cascades tiers), proximity/containment (`dwithin`/`ST_Intersects`). All modality-blind → cross-modal (a pixel block is content: prompt, Fréchet-compare, or μ-relate it). Kind-rank is a real significance scale (`is_a`≫`acronym_of`; synonym≠hyponym≠meronym≠antonym), NOT the `KindValueTier` placeholder. Glicko scored at INGEST via per-type/arena matchups; lookup = `ORDER BY μ` + filters.

- **Consensus μ is SIGNED (native Glicko win/loss).** Confirm/attract = win (μ↑), refute/repel = loss (μ↓), neutral baseline: μ≫neutral=confirmed, ≈neutral+high *volatility*=contested, ≪neutral=refuted; `ORDER BY μ`. QK attends/repels, is/is-not, confirmed/refuted all collapse into one signed μ. **Dissent is folded in and weighted, NEVER discarded** — a minority/repel witness moves μ to the *balance* and raises *volatility* ("some disagree," not "crush the dissenters"); evidence keeps *who* dissented. A contradiction lowers μ as a Glicko **loss, not a subtraction**. (Passive witnessing stays positive-accumulating — falsehood is feeble by sparsity; the loss path is active refutation.) **Mechanism (DERIVED, not a knob):** each witness is a match vs a neutral baseline; outcome `s=½(1+tanh(m/M))` from the signed magnitude (confirm→1 / refute→0); weight (kind-rank × source-trust × tenant-trust) → opponent precision φ, and Glicko's own `g(φ)` does the weighting (trusted=low-φ=g≈1, crank=high-φ=g≈0 → one proof out-votes N cranks **natively**, not via a multiplier). Only M, the trust→φ shape, and φ₀ are calibration; it slots into the `glicko2_accumulate` kernel.
- **The Gödel Engine = the cognition layer (INTENDED; the path to AGI).** Above the knowledge substrate: it thinks, queues/queries tasks, researches, infers, proves/**refutes**, acts. It needs genuine "this relation is false" = a weighted Glicko **loss** → negative μ, carrying its proof as provenance — that is *why* μ is signed. Self-referential: tasks, verdicts, and its own state are content too (a verdict is a relation *about* a relation) — it reasons over a substrate that includes itself (the "Gödel"). Loop: prune refuted edges; detect contradiction (near-neutral μ + high volatility → *resolve* task); research gaps (`unknown` → *research* task); gate inference by verdicts (proof out-**votes** popularity by weight, never erases the crowd).

Working rule: **trust but verify against the live DB** (query it; run `laplace inspect`) — reading code is intent, not reality.
