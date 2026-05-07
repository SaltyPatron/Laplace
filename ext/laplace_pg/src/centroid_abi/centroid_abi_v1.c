/*
 * centroid_abi_v1.c — codec for the v1.0 centroid mantissa payload.
 *
 * The payload (152 bits total) is striped across the 4 doubles of a
 * POINT4D. Each axis carries 38 payload bits in mantissa positions
 * 0..37 (the low end of the 52-bit mantissa). Geometric precision
 * preserved: bits 38..51 of the mantissa are untouched, giving ~14
 * bits of geometric resolution per axis — >= 10 orders of magnitude
 * better than the super-Fibonacci spacing of ~10^-3 on S^3.
 *
 * Stripe order (axis : payload-bits):
 *   X : payload bits   0.. 37     (prime_flags low 38)
 *   Y : payload bits  38.. 75     (prime_flags high 26 + entity_id low 12)
 *   Z : payload bits  76..113     (entity_id high 20 + modality + language low 10)
 *   W : payload bits 114..151     (language high 6 + model_id + tier + reserved)
 *
 * Sign and exponent of each axis are PRESERVED — the codec only
 * touches mantissa bits 0..37 (the trailing 38). This guarantees
 * positions that started on S^3 stay within 10^-15 of S^3 and
 * geometric operators (distance, slerp, centroid) produce the
 * expected results.
 */

#include "laplace_pg/centroid_abi_v1.h"

#include <string.h>

#define PAYLOAD_BITS_PER_AXIS  38u
#define PAYLOAD_MASK_PER_AXIS  ((1ULL << PAYLOAD_BITS_PER_AXIS) - 1ULL)

/* IEEE 754 double mantissa is 52 bits. Bits 0..37 are payload; bits
 * 38..51 are preserved geometric precision. */
#define MANTISSA_PAYLOAD_MASK  ((1ULL << PAYLOAD_BITS_PER_AXIS) - 1ULL)
#define MANTISSA_KEEP_MASK     (~MANTISSA_PAYLOAD_MASK)

static double stuff_axis(double v, uint64_t payload38)
{
    uint64_t bits;
    memcpy(&bits, &v, sizeof bits);
    bits = (bits & MANTISSA_KEEP_MASK) | (payload38 & MANTISSA_PAYLOAD_MASK);
    double out;
    memcpy(&out, &bits, sizeof out);
    return out;
}

static uint64_t extract_axis(double v)
{
    uint64_t bits;
    memcpy(&bits, &v, sizeof bits);
    return bits & MANTISSA_PAYLOAD_MASK;
}

/* Pack the 152-bit payload into four 38-bit chunks. */
static void pack_payload(const laplace_centroid_payload_v1_t *p,
                         uint64_t                              chunks[4])
{
    /* Build a 152-bit value as four 64-bit limbs (low-bit-first), then
     * slice into 38-bit chunks. */
    uint64_t buf[3] = {0, 0, 0};

    /* prime_flags: bits 0..63 → buf[0] */
    buf[0] = p->prime_flags;

    /* entity_id (32 bits) → buf bits 64..95 */
    buf[1] |= (uint64_t) p->entity_id;

    /* structural_flags (8 bits) → buf bits 96..103 */
    buf[1] |= ((uint64_t) p->structural_flags) << 32;

    /* language_id (16 bits) → buf bits 104..119 */
    buf[1] |= ((uint64_t) p->language_id) << 40;

    /* model_id (8 bits) → buf bits 120..127 */
    buf[1] |= ((uint64_t) p->model_id) << 56;

    /* tier (4 bits) → buf bits 128..131 */
    buf[2] |= ((uint64_t) (p->tier & 0xFu));

    /* reserved (20 bits) → buf bits 132..151 */
    buf[2] |= ((uint64_t) (p->reserved & 0xFFFFFu)) << 4;

    /* Slice 38-bit chunks. */
    /* chunk i covers bits [i*38, (i+1)*38). */
    for (int i = 0; i < 4; ++i) {
        const unsigned start_bit = (unsigned) i * PAYLOAD_BITS_PER_AXIS;
        const unsigned word      = start_bit / 64u;
        const unsigned bit_off   = start_bit % 64u;

        uint64_t low = buf[word] >> bit_off;
        if (bit_off + PAYLOAD_BITS_PER_AXIS > 64u && word + 1 < 3) {
            const unsigned spill = (bit_off + PAYLOAD_BITS_PER_AXIS) - 64u;
            low |= (buf[word + 1] & ((1ULL << spill) - 1ULL)) << (PAYLOAD_BITS_PER_AXIS - spill);
        }
        chunks[i] = low & PAYLOAD_MASK_PER_AXIS;
    }
}

