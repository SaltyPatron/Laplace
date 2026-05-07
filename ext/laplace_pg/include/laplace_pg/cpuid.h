/*
 * cpuid.h — CPU feature detection for runtime SIMD dispatch.
 *
 * Phase 2 / Track B / Service B1.
 *
 * The 14900KS dev box has AVX-512 fused off (Intel disabled it on Raptor
 * Lake hybrid silicon). Production Xeon and AMD Zen 4+ get the full
 * AVX-512 paths. AVX2 is the universal baseline. VNNI / SHA-NI / AES-NI
 * are dispatched the same way. This service is queried once at extension
 * load (PG_init) and the results are cached for the process lifetime.
 */

#ifndef LAPLACE_CPUID_H
#define LAPLACE_CPUID_H

#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    bool sse42;
    bool avx2;
    bool fma3;
    bool avx512_f;       /* AVX-512 Foundation */
    bool avx512_dq;
    bool avx512_bw;
    bool avx512_vl;
    bool avx512_vnni;
    bool avx2_vnni;      /* AVX2-VNNI (256-bit, available on 14900KS) */
    bool sha_ni;
    bool aes_ni;
    bool bmi1;
    bool bmi2;
} laplace_cpu_features_t;

/* Returns the cached feature struct (detected on first call). */
const laplace_cpu_features_t *laplace_cpuid_features(void);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_CPUID_H */
