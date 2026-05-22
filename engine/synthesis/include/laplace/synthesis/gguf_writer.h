#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* GGUF format writer — emits a model file in GGUF binary format
 * (llama.cpp's interchange format). Per RULES.md R4 (sparse-by-
 * construction emission): positions with no significant substrate
 * attestation emit exact zero.
 *
 * The GGUF spec is documented at github.com/ggerganov/ggml. We
 * implement the writer directly (substrate-specific emission logic
 * around the format spec) rather than linking llama.cpp/ggml (per
 * RULES.md R15: llama.cpp is banned as a runtime).
 *
 * Real impl lands Chunk 7 Story 7.15. */
typedef struct gguf_writer gguf_writer_t;

/* Begin writing a GGUF file. `output_path` will be truncated. */
gguf_writer_t* gguf_writer_create(const char* output_path);

/* Add a metadata key-value pair (architecture name, vocab, etc.). */
int gguf_writer_add_metadata_str(gguf_writer_t* w, const char* key, const char* value);
int gguf_writer_add_metadata_u32(gguf_writer_t* w, const char* key, uint32_t value);

/* Add a tensor. `data` is `n_elements * elem_size_bytes(dtype)` bytes.
 * Sparse-by-construction: zero entries are encoded according to the
 * quantization scheme. */
int gguf_writer_add_tensor(gguf_writer_t* w,
                           const char*    name,
                           int            dtype,
                           const size_t*  shape,
                           size_t         rank,
                           const void*    data);

/* Finalize and close. Returns 0 on success. */
int gguf_writer_finalize(gguf_writer_t* w);

void gguf_writer_free(gguf_writer_t* w);

#ifdef __cplusplus
}
#endif
