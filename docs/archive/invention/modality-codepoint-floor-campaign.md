> Archived campaign inventory. Paths and action state are historical.

# MACHINE CHECKLIST — rip private T0 alphabets → codepoint floor

Binding law: [`modality-ladder-law.md`](modality-ladder-law.md).
Digit/number ids: `ModelCoordinates.ScalarId` / text `TryDecomposeRoot` (decimal ASCII).

**Scope of this file:** enumeration only. No native ladder implementation here.

Legend: `RIP` = remove private alphabet mint; `REWIRE` = keep surface, change identity;
`KEEP` = packaging / recovery only; `ADD` = new above-T0 ROM or shared number helper;
`DOC` = prose/comments must match law; `TEST` = must assert codepoint-floor ids.

---

## Engine native — private alphabet (RIP / REWIRE)

| Path | Symbols / notes | Action |
|------|-----------------|--------|
| `engine/core/include/laplace/core/modality_atoms.h` | `laplace_modality_t` only (dispatch tags) | **DONE** — forged mint/pack APIs removed |
| `engine/core/src/modality_atoms.c` | — | **DONE** — deleted |
| `engine/core/include/laplace/core/modality_atom_cache.h` | — | **DONE** — deleted (superseded by T0 + number ROM) |
| `engine/core/src/modality_atom_cache.c` | — | **DONE** — deleted |
| `engine/core/include/laplace/core/image_decomposer.h` | digit→number→channel→pixel→… | **DONE** |
| `engine/core/src/image_decomposer.c` | codepoint floor tree | **DONE** |
| `engine/core/include/laplace/core/audio_decomposer.h` | sample = number over digit codepoints | **DONE** |
| `engine/core/src/audio_decomposer.c` | codepoint floor tree | **DONE** |
| `engine/core/include/laplace/core/modality_witness.h` | emit + type floors | **DONE** |
| `engine/core/src/modality_witness.c` | codepoint compose + number ROM O(1) for 0..255 | **DONE** |
| `engine/core/include/laplace/core/super_fibonacci.h` | comment | **DONE** |
| `engine/core/CMakeLists.txt` | no atoms.c / atom_cache | **DONE** |
| `engine/core/tests/CMakeLists.txt` | no `test_modality_atoms` | **DONE** |
| `engine/core/tests/test_modality_atoms.cpp` | — | **DONE** — deleted |
| `engine/core/tests/test_image_decomposer.cpp` | codepoint-floor assertions | **DONE** |
| `engine/core/tests/test_audio_decomposer.cpp` | digit-leaf assertions | **DONE** |

---

## Packaging decode — KEEP (recovery only)

| Path | Symbols / notes | Action |
|------|-----------------|--------|
| `engine/core/include/laplace/core/media_decode.h` | `laplace_media_decode_*`, struct fields `rgba` / PCM | **KEEP** unpack; **DOC** — output is recovery buffer, not identity / not T0 |
| `engine/core/src/media_decode.c` | dispatch | **KEEP** |
| `engine/core/src/media_decode_impl.c` | stb/dr glue | **KEEP** |
| `engine/core/src/media_decode_vorbis.c` | vorbis path | **KEEP** |
| `engine/core/tests/test_media_decode.cpp` | codec roundtrips | **KEEP** (may add assertion decode ≠ identity) |
| `engine/core/third_party/media/*` | stb_image, dr_*, stb_vorbis | **KEEP** |
| `engine/core/third_party/media/README.md` | “identity is planar RGBA / mono int16” | **DOC** → packaging recovers values; identity = codepoint trajectories |

---

## C# spines / interop / modality mirrors

| Path | Symbols / notes | Action |
|------|-----------------|--------|
| `app/Laplace.Core/Core/LaplaceModality.cs` | mirror of `laplace_modality_t` | **RIP/REWIRE** with native enum |
| `app/Laplace.Core/Core/MediaDecode.cs` | `MediaDecode.DecodeImageFile`, `DecodeAudioFile`; identity comments | **KEEP** decode; **DOC** not identity |
| `app/Laplace.Core/Core/NativeInterop.cs` | `MediaDecodeImageFile`, `MediaDecodeAudioFile`, modality witness/atom cache entrypoints, comment “image RGBA / audio PCM16” | **REWIRE** P/Invokes after native rip; **DOC** |
| `app/Laplace.Core/Core/IntentStage.cs` | `BuildImageTree`, `ImageRootId`, audio tree/root helpers (modality path) | **REWIRE** to codepoint-floor compose |
| `app/Laplace.Core/Core/IngestSourceProfile.cs` | rgba-image / track-audio / frame-video profiles | **DOC/REWIRE** sizing comments (“planar RGBA identity” → recovery) |
| `app/Laplace.Substrate/Abstractions/ImageTierSpine.cs` | `BuildTree`, `ResolveRoot`, `EmitTree`, `ExistenceEmitBitmapAsync` | **REWIRE** — sibling of `ContentTierSpine` must share T0 floor |
| `app/Laplace.Substrate/Abstractions/AudioTierSpine.cs` | same for PCM | **REWIRE** |
| `app/Laplace.Substrate/Abstractions/MediaIngestAdapter.cs` | `ImageIngestRecord`, `AudioIngestRecord`, adapters calling spines | **REWIRE** encode after recovery |
| `app/Laplace.Substrate/Abstractions/EntityTypeRegistry.cs` | media entity types if any assume color/sample atoms | audit **REWIRE** |
| `app/Laplace.Core.Tests/Core/MediaLadderInteropTests.cs` | native ladder interop | **TEST** |

---

## C# decomposers / codecs (thin lanes)

