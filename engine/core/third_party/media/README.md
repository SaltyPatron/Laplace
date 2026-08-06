# Vendored media packaging codecs

Single-header / single-file unpack libraries used by `laplace_media_decode_*`.
These are **packaging** only: they recover planar RGBA / mono int16 buffers from
containers. The recovered buffers are NOT identity — identity comes from the
codepoint-floor ladder (digit → number → … compose per modality-ladder-law.md);
no recovery buffer is ever hashed as a leaf.

| File | Upstream | Role |
|------|----------|------|
| `stb_image.h` | nothings/stb | JPEG, PNG, BMP, GIF, TGA → RGBA |
| `stb_vorbis.c` | nothings/stb | Ogg Vorbis → PCM |
| `dr_wav.h` | mackron/dr_libs | WAV → PCM |
| `dr_mp3.h` | mackron/dr_libs | MP3 → PCM |
| `dr_flac.h` | mackron/dr_libs | FLAC → PCM |

Licenses: `LICENSE.stb`, `LICENSE.dr_libs`.
