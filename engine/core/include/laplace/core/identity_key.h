#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Canonical identity key: trim Unicode whitespace, collapse internal runs to
 * U+0020, apply Unicode full case folding, then NFC.
 * The returned buffer is allocated with malloc and is owned by the caller. */
int laplace_identity_key_normalize_utf8(
    const uint8_t* utf8,
    size_t len,
    uint8_t** out_utf8,
    size_t* out_len);

#ifdef __cplusplus
}
#endif
