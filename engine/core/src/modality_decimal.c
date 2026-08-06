#include "laplace/core/modality_decimal.h"

uint32_t laplace_decimal_codepoints_u32(uint32_t value, uint32_t out_cps[LAPLACE_DECIMAL_MAX_CPS])
{
    uint32_t n = 0;
    if (value == 0u) {
        out_cps[n++] = 0x30u;
        return n;
    }
    uint8_t rev[10];
    uint32_t nd = 0;
    uint32_t tmp = value;
    while (tmp > 0u) {
        rev[nd++] = (uint8_t)(tmp % 10u);
        tmp /= 10u;
    }
    while (nd > 0u) {
        --nd;
        out_cps[n++] = 0x30u + (uint32_t)rev[nd];
    }
    return n;
}

uint32_t laplace_decimal_codepoints_i32(int32_t value, uint32_t out_cps[LAPLACE_DECIMAL_MAX_CPS])
{
    uint32_t n = 0;
    uint32_t mag;
    if (value < 0) {
        out_cps[n++] = 0x2Du;
        /* INT32_MIN magnitude */
        mag = (uint32_t)(-(int64_t)value);
    } else {
        mag = (uint32_t)value;
    }
    uint32_t dig[LAPLACE_DECIMAL_MAX_CPS];
    uint32_t nd = laplace_decimal_codepoints_u32(mag, dig);
    for (uint32_t i = 0; i < nd; ++i)
        out_cps[n++] = dig[i];
    return n;
}
