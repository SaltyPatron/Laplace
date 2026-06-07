#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint8_t bytes[16];
} hilbert128_t;

void hilbert4d_encode(const double p[4], hilbert128_t* out);
void hilbert4d_decode(const hilbert128_t* h, double out[4]);
int  hilbert128_compare(const hilbert128_t* a, const hilbert128_t* b);

#ifdef __cplusplus
}
#endif
