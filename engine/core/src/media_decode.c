#include "laplace/core/media_decode.h"

#include <ctype.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../third_party/media/dr_flac.h"
#include "../third_party/media/dr_mp3.h"
#include "../third_party/media/dr_wav.h"

/* stb_image (implemented in media_decode_impl.c) */
extern unsigned char* stbi_load_from_memory(
    unsigned char const* buffer, int len, int* x, int* y, int* channels_in_file, int desired_channels);
extern void stbi_image_free(void* retval_from_stbi_load);

/* stb_vorbis (media_decode_vorbis.c) */
extern int stb_vorbis_decode_memory(
    const unsigned char* mem, int len, int* channels, int* sample_rate, short** output);

void laplace_media_free(void* p)
{
    free(p);
}

static char* read_entire_file(const char* path, size_t* out_len)
{
    FILE* f = fopen(path, "rb");
    long sz;
    char* buf;
    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    sz = ftell(f);
    if (sz < 0) { fclose(f); return NULL; }
    if (fseek(f, 0, SEEK_SET) != 0) { fclose(f); return NULL; }
    buf = (char*)malloc((size_t)sz);
    if (!buf) { fclose(f); return NULL; }
    if (fread(buf, 1, (size_t)sz, f) != (size_t)sz) {
        free(buf);
        fclose(f);
        return NULL;
    }
    fclose(f);
    *out_len = (size_t)sz;
    return buf;
}

static int16_t* downmix_f32_to_s16(const float* interleaved, size_t frames, int channels)
{
    int16_t* mono;
    if (channels <= 0 || frames == 0) return NULL;
    mono = (int16_t*)malloc(frames * sizeof(int16_t));
    if (!mono) return NULL;
    for (size_t i = 0; i < frames; ++i) {
        float acc = 0.f;
        for (int c = 0; c < channels; ++c)
            acc += interleaved[i * (size_t)channels + (size_t)c];
        if (channels > 1) acc /= (float)channels;
        if (acc > 1.f) acc = 1.f;
        if (acc < -1.f) acc = -1.f;
        mono[i] = (int16_t)(acc * 32767.f);
    }
    return mono;
}

static int16_t* downmix_s16(const int16_t* interleaved, size_t frames, int channels)
{
    int16_t* mono;
    if (channels <= 0 || frames == 0) return NULL;
    if (channels == 1) {
        mono = (int16_t*)malloc(frames * sizeof(int16_t));
        if (!mono) return NULL;
        memcpy(mono, interleaved, frames * sizeof(int16_t));
        return mono;
    }
    mono = (int16_t*)malloc(frames * sizeof(int16_t));
    if (!mono) return NULL;
    for (size_t i = 0; i < frames; ++i) {
        int acc = 0;
        for (int c = 0; c < channels; ++c)
            acc += interleaved[i * (size_t)channels + (size_t)c];
        mono[i] = (int16_t)(acc / channels);
    }
    return mono;
}

int laplace_media_decode_image_memory(
    const uint8_t* data, size_t len, laplace_media_image_t* out)
{
    int w = 0, h = 0, n = 0;
    unsigned char* rgba;
    size_t nbytes;
    uint8_t* copy;
    if (!data || !out || len == 0 || len > (size_t)INT_MAX) return 1;
    memset(out, 0, sizeof(*out));
    rgba = stbi_load_from_memory(data, (int)len, &w, &h, &n, 4);
    if (!rgba || w <= 0 || h <= 0) return 2;
    nbytes = (size_t)w * (size_t)h * 4u;
    copy = (uint8_t*)malloc(nbytes);
    if (!copy) {
        stbi_image_free(rgba);
        return 3;
    }
    memcpy(copy, rgba, nbytes);
    stbi_image_free(rgba);
    out->rgba = copy;
    out->width = (uint32_t)w;
    out->height = (uint32_t)h;
    return 0;
}

int laplace_media_decode_image_file(const char* path, laplace_media_image_t* out)
{
    size_t len = 0;
    char* buf;
    int rc;
    if (!path || !out) return 1;
    buf = read_entire_file(path, &len);
    if (!buf) return 2;
    rc = laplace_media_decode_image_memory((const uint8_t*)buf, len, out);
    free(buf);
    return rc;
}

static int decode_wav_mem(const uint8_t* data, size_t len, laplace_media_audio_t* out)
{
    drwav wav;
    float* f32;
    if (!drwav_init_memory(&wav, data, len, NULL)) return 1;
    f32 = (float*)malloc((size_t)wav.totalPCMFrameCount * wav.channels * sizeof(float));
    if (!f32) { drwav_uninit(&wav); return 2; }
    if (drwav_read_pcm_frames_f32(&wav, wav.totalPCMFrameCount, f32) != wav.totalPCMFrameCount) {
        free(f32);
        drwav_uninit(&wav);
        return 3;
    }
    out->pcm = downmix_f32_to_s16(f32, (size_t)wav.totalPCMFrameCount, (int)wav.channels);
    out->n_samples = (size_t)wav.totalPCMFrameCount;
    out->sample_rate = (int)wav.sampleRate;
    free(f32);
    drwav_uninit(&wav);
    return out->pcm ? 0 : 4;
}

