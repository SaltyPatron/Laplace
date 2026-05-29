# ADR 0037: Layered seed ingestion and model-ingest fidelity

> **Anchor note (2026-05-28):** This ADR predates [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md). Its original title and body used "codec," which implies round-trip blob preservation and is banned by the anchor (truth 10: "Cute names are a tell"; truth 6: "Bit-perfect preservation is worthless"). Model ingestion is a **streaming O(params) ETL of weight tables into Glicko-2 matchup observations** (truths 1–2), not a codec. References to "codec" below have been replaced; the ADR slug is retained for stable linking only.

## Status

**Accepted** — 2026-05-22
**Amended** — 2026-05-23: each "layer" is one **Decomposer plugin** ingesting its domain's FULL data ecosystem (per the new Decomposer-scope ADR — companion to this one). Layer names below renamed accordingly. Single-file-per-layer framing is wrong; layers are domain-scoped, not file-scoped.

## Context

Laplace is not seeded by an ontology plus one model. The seed plan is layered: Unicode and language standards establish atoms and language identity; lexical resources establish senses/POS; multilingual resources cross-link languages; treebanks and dictionaries add usage/grammar; sentence/audio corpora add parallel utterances and speech; commonsense resources add event/causal structure; tree-sitter/code corpora add code modality; AI models arrive as later evidence sources whose already-computed weight relationships are streamed (O(params) ETL) into Glicko-2 matchup attestations, with physicalities serving as the S³ embedding/access frame the source is morphed into — not the knowledge layer.

This changes what "fidelity" means. For a source-scoped model round-trip, fidelity means the model-ingest ETL records enough of the model's already-computed relationships — each weight cell as a Glicko-2 matchup outcome (per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truths 1–2) — that synthesis can pour those consensus facts back into the source recipe's mold and land in its behavioral basin. It does **not** mean preserving the weight blob; the blob is discarded (truth 6). For broader substrate synthesis, fidelity means the substrate preserves and materializes cross-source consensus structure.

## Decision

Canonical layer order (each layer = one Decomposer ingesting its full domain ecosystem):

