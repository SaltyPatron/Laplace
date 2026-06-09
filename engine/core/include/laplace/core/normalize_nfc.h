#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

size_t laplace_normalize_nfc(
    const uint32_t* in,
    size_t          in_len,
    uint32_t*       out,
    size_t          out_cap);

/* Decode UTF-8, NFC-compose codepoints, re-encode UTF-8. Returns 0 on success.
 * On success *out_utf8 is malloc'd (caller frees); *out_len is byte length.
 * Returns -1 on null args, -2 on invalid UTF-8, -3 on allocation failure. */
int laplace_normalize_nfc_utf8(
    const uint8_t* utf8,
    size_t           len,
    uint8_t**        out_utf8,
    size_t*          out_len);

#ifdef __cplusplus
}
#endif
