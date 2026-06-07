#include "laplace/core/mantissa.h"

#include <string.h>

#define LAPLACE_FP_EXP_BIASED_ZERO 0x3FFULL
#define LAPLACE_FP_EXP_SHIFT       52
#define LAPLACE_MANTISSA_MASK_52   ((1ULL << 52) - 1ULL)

#define LAPLACE_FLAGS_TOTAL_BITS   52u
#define LAPLACE_FLAGS_MASK         ((1ULL << LAPLACE_FLAGS_TOTAL_BITS) - 1ULL)
#define LAPLACE_FLAGS_Z_BITS       31u
#define LAPLACE_FLAGS_Z_MASK       ((1ULL << LAPLACE_FLAGS_Z_BITS) - 1ULL)
#define LAPLACE_FLAGS_M_BITS       21u
#define LAPLACE_FLAGS_M_MASK       ((1ULL << LAPLACE_FLAGS_M_BITS) - 1ULL)

#define LAPLACE_X_HASH_BITS        53u
#define LAPLACE_X_HASH_MASK        ((1ULL << LAPLACE_X_HASH_BITS) - 1ULL)
#define LAPLACE_Y_LO_BITS          11u
#define LAPLACE_Y_LO_MASK          ((1ULL << LAPLACE_Y_LO_BITS) - 1ULL)
#define LAPLACE_Y_HI_BITS          42u
#define LAPLACE_Y_HI_MASK          ((1ULL << LAPLACE_Y_HI_BITS) - 1ULL)
#define LAPLACE_Z_HASH_BITS        22u
#define LAPLACE_Z_HASH_MASK        ((1ULL << LAPLACE_Z_HASH_BITS) - 1ULL)

static inline double laplace_slot_to_fp(uint64_t slot) {
    const uint64_t sign     = (slot >> 52) & 1ULL;
    const uint64_t mantissa = slot & LAPLACE_MANTISSA_MASK_52;
    const uint64_t bits     =
        (sign << 63) |
        (LAPLACE_FP_EXP_BIASED_ZERO << LAPLACE_FP_EXP_SHIFT) |
        mantissa;
    double out;
    memcpy(&out, &bits, sizeof(out));
    return out;
}

static inline uint64_t laplace_fp_to_slot(double d) {
    uint64_t bits;
    memcpy(&bits, &d, sizeof(bits));
    const uint64_t sign     = (bits >> 63) & 1ULL;
    const uint64_t mantissa = bits & LAPLACE_MANTISSA_MASK_52;
    return (sign << 52) | mantissa;
}

void mantissa_pack(double vertex[4], const mantissa_payload_t* p) {
    const uint64_t h_lo  = p->entity_id.lo;
    const uint64_t h_hi  = p->entity_id.hi;
    const uint64_t flags = p->flags & LAPLACE_FLAGS_MASK;

    const uint64_t slot_x = h_lo & LAPLACE_X_HASH_MASK;

    const uint64_t y_lo = (h_lo >> LAPLACE_X_HASH_BITS) & LAPLACE_Y_LO_MASK;
    const uint64_t y_hi = h_hi & LAPLACE_Y_HI_MASK;
    const uint64_t slot_y = y_lo | (y_hi << LAPLACE_Y_LO_BITS);

    const uint64_t z_hash  = (h_hi >> LAPLACE_Y_HI_BITS) & LAPLACE_Z_HASH_MASK;
    const uint64_t z_flags = flags & LAPLACE_FLAGS_Z_MASK;
    const uint64_t slot_z  = z_hash | (z_flags << LAPLACE_Z_HASH_BITS);

    const uint64_t m_ord   = (uint64_t)p->ordinal;
    const uint64_t m_run   = (uint64_t)p->run_length;
    const uint64_t m_flags = (flags >> LAPLACE_FLAGS_Z_BITS) & LAPLACE_FLAGS_M_MASK;
    const uint64_t slot_m  = m_ord | (m_run << 16) | (m_flags << 32);

    vertex[0] = laplace_slot_to_fp(slot_x);
    vertex[1] = laplace_slot_to_fp(slot_y);
    vertex[2] = laplace_slot_to_fp(slot_z);
    vertex[3] = laplace_slot_to_fp(slot_m);
}

void mantissa_unpack(const double vertex[4], mantissa_payload_t* out) {
    const uint64_t slot_x = laplace_fp_to_slot(vertex[0]);
    const uint64_t slot_y = laplace_fp_to_slot(vertex[1]);
    const uint64_t slot_z = laplace_fp_to_slot(vertex[2]);
    const uint64_t slot_m = laplace_fp_to_slot(vertex[3]);

    const uint64_t x_hash = slot_x & LAPLACE_X_HASH_MASK;
    const uint64_t y_lo   = slot_y & LAPLACE_Y_LO_MASK;
    const uint64_t y_hi   = (slot_y >> LAPLACE_Y_LO_BITS) & LAPLACE_Y_HI_MASK;
    const uint64_t z_hash = slot_z & LAPLACE_Z_HASH_MASK;

    out->entity_id.lo = x_hash | (y_lo << LAPLACE_X_HASH_BITS);
    out->entity_id.hi = y_hi   | (z_hash << LAPLACE_Y_HI_BITS);

    out->ordinal    = (uint16_t)(slot_m & 0xFFFFULL);
    out->run_length = (uint16_t)((slot_m >> 16) & 0xFFFFULL);

    const uint64_t z_flags = (slot_z >> LAPLACE_Z_HASH_BITS) & LAPLACE_FLAGS_Z_MASK;
    const uint64_t m_flags = (slot_m >> 32) & LAPLACE_FLAGS_M_MASK;
    out->flags = z_flags | (m_flags << LAPLACE_FLAGS_Z_BITS);
}
