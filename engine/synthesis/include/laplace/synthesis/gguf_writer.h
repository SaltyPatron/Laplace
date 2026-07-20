#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct gguf_writer gguf_writer_t;

/*
 * HuggingFace tensor name -> GGML/GGUF tensor name.
 *
 * Format grammar, not policy: GGUF names the same tensors differently from a HF
 * checkpoint, and a writer that emits HF names produces a file no llama.cpp build
 * will load. Kept beside the writer so the mapping lives with the format it belongs
 * to instead of being retyped by each caller.
 *
 * Writes the mapped name into out_buf (NUL-terminated) and returns its length, or
 * -1 if out_buf is too small / an argument is null. An unrecognized name maps to
 * itself: unknown tensors pass through rather than being silently dropped.
 */
int gguf_tensor_name_hf_to_ggml(const char* hf_name, char* out_buf, size_t out_cap);

gguf_writer_t* gguf_writer_create(const char* output_path);

int gguf_writer_add_metadata_str(gguf_writer_t* w, const char* key, const char* value);
int gguf_writer_add_metadata_u32(gguf_writer_t* w, const char* key, uint32_t value);
int gguf_writer_add_metadata_f32(gguf_writer_t* w, const char* key, float value);
int gguf_writer_add_metadata_bool(gguf_writer_t* w, const char* key, int value);

int gguf_writer_add_metadata_str_array_packed(gguf_writer_t* w, const char* key,
                                              const uint8_t* packed_data,
                                              size_t         total_bytes,
                                              size_t         count);
int gguf_writer_add_metadata_f32_array(gguf_writer_t* w, const char* key,
                                       const float*   values, size_t count);
int gguf_writer_add_metadata_i32_array(gguf_writer_t* w, const char* key,
                                       const int32_t* values, size_t count);

int gguf_writer_add_tensor(gguf_writer_t* w,
                           const char*    name,
                           int            dtype,
                           const size_t*  shape,
                           size_t         rank,
                           const void*    data);

int gguf_writer_finalize(gguf_writer_t* w);

void gguf_writer_free(gguf_writer_t* w);

#ifdef __cplusplus
}
#endif
