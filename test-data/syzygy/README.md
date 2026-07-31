# Syzygy tablebase test fixtures

The two smallest 3-men Syzygy endgame tables (WDL `.rtbw` + DTZ `.rtbz`,
~14 KB total), committed so the probe kernel (`engine/core/src/syzygy.c`, via
the vendored Fathom prober at `external/fathom`) and the `ChessSyzygy` lane
tests run without any external download.

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

The full 3-4-5 set (~1 GB) lives outside the repo — `LAPLACE_SYZYGY` points
at its directory (hart-server: `/vault/Data/Games/Chess/syzygy/3-4-5/`).
