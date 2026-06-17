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




int laplace_normalize_nfc_utf8(
    const uint8_t* utf8,
    size_t           len,
    uint8_t**        out_utf8,
    size_t*          out_len);

#ifdef __cplusplus
}
#endif
