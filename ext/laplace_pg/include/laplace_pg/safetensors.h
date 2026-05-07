/*
 * safetensors.h — TensorDecodeService public API.
 *
 * Phase 2 / Track B / Service B19.
 *
 * Reads HuggingFace .safetensors files. The format:
 *   bytes  0..7  : little-endian uint64 N — header byte length
 *   bytes  8..7+N: UTF-8 JSON header
 *   bytes 8+N..  : raw tensor data
 *
 * The JSON header maps tensor_name → {dtype, shape, data_offsets:[start,end]}
 * where offsets are relative to the start of the data section.
 *
 * This service does NOT load tensor data into memory — it parses the header
 * once and returns mmap'd pointers into the data section per tensor. F5
 * AI model decomposers iterate the entries, project per-token embedding
 * rows through the Laplacian eigenmap pipeline, and discard the file once
 * extraction completes (per the AI-models-as-edge-extraction invariant).
 */

#ifndef LAPLACE_SAFETENSORS_H
#define LAPLACE_SAFETENSORS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_SAFETENSORS_MAX_NAME    256
#define LAPLACE_SAFETENSORS_MAX_RANK    8

typedef enum {
    LAPLACE_DTYPE_UNKNOWN = 0,
    LAPLACE_DTYPE_F64,
    LAPLACE_DTYPE_F32,
    LAPLACE_DTYPE_F16,
    LAPLACE_DTYPE_BF16,
    LAPLACE_DTYPE_F8_E4M3,
    LAPLACE_DTYPE_F8_E5M2,
    LAPLACE_DTYPE_I64,
    LAPLACE_DTYPE_I32,
    LAPLACE_DTYPE_I16,
    LAPLACE_DTYPE_I8,
    LAPLACE_DTYPE_U64,
    LAPLACE_DTYPE_U32,
    LAPLACE_DTYPE_U16,
    LAPLACE_DTYPE_U8,
    LAPLACE_DTYPE_BOOL,
} laplace_dtype_t;

typedef struct {
    char            name[LAPLACE_SAFETENSORS_MAX_NAME];
    laplace_dtype_t dtype;
    int             rank;
    int64_t         shape[LAPLACE_SAFETENSORS_MAX_RANK];
    uint64_t        data_offset;          /* relative to data section start */
    uint64_t        data_byte_length;
} laplace_tensor_entry_t;

typedef struct laplace_safetensors_handle laplace_safetensors_handle_t;

/* Open a .safetensors file. Returns NULL on error.
 * The handle owns no large allocations beyond the parsed entry array; the
 * caller may keep the handle alive for the duration of decomposition. */
laplace_safetensors_handle_t *laplace_safetensors_open(const char *path);

void                          laplace_safetensors_close(laplace_safetensors_handle_t *h);

size_t                        laplace_safetensors_entry_count(const laplace_safetensors_handle_t *h);

const laplace_tensor_entry_t *laplace_safetensors_entry(
    const laplace_safetensors_handle_t *h, size_t index);

/* Lookup a tensor by name. Returns NULL if not present. */
const laplace_tensor_entry_t *laplace_safetensors_find(
    const laplace_safetensors_handle_t *h, const char *name);

/* Returns the absolute byte offset (within the file) of the data section
 * start. Add this to entry.data_offset to compute the absolute file offset
 * of a tensor's first byte. */
uint64_t                      laplace_safetensors_data_section_offset(
    const laplace_safetensors_handle_t *h);

/* Convert a dtype enum to its underlying element byte width (0 if unknown). */
size_t                        laplace_dtype_byte_width(laplace_dtype_t dtype);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_SAFETENSORS_H */
