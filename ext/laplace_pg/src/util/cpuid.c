/*
 * cpuid.c — implementation of CpuidService.
 *
 * Detects CPU features via the CPUID instruction (Intel/AMD x86_64). Runs
 * once at first call; subsequent calls return the cached result.
 *
 * Reference: Intel SDM Vol. 2A, Section 3.3 (CPUID).
 */

#include "laplace_pg/cpuid.h"

#include <stdint.h>
#include <string.h>

#if defined(_MSC_VER)
#  include <intrin.h>
#  define LAPLACE_CPUID(out, leaf)        __cpuid((int *)(out), (leaf))
#  define LAPLACE_CPUIDEX(out, leaf, sub) __cpuidex((int *)(out), (leaf), (sub))
#  define LAPLACE_XGETBV(idx)             ((uint64_t) _xgetbv(idx))
#elif defined(__GNUC__) || defined(__clang__)
#  include <cpuid.h>
#  include <immintrin.h>
static inline void LAPLACE_CPUID(unsigned int *out, unsigned int leaf) {
    __get_cpuid(leaf, &out[0], &out[1], &out[2], &out[3]);
}
static inline void LAPLACE_CPUIDEX(unsigned int *out, unsigned int leaf, unsigned int sub) {
    __get_cpuid_count(leaf, sub, &out[0], &out[1], &out[2], &out[3]);
}
static inline uint64_t LAPLACE_XGETBV(unsigned int idx) {
    unsigned int eax, edx;
    __asm__ __volatile__("xgetbv" : "=a"(eax), "=d"(edx) : "c"(idx));
    return ((uint64_t) edx << 32) | eax;
}
#else
#  error "Unsupported compiler for CPUID detection"
#endif

static laplace_cpu_features_t g_features;
static int                    g_initialized = 0;

static void laplace_cpuid_detect(void)
{
    unsigned int regs[4] = {0, 0, 0, 0};

    /* Leaf 1: classical features */
    LAPLACE_CPUID(regs, 1);
    const unsigned int ecx1 = regs[2];
    g_features.sse42  = (ecx1 & (1u << 20)) != 0;
    g_features.fma3   = (ecx1 & (1u << 12)) != 0;
    g_features.aes_ni = (ecx1 & (1u << 25)) != 0;

    const int has_osxsave  = (ecx1 & (1u << 27)) != 0;
    const int has_avx_cpu  = (ecx1 & (1u << 28)) != 0;
    int       avx_enabled  = 0;
    int       avx512_enabled = 0;
    if (has_osxsave && has_avx_cpu) {
        const uint64_t xcr0 = LAPLACE_XGETBV(0);
        avx_enabled    = (xcr0 & 0x6u) == 0x6u;       /* XMM | YMM */
        avx512_enabled = (xcr0 & 0xE6u) == 0xE6u;     /* + opmask + ZMM_lo + ZMM_hi */
    }

    /* Leaf 7 sub-leaf 0: extended features */
    LAPLACE_CPUIDEX(regs, 7, 0);
    const unsigned int ebx7 = regs[1];
    const unsigned int ecx7 = regs[2];
    g_features.bmi1     = (ebx7 & (1u <<  3)) != 0;
    g_features.avx2     = avx_enabled && ((ebx7 & (1u <<  5)) != 0);
    g_features.bmi2     = (ebx7 & (1u <<  8)) != 0;
    g_features.avx512_f = avx512_enabled && ((ebx7 & (1u << 16)) != 0);
    g_features.avx512_dq = avx512_enabled && ((ebx7 & (1u << 17)) != 0);
    g_features.avx512_bw = avx512_enabled && ((ebx7 & (1u << 30)) != 0);
    g_features.avx512_vl = avx512_enabled && ((ebx7 & (1u << 31)) != 0);
    g_features.sha_ni   = (ebx7 & (1u << 29)) != 0;
    g_features.avx512_vnni = avx512_enabled && ((ecx7 & (1u << 11)) != 0);

    /* Leaf 7 sub-leaf 1: AVX2-VNNI lives here */
    LAPLACE_CPUIDEX(regs, 7, 1);
    const unsigned int eax7_1 = regs[0];
    g_features.avx2_vnni = avx_enabled && ((eax7_1 & (1u << 4)) != 0);
}

const laplace_cpu_features_t *laplace_cpuid_features(void)
{
    if (!g_initialized) {
        memset(&g_features, 0, sizeof g_features);
        laplace_cpuid_detect();
        g_initialized = 1;
    }
    return &g_features;
}
