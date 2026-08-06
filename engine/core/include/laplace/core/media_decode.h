#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Packaging decode — NOT identity. Image identity is planar RGBA; audio identity
 * is the mono int16 sample sequence the ladder consumes. Containers (JPEG, PNG,
 * WebP-via-stb, MP3, FLAC, Ogg Vorbis, WAV, …) unpack here.
 */

typedef struct laplace_media_image {
    uint8_t* rgba; /* tightly packed row-major RGBA8; free with laplace_media_free */
    uint32_t width;
    uint32_t height;
} laplace_media_image_t;

typedef struct laplace_media_audio {
    int16_t* pcm; /* mono LE int16; free with laplace_media_free */
    size_t   n_samples;
    int      sample_rate;
} laplace_media_audio_t;

void laplace_media_free(void* p);

/* 0 = ok, nonzero = unsupported / corrupt / I/O. */
int laplace_media_decode_image_file(const char* path, laplace_media_image_t* out);
int laplace_media_decode_image_memory(
    const uint8_t* data, size_t len, laplace_media_image_t* out);

int laplace_media_decode_audio_file(const char* path, laplace_media_audio_t* out);
/* hint_ext: ".mp3" / "mp3" / NULL (sniff). */
int laplace_media_decode_audio_memory(
    const uint8_t* data, size_t len, const char* hint_ext, laplace_media_audio_t* out);

#ifdef __cplusplus
}
#endif
