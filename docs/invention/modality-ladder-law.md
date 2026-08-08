# Modality ladder law — codepoint floor (binding)

This binding law supersedes every prior private tier-0 alphabet in this file and in the
modality campaign code that mirrored it.
(`modality_atoms*` packed-RGBA / PCM16 blake3 mints, image/audio “scalar content
law” as distinct T0 alphabets). Geometry = identity/reconstruction only.
Semantics live in the Glicko attestation graph. Do **not** hash embeddings as
identity.

This document is law for identity. Implementation rip work is tracked in
[`modality-codepoint-floor-checklist.md`](modality-codepoint-floor-checklist.md).
Do not reintroduce private alphabets under new names.

---

## The one law (every modality)

1. **Tier-0 is always Unicode codepoints** for every modality. Same floor as text:
   existing UCA T0 perfcache (`laplace_t0_perfcache`, `codepoint_table_*`). There
   is **no** image-only or audio-only tier-0 alphabet. Packed RGBA uint32 blake3,
   PCM int16 blake3, and any other private atom mint at tier-0 are **wrong** and
   must be ripped.
2. **Modalities are UAX#29-analogs:** deterministic segmentation / composition
   **above** that shared floor. Order-preserving trajectory → tier>0 is an
   invertible ordered constituent sequence (same structural shape as
   grapheme → word → sentence → document).
3. **Shared codepoint floor enables modality perfcaches.** Above T0, modalities
   may emit O(1) ROM blobs (same *blob class* as chess position perfcache —
   peer of `codepoint_table_load_perfcache`, not a second tier-0). Tier-0 remains
   codepoints; modality ROM eliminates repeat DB resolve of composed ids, it does
   not replace the floor.
4. **Witnessed / calculated split.** Raw recovered values are witnessed by
   encoding them into codepoint trajectories under this law. Interpretation
   (Fourier, object detectors, learned embeddings) is calculated, versioned,
   evictable: emit attestations, discard the transient transform.

---

## Packaging vs identity

Containers (JPEG, PNG, MP3, FLAC, Ogg Vorbis, WAV, MP4, …) are **unpack codecs**.
They are not the modality and are not hashed for identity.

| Layer | Owns |
|-------|------|
| Packaging | `media_decode` / `ImageFileOpen` / `AudioFileOpen` — recover channel values / samples / frames only |
| Identity ladder | compose those values into **codepoint trajectories** (digit → number → …), then modality tiers |
| Corpus pairing | Source decomposers may attest content ↔ track with existing relations (e.g. `HAS_RECORDING`) — they do **not** become media lanes |

Packaging output (planar RGBA bytes, mono PCM samples, ordered frames) is an
**intermediate recovery buffer**, not a tier-0 alphabet. After recovery, encode
into the shared content law below. Never blake3 the packaging buffer or the
packed channel tuple as a leaf id.

---

## Number / digit encoding (locked by existing substrate law)

Quantized scalars (channel intensities, sample amplitudes, counts, indices that
enter a modality ladder as numbers) use the **already-shipped text content /
recipe-scalar law** — not a new modality numeric alphabet.

**Canonical form**

1. Render the integer in **decimal ASCII** via invariant culture digit string
   (`CultureInfo.InvariantCulture` / UTF-8 bytes of `'0'`…`'9'`, optionally `'-'`
   for signed audio samples). No leading zeros except the number zero itself
   (`"0"`).
2. Tier-0 leaves are the **Unicode digit codepoints** (and sign if present) —
   U+0030…U+0039, U+002D — resolved through the existing T0 / UCA perfcache.
3. The **number** id is the content-decomposition root of that digit string
   (text content law). Single-digit values collapse to their codepoint ids
   (tier-floor collapse). Multi-digit values compose above T0 like any other
   short text root.

**Authority in code (do not fork)**

- `ModelCoordinates.ScalarId` — `app/Laplace.Decomposers/Model/ModelCoordinates.cs`
  (`value.ToString(CultureInfo.InvariantCulture)` → `TryDecomposeRoot` on UTF-8).
- Comments there pin: scalar identity = text content law; single digits =
  codepoint ids; requires codepoint perfcache.

**Operator white example (image channel 255)**

```
[[2,5,5],[2,5,5],[2,5,5]]
```

Three channels; each channel value **255** is the ordered digit codepoints
`2`, `5`, `5` → composed **number** → **channel** → **pixel** → **patch** →
**region** → **image**. Tier-0 in that trajectory is only the digit codepoints
(and whatever other Unicode leaves a future signed/fractional extension needs);
the pixel is never `blake3({R,G,B,A})`.

No hex, packed-byte, or “alphabet index = identity” shortcut for ladder leaves.
Hex appears elsewhere in the substrate (ids, dumps); it is not the modality
number law.

