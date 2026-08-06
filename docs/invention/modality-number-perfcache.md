# Modality number compose-floor perfcache (scaffold note)

Pointer only — binding blob law is [`docs/specs/33_Perfcache_Blob_Law.md`](../specs/33_Perfcache_Blob_Law.md);
identity law is [`modality-ladder-law.md`](modality-ladder-law.md) (codepoint floor).

## What landed

| Piece | Path |
|-------|------|
| Format | `engine/core/include/laplace/core/modality_number_perfcache_format.h` |
| Load / O(1) lookup | `modality_number_table_*` |
| Emit | `engine/core/tools/modality_number_tables_emit` → `laplace_modality_number_perfcache.bin` |
| CMake | `laplace_modality_number_perfcache` (depends on t0 only — CI-safe, no corpus) |

## Why v1 is 0..255

Image packaging recovers uint8 channel intensities. The ladder encodes each as
decimal digit codepoints → number (operator white: `255` → `2`,`5`,`5`). A dense
256-slot table makes `records[value]` true O(1) with no DB call. Audio shares the
same number law; full signed sample range is a later scope, not a private T0.

## Still TODO

- Extension GUC / prewarm (peer of chess_position_perfcache_path)
- App `NativeInterop` + managed floor wrapper / process load
- Higher ROM layers: channel→pixel→patch→… / sample→window→…
- Optional signed PCM scope (or separate blob) when audio needs full int16 table
- Determinism gate (double-emit compare) in CI

Compose already prefers `modality_number_table_lookup_geom` for unsigned 0..255
when the table is loaded (`modality_witness.c`); without load it falls back to
`laplace_content_root_id` of the digit UTF-8.
