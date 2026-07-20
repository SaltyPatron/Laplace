/*
 * Unified tensor dtype codec: every safetensors numeric/bool dtype -> float32.
 *
 * One entry point for the whole dtype family. The C# side resolves a dtype NAME
 * once, then streams buffers through laplace_tensor_decode_f32; no per-element
 * work stays in managed code (engine law: C/C++ does the math, C# orchestrates).
 *
 * Exactness: ingestion must be exact and deterministic. Every conversion here is
 * value-preserving for the source encoding — IEEE half/bfloat16 widening is exact
 * into float32, and the FP8 forms reproduce their defining formulas literally.
 * SIMD paths and the scalar tail must agree bit-for-bit; the gtest suite pins that.
 *
 * Block-quantized GGUF forms (Q4_K/Q6_K/...) are deliberately absent: they are a
 * different container with their own dequantizer, and silently reading them as
 * zeros would attest garbage. Unknown dtype => LAPLACE_TENSOR_DTYPE_UNKNOWN.
 */
#ifndef LAPLACE_SYNTHESIS_TENSOR_DTYPE_CODEC_H
#define LAPLACE_SYNTHESIS_TENSOR_DTYPE_CODEC_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum laplace_tensor_dtype {
    LAPLACE_TENSOR_DTYPE_UNKNOWN = -1,
    LAPLACE_TENSOR_DTYPE_F64     = 0,
    LAPLACE_TENSOR_DTYPE_F32     = 1,
    LAPLACE_TENSOR_DTYPE_F16     = 2,
    LAPLACE_TENSOR_DTYPE_BF16    = 3,
    LAPLACE_TENSOR_DTYPE_F8_E5M2 = 4,
    LAPLACE_TENSOR_DTYPE_F8_E4M3 = 5,
    LAPLACE_TENSOR_DTYPE_I64     = 6,
    LAPLACE_TENSOR_DTYPE_I32     = 7,
    LAPLACE_TENSOR_DTYPE_I16     = 8,
    LAPLACE_TENSOR_DTYPE_I8      = 9,
    LAPLACE_TENSOR_DTYPE_U8      = 10,
    LAPLACE_TENSOR_DTYPE_BOOL    = 11
} laplace_tensor_dtype;

/* Resolve a safetensors dtype name ("F32", "BF16", ...) to its code.
 * Returns LAPLACE_TENSOR_DTYPE_UNKNOWN for anything unrecognized, including the
 * block-quant names, so the caller can refuse rather than ingest zeros. */
int laplace_tensor_dtype_from_name(const char* name);

/* Bytes per stored element, or 0 when the dtype is unknown. */
size_t laplace_tensor_dtype_size(int dtype);

/* Decode n_elements of `dtype` from raw_bytes into out (float32).
 * raw_bytes must hold at least n_elements * laplace_tensor_dtype_size(dtype).
 * Returns 0 on success, -1 on a null argument, -2 on an unknown dtype. */
int laplace_tensor_decode_f32(const void* raw_bytes,
                              size_t n_elements,
                              int dtype,
                              float* out);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_SYNTHESIS_TENSOR_DTYPE_CODEC_H */
