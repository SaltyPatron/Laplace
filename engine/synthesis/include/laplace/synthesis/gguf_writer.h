#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct gguf_writer gguf_writer_t;

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