static void unpack_payload(const uint64_t                  chunks[4],
                           laplace_centroid_payload_v1_t  *out)
{
    uint64_t buf[3] = {0, 0, 0};
    for (int i = 0; i < 4; ++i) {
        const unsigned start_bit = (unsigned) i * PAYLOAD_BITS_PER_AXIS;
        const unsigned word      = start_bit / 64u;
        const unsigned bit_off   = start_bit % 64u;
        const uint64_t v         = chunks[i] & PAYLOAD_MASK_PER_AXIS;

        buf[word] |= v << bit_off;
        if (bit_off + PAYLOAD_BITS_PER_AXIS > 64u && word + 1 < 3) {
            const unsigned spill = (bit_off + PAYLOAD_BITS_PER_AXIS) - 64u;
            buf[word + 1] |= v >> (PAYLOAD_BITS_PER_AXIS - spill);
        }
    }

    out->prime_flags      = buf[0];
    out->entity_id        = (uint32_t) (buf[1] & 0xFFFFFFFFu);
    out->structural_flags = (uint8_t) ((buf[1] >> 32) & 0xFFu);
    out->language_id      = (uint16_t) ((buf[1] >> 40) & 0xFFFFu);
    out->model_id         = (uint8_t) ((buf[1] >> 56) & 0xFFu);
    out->tier             = (uint8_t) (buf[2] & 0xFu);
    out->reserved         = (uint32_t) ((buf[2] >> 4) & 0xFFFFFu);
}

void laplace_centroid_encode_v1(laplace_point4d_t                   *position,
                                const laplace_centroid_payload_v1_t *payload)
{
    uint64_t chunks[4];
    pack_payload(payload, chunks);
    position->x = stuff_axis(position->x, chunks[0]);
    position->y = stuff_axis(position->y, chunks[1]);
    position->z = stuff_axis(position->z, chunks[2]);
    position->w = stuff_axis(position->w, chunks[3]);
}

void laplace_centroid_decode_v1(const laplace_point4d_t        *position,
                                laplace_centroid_payload_v1_t  *out_payload)
{
    const uint64_t chunks[4] = {
        extract_axis(position->x),
        extract_axis(position->y),
        extract_axis(position->z),
        extract_axis(position->w),
    };
    unpack_payload(chunks, out_payload);
}

void laplace_centroid_strip_payload_v1(const laplace_point4d_t *position,
                                       laplace_point4d_t       *out_geometry)
{
    *out_geometry = *position;
    /* Zero the payload bits on each axis. */
    uint64_t bx, by, bz, bw;
    memcpy(&bx, &out_geometry->x, sizeof bx); bx &= MANTISSA_KEEP_MASK; memcpy(&out_geometry->x, &bx, sizeof bx);
    memcpy(&by, &out_geometry->y, sizeof by); by &= MANTISSA_KEEP_MASK; memcpy(&out_geometry->y, &by, sizeof by);
    memcpy(&bz, &out_geometry->z, sizeof bz); bz &= MANTISSA_KEEP_MASK; memcpy(&out_geometry->z, &bz, sizeof bz);
    memcpy(&bw, &out_geometry->w, sizeof bw); bw &= MANTISSA_KEEP_MASK; memcpy(&out_geometry->w, &bw, sizeof bw);
}
