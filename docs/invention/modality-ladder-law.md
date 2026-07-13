# Modality ladder law — Part 1 + modalities campaign (M0)

Binding template from the forward plan. Geometry = identity/reconstruction only.
Semantics live in the Glicko attestation graph. Do **not** hash embeddings as identity.

**Status:** M0 specs + TOML relations declared. **No ingest until KEYMASTER reseed.**

## The one law (every modality)

1. **Tier-0 = quantized scalar alphabet with a canonical total order.** Mints through the
   scalar content law; S³-anchored by that order; dense-perfcached (own determinism gate).
2. **Deterministic segmentation** (UAX29-analog): atoms → tiers, order-preserving
   trajectory → tier>0 is an invertible ordered constituent sequence.
3. **Witnessed / calculated split.** Raw atoms witnessed (verbatim, content-hashed).
   Interpretation (Fourier, patches, regions, objects) is calculated, versioned, evictable;
   emit attestations, discard the transient transform.

## Color order (image rock lock)

Packed `uint32` RGB total order: `(R<<16)|(G<<8)|B`. Deterministic, rock-stable.
Operator may override **before** first image seed only.

## Ladders shipping this campaign

### Text (reference — already live)

- Tier-0: codepoint / UCA order
- Tiers: grapheme → word → sentence → document
- Witnessed ≈ semantic for lexical sources

### Code (witnessed AST)

- Tier-0: source bytes / tokens as content (existing code lane)
- Segmentation: tree-sitter unpack → ordered AST child sequence
- Relations: `HAS_AST_CHILD`, `HAS_AST_KIND` (plus existing `CONTAINS`/`CALLS`/`DEFINES`/`REFERENCES`)
- Roundtrip: trunk reconstructs witnessed structure; no invented semantics from raw atoms

### Audio via Tatoeba (PCM + witnessed pairing)

- Tier-0: 16-bit PCM sample alphabet (65,536 atoms), amplitude order
- Channel = partition, not a tier
- Tiers: sample → window/frame → onset segment → phrase → track
- Witnessed edge: sentence content root `HAS_RECORDING` / `RECORDING_OF` recording entity
- Calculated (later analyzer): `HAS_SPECTRAL_PEAK`, `HAS_ONSET_SEGMENT` — attest, discard STFT
- Data root: `D:\Data\Ingest\Tatoeba\audio` (paths only until post-reseed ingest)

### Image (witnessed infra only)

- Tier-0: color atom = packed RGB uint32 order (locked above)
- Position = trajectory (Hilbert 2D→1D already positional-encoding primitive)
- Tiers: channel-value → pixel → patch → region → image
- Relations: `HAS_REGION`, `HAS_PATCH`; reuse `ADJACENT_TO_PIXEL`, `IS_PIXEL_OF`, `DEPICTS`, `CAPTIONS`
- No object/scene reasoning claims this campaign

### Video (witnessed infra only)

- Spatial: image ladder per frame
- Temporal: `PRECEDES_IN_TIME`; membership `HAS_FRAME` / `IS_FRAME_OF`
- Shot-boundary ≈ sentence-analog; unchanged regions dedup by content id

## Reseed coupling

All new names + HAS_SENSE family_root edits batch into
[`.scratchpad/24_Campaign_Reseed_Queue.md`](../.scratchpad/24_Campaign_Reseed_Queue.md).
Highway bit renumber ⇒ one KEYMASTER reseed before modality ingest and before R3 mask gate claims.