| # | Decomposer | Ecosystem (full set, not single file) | Local data root |
|---|---|---|---|
| 1 | **UnicodeDecomposer** | UCDXML + UCA DUCET + Unihan + emoji + auxiliary segmentation (UAX-#29) + CLDR-unicode | `/vault/Data/Unicode/` (≈37 GB) |
| 2 | **ISODecomposer** | ISO 639-3 (SIL) + ISO 15924 + ISO 10646 + BCP-47 (IANA) + CLDR validity + LoC + Glottolog | `/vault/Data/ISO639/` + `/vault/Data/Unicode/iso15924/` |
| 3 | **WordNetDecomposer** | Full WordNet 3.0: data + index + glosses + senses + examples + lexicographer files + exception lists + ILI mappings | `/vault/Data/Wordnet/WordNet-3.0/` (≈49 MB) |
| 4 | **OMWDecomposer** | 100+ language WordNet packs + cross-lingual synset bridges + per-language licensing | `/vault/Data/omw/wns/` (≈245 MB) |
| 5 | **UDDecomposer** | Universal Dependencies v2.17 — 250+ treebanks across ~140 languages, CoNLL-U per sentence | `/vault/Data/UD-Treebanks/ud-treebanks-v2.17/` (≈4.3 GB) |
| 6 | **WiktionaryDecomposer** | Per-language Wiktionary XML dumps (definitions, etymology, IPA, audio refs, inflections, translations, examples) | `/vault/Data/Wiktionary/` (≈34 GB; currently `en/` only) |
| 7 | **TatoebaDecomposer** | sentence dump + per-pair links + per-sentence metadata + `audio/` recordings + speaker/voice metadata + licensing | `/vault/Data/Tatoeba/` (≈5.4 GB) |
| 8a | **Atomic2020Decomposer** | ~1.3M commonsense triples across ~25 relation types | `/vault/Data/Atomic2020/` (≈66 MB) |
| 8b | **ConceptNetDecomposer** | ConceptNet 5.7+ multilingual; ~30 relation types; sub-sources (Wikipedia / OMCS / WordNet / JMDict / Verbosity / GlobalMind / ...) tracked per assertion | `/vault/Data/ConceptNet/` (≈9.5 GB) |
| 9 | **TreeSitterDecomposer** | 303 tree-sitter grammar repos (grammar.js + parser.c + queries/) + code corpora when ingested | `/vault/Data/TreeSitter/` (≈1.9 GB) |
| 10 | **TransformerModelDecomposer** | per-model safetensors + config.json + tokenizer.json + auxiliary architecture files | `/vault/models/<model>/` (TinyLlama-1.1B + Phi-2 + Qwen variants present) |
| 10+ | Other modality decomposers (Image / Audio / Video corpora) | per-modality ecosystems | TBD |

The dependency arrows propagate: Layer N's decomposer references entities Layer M<N's decomposer produced (via shared content-addressed IDs in the same hash space). Examples: ISODecomposer's `Script` rows are the same rows UnicodeDecomposer emitted; WordNetDecomposer's `Text` lemmas reuse Unicode-codepoint-decomposed text entities; OMWDecomposer's per-language lemmas reference ISO's `Language` entities; UDDecomposer's treebank metadata references ISO + Unicode; etc.

AI model ingestion is a **streaming O(params) ETL of weight tables**, not a conventional distillation/training step and not a round-trip codec. Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truths 1–2: each weight cell is one already-computed relationship, emitted in parallel (oneTBB) as a Glicko-2 matchup outcome (weight = outcome; source-model trust = opponent strength); only the emergent consensus rating is stored, never the weight, never bit-perfect. It must scale linearly to frontier models. **Forbidden:** GEMM at ingest (`E·W·Wᵀ·Eᵀ` over vocab²), materializing a vocab² matchup space, or a flat top-k that discards most of the model — the prior attempt took an hour on a 2 GB model and returned 646/32000 tokens; that is the disease, not a tuning knob. Sparsity is a property of which cells are significant, not a top-k cap.

The ETL also records recipe metadata, tokenizer/modality content, physicalities (the S³ access/embedding frame, not knowledge), and architecture-specific attestation arenas. If `ModelDecomposer` records the source model's load-bearing relationships and synthesis pours those consensus facts back into the source recipe's mold, the native Synthesis package should land in the source model's behavioral basin. Differences should come from intentional sparsity, sampler settings, or broader substrate consensus scope — not accidental missingness. GGUF is a proof/compatibility artifact for chat validation, not the native export target.

> **OPEN per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md):** how interior `d×d` tensor cells (`q/k/v/o/gate/up/down`) resolve to token entities *without* re-running the GEMM (the thing that blows up), and the exact arena/kind assignment per interior tensor role, are unsolved. `embed_tokens`/`lm_head` are directly token-anchored and cheap; the interior resolution and the frontier-scale "pour facts into the mold" synthesis algorithm must be pinned with Anthony. This ADR does not settle them.

The v0.1 proof can be narrow and still decisive: Unicode-derived T0 + one Qwen-family source model + recipe extraction + sparse attestations + native safetensors-style package emission + GGUF proof conversion + chat verification.

## Consequences

- Seed resources supply explicit fidelity channels that models normally carry implicitly: character/script fidelity, lexical fidelity, cross-lingual fidelity, syntactic fidelity, usage fidelity, audio fidelity, code fidelity, commonsense fidelity, and model-behavior fidelity.
- Later model-derived claims are measured inside an already constrained substrate instead of seeding meaning into an empty database.
- Source-scoped round-trip tests should compare stock source model, native substrate traversal, and synthesized export under fixed prompt/sampler settings.
- Broader synthesis can intentionally improve or alter behavior by changing source scope, trust policy, recipe, feature extractors, and sparsity.

## Alternatives considered

- **Model-first seeding.** Rejected — leaves language/script/sense structure implicit inside model behavior.
- **Ontology-only seeding.** Rejected — lacks sequence pressure, usage, audio, code structure, and model behavioral evidence.
- **Treating model weights as authoritative artifacts.** Rejected — models are sources; substrate state is the artifact.

## References

- [RULES.md R3](../../RULES.md) — lottery-ticket-aware sparsity
- [RULES.md R4](../../RULES.md) — sparse-by-construction emission
- [RULES.md R21](../../RULES.md) — layered seed ingestion and model dissolve/synthesize fidelity
- [DESIGN.md](../../DESIGN.md) — seed source order and model dissolve/synthesize fidelity
- [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) — ratified conceptual core (model ingest = streaming O(params) ETL; bit-perfect worthless; "codec" banned)
- [OPERATIONS.md](../../OPERATIONS.md) — round-trip and comparison workflow