---

## What private alphabets were (void)

The following are **void** as tier-0 identity. Keep only as packaging /
recovery documentation until rip:

| Void claim | Why void |
|------------|----------|
| Image atom = 4 bytes `{R,G,B,A}` blake3 | Private alphabet; bypasses codepoint floor |
| Packed RGBA uint32 total-order rank as T0 | Order may inform *composition schedule*, not a second T0 |
| Audio atom = LE int16 blake3 | Private alphabet |
| Amplitude-order 65 536-atom audio T0 | Same defect |
| On-demand mint of 2³² color atoms as T0 | Alphabet size argument for a false floor |

S³ / Hilbert geometry still derives from content; it does not justify a separate
atom id space. Chess already shows the correct pattern: composition above the
shared codepoint floor, optional ROM perfcache for hot composed forms.

---

## Ladders (composition above codepoints)

### Text (reference — live)

- Tier-0: codepoint / UCA order (perfcache)
- Tiers: grapheme → word → sentence → document
- Witnessed ≈ semantic for lexical sources

### Code (witnessed AST)

- Tier-0: still Unicode/source text under content law (existing code lane) —
  not a byte-hash floor that fragments from text
- Segmentation: tree-sitter unpack → ordered AST child sequence
- Relations: `HAS_AST_CHILD`, `HAS_AST_KIND` (+ existing `CONTAINS` / `CALLS` / …)

### Image (generic lane — target shape)

- Dispatch: `rgba-image` → `RgbaImageDecomposer` (names may stay; identity must change)
- Packaging: JPEG, PNG, BMP, GIF, TGA, planar `.rgba` → recover per-channel bytes
- **Encode:** each channel value → digit codepoints → number → channel sequence →
  pixel → patch → region → image (operator white)
- **Leaf order rock lock (v1, still binding for higher tiers):** patch-major —
  patches in row-major grid order; within each patch, pixels row-major. Changing
  it reassigns every Patch/Region/Image id. Hilbert 2D→1D remains a future
  scan-order option (new lock, not a silent swap).
- Patch size: `LAPLACE_IMAGE_PATCH_SIZE` = 8 (reassigns higher ids if changed)
- Relations: `HAS_REGION`, `HAS_PATCH`; reuse `ADJACENT_TO_PIXEL`, `IS_PIXEL_OF`,
  `DEPICTS`, `CAPTIONS`
- Absent alpha from RGB packaging: recover as `A = 0xFF` **value**, then encode
  that value under the digit law (same as other channels) — not a packed-RGBA leaf

### Audio (generic track lane — target shape)

- Dispatch: `track-audio` → `TrackAudioDecomposer`
- Packaging: WAV, MP3, FLAC, Ogg Vorbis (+ magic sniff) → recover sample values
- **Encode:** each sample amplitude → digit (signed decimal) codepoint trajectory
  → number → sample → window/frame → onset segment → phrase → track
- Channel remains a **partition** of streams (not a tier), unless a future law
  explicitly composes multi-channel trajectories
- Onset segmentation for witnessed infra may stay fixed-hop placeholder; real
  onset detector = calculated layer later
- Calculated later: `HAS_SPECTRAL_PELOT`, `HAS_ONSET_SEGMENT` — attest, discard STFT

### Video (generic lane — target shape)

- Dispatch: `frame-video` → `FrameVideoDecomposer`
- Spatial: image ladder per frame (same codepoint-floor image law)
- Temporal: `PRECEDES_IN_TIME`; membership `HAS_FRAME` / `IS_FRAME_OF`
- Root: content-addressed over ordered frame roots (not path hash)
- Container demux is packaging only

### Chess (analogy — already correct class)

- Tier-0 remains codepoints / shared floor
- Position (and related) ROM is an **above-T0** perfcache blob, not a private T0
- Image/audio modality ROMs, when built, follow this class

---

## Reseed / freeze implications

- Relation canonicals in `engine/manifest/relation_types.toml` stay ADR 0001
  append-only (no renumber).
- Any ids already minted under void private alphabets are **not** the identity
  law going forward. Do not “freeze” packed-RGBA or PCM16 atom alphabets as rock
  locks; rip and replace with codepoint-floor composition before corpus seed of
  media lanes.
- First image/audio seed under the **corrected** law freezes digit rendering and
  higher-tier leaf order (patch-major, hop sizes), not a private T0 alphabet.

---

## Non-goals (this law doc)

- Does not authorize implementing native ladders in the same turn as a doc-only
  agent task.
- Does not make embeddings, spectrograms, or container bytes identity.
- Does not elevate packaging recovery buffers to tier-0.
