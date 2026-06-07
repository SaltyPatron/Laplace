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

#ifdef __cplusplus
}
#endif
