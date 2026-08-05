# Syzygy tablebase test fixtures

The two smallest 3-men Syzygy endgame tables (WDL `.rtbw` + DTZ `.rtbz`,
~14 KB total), committed so closings-catalog ingest (`chess-syzygy`) and its
extract codec (Fathom via `engine/core/src/syzygy.c` / `external/fathom`) can
be tested without any external download. After ingest, `HAS_WDL` / `HAS_DTZ`
rows are substrate authority — reads must not require a vault mmap.

| File | Bytes | MD5 |
|---|---|---|
| `KQvK.rtbw` | 272 | `f06221548404795b6b33469e247b4560` |
| `KQvK.rtbz` | 5392 | `ac866466e16eb19a4f8c796f8e1abd2b` |
| `KRvK.rtbw` | 208 | `89a27823bfa03d0b0f25728c4f0fb571` |
| `KRvK.rtbz` | 7632 | `9cb0795fa43904a3e91bf749971964b8` |

Origin: <https://tablebase.lichess.ovh/tables/standard/> (`3-4-5-wdl/`,
`3-4-5-dtz/`), fetched 2026-07-30; MD5s above match that mirror's published
`md5` manifest.

License note: Syzygy tablebases are generated factual data (win/draw/loss and
distance-to-zeroing values of chess positions), computed by Ronald de Man's
tablebase generator and redistributed freely by the community mirrors
(lichess.org among them). The files carry no license text of their own; the
probing CODE (Fathom) is MIT and pinned at `external/fathom`.

The full 3-4-5 set (~1 GB) lives outside the repo — packaging directory for
`laplace ingest chess-syzygy <dir>` (also `LAPLACE_SYZYGY` /
`Games/Chess/syzygy/3-4-5` under the data root). Fathom is the extract codec
only; each board product becomes a position-grain substrate record.