| Path | Symbols / notes | Action |
|------|-----------------|--------|
| `app/Laplace.Decomposers/Media/RgbaImageDecomposer.cs` | `rgba-image` lane | **REWIRE** identity via corrected spine |
| `app/Laplace.Decomposers/Media/RgbaImageSource.cs` | source id | keep name ok |
| `app/Laplace.Decomposers/Media/TrackAudioDecomposer.cs` | `track-audio` | **REWIRE** |
| `app/Laplace.Decomposers/Media/TrackAudioSource.cs` | | keep |
| `app/Laplace.Decomposers/Media/FrameVideoDecomposer.cs` | `frame-video` → image spine per frame | **REWIRE** |
| `app/Laplace.Decomposers/Media/FrameVideoSource.cs` | | keep |
| `app/Laplace.Decomposers/Media/ImageFileOpen.cs` | packaging open | **KEEP**; **DOC** |
| `app/Laplace.Decomposers/Media/AudioFileOpen.cs` | packaging open | **KEEP**; **DOC** |
| `app/Laplace.Decomposers/Media/PngRgbaDecoder.cs` | managed PNG→RGBA | **KEEP** recovery; **DOC** identity comments |
| `app/Laplace.Decomposers/Media/RgbaFileCodec.cs` | planar `.rgba` package | **KEEP** packaging |
| `app/Laplace.Decomposers/Media/RgbaContentAdapter.cs` | | **REWIRE/DOC** |
| `app/Laplace.Decomposers/Media/WavPcm16Codec.cs` | | **KEEP** packaging |
| `app/Laplace.Decomposers/Media/WavContentAdapter.cs` | | **REWIRE/DOC** |
| `app/Laplace.Decomposers/Composition/SeedIngestComposition.cs` | dispatch map entries | audit comments only unless types move |
| `app/Laplace.Decomposers.Tests/Media/*` | | **TEST** |

---

## Shared number law (reuse, do not fork)

| Path | Symbols / notes | Action |
|------|-----------------|--------|
| `app/Laplace.Decomposers/Model/ModelCoordinates.cs` | `ScalarId(int)`, `ScalarId(string)` | **KEEP** — authority for decimal digit → content root |
| `app/Laplace.Decomposers/Model/LlamaTokenizerParser.cs` | `TryDecomposeRoot` | **KEEP** — shared decompose |
| `app/Laplace.Substrate/Abstractions/TextEntityBuilder.cs` | `TryDecomposeRoot` | **KEEP** |
| `app/Laplace.Substrate/Abstractions/ContentTierSpine.cs` | text UAX#29 spine | **KEEP** — reference floor; media must not bypass |
| Native codepoint / UCA T0 | `codepoint_table_*`, `laplace_t0_perfcache`, `CodepointPerfcache` | **KEEP** — sole T0 |

Optional **ADD**: shared native/C# helper “integer → digit codepoint trajectory / number root” used by image/audio ladders (must call same law as `ScalarId`, not blake3 of raw bytes).

---

## Chess-class above-T0 ROM (pattern to copy, not rip)

| Path | Notes | Action |
|------|-------|--------|
| Chess position perfcache (`laplace_chess_position_perfcache`, `NativeInterop` load peer of T0) | Above-T0 ROM; tier0 stays codepoints | **PATTERN** for future image/audio modality ROM |
| `engine/core/include/laplace/core/chess_perfcache_format.h` | format reference | read-only pattern |

---

## Dispatch / CLI / scripts / gates

| Path | Notes | Action |
|------|-------|--------|
| `app/Laplace.Cli/IngestDispatchTable.cs` | `rgba-image`, `track-audio`, `frame-video` | keep keys; identity via spines |
| `app/Laplace.Cli/IngestDataPaths.cs` | media paths | **DOC** if comments claim RGBA identity |
| `scripts/ingest-source.sh` | media lane allowlist | keep |
| `app/Laplace.Substrate.Tests/Ingestion/IngestIntegrityGateTests.cs` | file existence for Media decomposers | keep; extend if encode helpers become required files |
| `app/Laplace.Substrate/Abstractions/IngestExistenceGate.cs` | if media-specific | audit |

---

## Docs / comments that still teach the void law

| Path | Action |
|------|--------|
| `docs/invention/modality-ladder-law.md` | **DONE** — binding rewrite |
| `docs/invention/modality-codepoint-floor-checklist.md` | this file |
| `docs/INDEX.md` | **DOC** blurb if it still says pre-floor notes |
| `docs/invention/00-CONTINUITY.md` | pointer ok; no private-alphabet summary |
| `engine/core/include/laplace/core/modality_atoms.h` | **DONE** — enum-only dispatch tags |
| `engine/core/include/laplace/core/media_decode.h` identity sentences | **DOC** |
| `app/Laplace.Core/Core/MediaDecode.cs` / `NativeInterop.cs` comments | **DOC** |
| `app/Laplace.Substrate/Abstractions/ImageTierSpine.cs` / `AudioTierSpine.cs` summaries | **DOC** |

Out of scope unless they assert media T0: `web/.../ModalityMap.tsx`, `extension/.../modality_counts.sql.in`, `app/Laplace.Core/Modality/*` (turn modality, not media ladder).

---

## Acceptance predicates (machine-checkable later)

1. No `blake3` of `{R,G,B,A}` or LE int16 as ladder leaf id.
2. Image white `255` channel decomposes through digit codepoints `2`,`5`,`5` (same ids as text/scalar `"255"`).
3. No `laplace_modality_atom_id` / packed-RGBA/PCM blake3 leaf mint remains in the tree.
4. `MediaDecode` / `media_decode` produce recovery buffers only; root ids come from compose-after-encode.
5. Modality ROM, if present, loads like chess perfcache (above T0), never replaces UCA T0.
