/*
 * safetensors header parser.
 *
 * Container shape: [u64 little-endian JSON length][JSON header][tensor data...].
 * The JSON maps tensor name -> { dtype, shape[], data_offsets[2] }, plus an optional
 * "__metadata__" object. Offsets are relative to the END of the header, so absolute
 * position = 8 + json_len + data_offset.
 *
 * Entries come back sorted by data_offsets[0] — the on-disk order — so a caller that
 * walks them in index order reads the file forward instead of seeking backwards.
 *
 * Refusal, not repair: a truncated buffer, a non-object header, a missing dtype /
 * shape / data_offsets, or reversed offsets return NULL. A model whose header does
 * not describe its own bytes must not be half-ingested.
 */
#ifndef LAPLACE_SYNTHESIS_SAFETENSORS_PARSER_H
#define LAPLACE_SYNTHESIS_SAFETENSORS_PARSER_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct safetensors_header safetensors_header_t;

/* Parse the leading header of a safetensors file. `bytes` must cover at least the
 * 8-byte length prefix plus the JSON it declares; the tensor data itself is not
 * needed and is never read here. Returns NULL on any malformed input. */
safetensors_header_t* safetensors_parse_header(const void* bytes, size_t len);

/* Bytes consumed by the container header (8 + JSON length); tensor data starts here. */
long long safetensors_header_bytes(const safetensors_header_t* h);

/* Number of tensors, excluding the "__metadata__" pseudo-entry. */
int safetensors_tensor_count(const safetensors_header_t* h);

/* True when the header carried a "__metadata__" object. The block's CONTENT is
 * source-asserted provenance the recorder should witness — see GH #480. */
int safetensors_has_metadata(const safetensors_header_t* h);

/* Per-tensor accessors; index is 0..count-1 in data-offset order.
 * Name/dtype pointers stay valid until safetensors_header_free. */
const char* safetensors_tensor_name(const safetensors_header_t* h, int index);
const char* safetensors_tensor_dtype(const safetensors_header_t* h, int index);
int         safetensors_tensor_rank(const safetensors_header_t* h, int index);
long long   safetensors_tensor_dim(const safetensors_header_t* h, int index, int axis);
long long   safetensors_tensor_data_start(const safetensors_header_t* h, int index);
long long   safetensors_tensor_data_end(const safetensors_header_t* h, int index);

void safetensors_header_free(safetensors_header_t* h);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_SYNTHESIS_SAFETENSORS_PARSER_H */
