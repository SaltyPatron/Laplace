#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Native safetensors-style package writer.
 *
 * Emits a directory containing:
 *   model.safetensors         — tensor data (single shard for proof)
 *   model.safetensors.index.json — shard manifest
 *   config.json               — from recipe
 *   tokenizer.json            — from substrate tokenizer entity
 *   provenance.json           — substrate source lineage metadata
 *
 * Sparse-by-construction per RULES.md R4: tensor positions with no
 * supporting attestation emit exact zero. The writer does not verify
 * this — the caller is responsible for zeroing unsupported positions.
 *
 * Real impl lands Chunk 7 Story 7.15b. */
typedef struct format_writer format_writer_t;

/* Create a package writer. `format` must be "safetensors".
 * `output_dir_path` is created if it does not exist.
 * Returns NULL on failure. */
format_writer_t* format_writer_create(const char* format, const char* output_dir_path);

/* Stage a tensor for writing. dtype: 0=f32, 1=f16, 2=bf16.
 * data_len must equal product(shape) * sizeof(dtype).
 * Returns 0 on success, -1 on error. */
int format_writer_add_tensor(format_writer_t* w,
                             const char*      name,
                             int              dtype,
                             const size_t*    shape,
                             size_t           rank,
                             const void*      data,
                             size_t           data_len);

/* Set config.json content (canonical sorted JSON bytes). */
int format_writer_set_config(format_writer_t* w, const char* config_json, size_t len);

/* Set tokenizer.json content (raw bytes from substrate tokenizer entity). */
int format_writer_set_tokenizer(format_writer_t* w, const char* tokenizer_json, size_t len);

/* Finalize: write all files to output_dir_path. Returns 0 on success. */
int format_writer_finalize(format_writer_t* w);

void format_writer_free(format_writer_t* w);

#ifdef __cplusplus
}
#endif