static int decode_mp3_mem(const uint8_t* data, size_t len, laplace_media_audio_t* out)
{
    drmp3_config cfg;
    drmp3_uint64 frames = 0;
    float* f32 = drmp3_open_memory_and_read_pcm_frames_f32(
        data, len, &cfg, &frames, NULL);
    if (!f32 || frames == 0) {
        if (f32) free(f32);
        return 1;
    }
    out->pcm = downmix_f32_to_s16(f32, (size_t)frames, (int)cfg.channels);
    out->n_samples = (size_t)frames;
    out->sample_rate = (int)cfg.sampleRate;
    free(f32);
    return out->pcm ? 0 : 2;
}

static int decode_flac_mem(const uint8_t* data, size_t len, laplace_media_audio_t* out)
{
    drflac* flac = drflac_open_memory(data, len, NULL);
    int16_t* interleaved;
    if (!flac) return 1;
    interleaved = (int16_t*)malloc(
        (size_t)flac->totalPCMFrameCount * flac->channels * sizeof(int16_t));
    if (!interleaved) { drflac_close(flac); return 2; }
    if (drflac_read_pcm_frames_s16(flac, flac->totalPCMFrameCount, interleaved)
        != flac->totalPCMFrameCount) {
        free(interleaved);
        drflac_close(flac);
        return 3;
    }
    out->pcm = downmix_s16(interleaved, (size_t)flac->totalPCMFrameCount, (int)flac->channels);
    out->n_samples = (size_t)flac->totalPCMFrameCount;
    out->sample_rate = (int)flac->sampleRate;
    free(interleaved);
    drflac_close(flac);
    return out->pcm ? 0 : 4;
}

static int decode_ogg_mem(const uint8_t* data, size_t len, laplace_media_audio_t* out)
{
    int channels = 0, rate = 0;
    short* interleaved = NULL;
    int n;
    if (len > (size_t)INT_MAX) return 1;
    n = stb_vorbis_decode_memory(data, (int)len, &channels, &rate, &interleaved);
    if (n <= 0 || !interleaved) return 1;
    out->pcm = downmix_s16(interleaved, (size_t)n, channels);
    out->n_samples = (size_t)n;
    out->sample_rate = rate;
    free(interleaved);
    return out->pcm ? 0 : 2;
}

static const char* normalize_ext(const char* hint_ext, char* scratch, size_t scratch_n)
{
    const char* p;
    size_t i = 0;
    if (!hint_ext || !*hint_ext) return NULL;
    p = hint_ext;
    if (*p == '.') ++p;
    while (*p && i + 1 < scratch_n)
        scratch[i++] = (char)tolower((unsigned char)*p++);
    scratch[i] = 0;
    return scratch;
}

int laplace_media_decode_audio_memory(
    const uint8_t* data, size_t len, const char* hint_ext, laplace_media_audio_t* out)
{
    char extbuf[16];
    const char* ext;
    if (!data || !out || len == 0) return 1;
    memset(out, 0, sizeof(*out));
    ext = normalize_ext(hint_ext, extbuf, sizeof(extbuf));

    if (ext) {
        if (strcmp(ext, "wav") == 0 || strcmp(ext, "wave") == 0)
            return decode_wav_mem(data, len, out);
        if (strcmp(ext, "mp3") == 0)
            return decode_mp3_mem(data, len, out);
        if (strcmp(ext, "flac") == 0)
            return decode_flac_mem(data, len, out);
        if (strcmp(ext, "ogg") == 0 || strcmp(ext, "oga") == 0)
            return decode_ogg_mem(data, len, out);
    }

    if (len >= 12 && memcmp(data, "RIFF", 4) == 0 && memcmp(data + 8, "WAVE", 4) == 0)
        return decode_wav_mem(data, len, out);
    if (len >= 4 && memcmp(data, "fLaC", 4) == 0)
        return decode_flac_mem(data, len, out);
    if (len >= 4 && memcmp(data, "OggS", 4) == 0)
        return decode_ogg_mem(data, len, out);
    if (decode_mp3_mem(data, len, out) == 0) return 0;
    if (decode_wav_mem(data, len, out) == 0) return 0;
    if (decode_flac_mem(data, len, out) == 0) return 0;
    if (decode_ogg_mem(data, len, out) == 0) return 0;
    return 9;
}

int laplace_media_decode_audio_file(const char* path, laplace_media_audio_t* out)
{
    size_t len = 0;
    char* buf;
    const char* ext;
    int rc;
    if (!path || !out) return 1;
    buf = read_entire_file(path, &len);
    if (!buf) return 2;
    ext = strrchr(path, '.');
    rc = laplace_media_decode_audio_memory((const uint8_t*)buf, len, ext, out);
    free(buf);
    return rc;
}
