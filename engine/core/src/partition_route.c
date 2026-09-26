#include "laplace/core/partition_route.h"

/* HASH_PARTITION_SEED and hash_combine64's constant, from PostgreSQL's partition
 * bound support. */
#define PG_HASH_PARTITION_SEED UINT64_C(0x7A5B22367996DCFD)
#define PG_HASH_COMBINE64 UINT64_C(0x49a0f4dd15e5a8e3)

static inline uint32_t rot(uint32_t x, int k) { return (x << k) | (x >> (32 - k)); }

#define MIX(a, b, c) \
    do { \
        a -= c; a ^= rot(c, 4);  c += b; \
        b -= a; b ^= rot(a, 6);  a += c; \
        c -= b; c ^= rot(b, 8);  b += a; \
        a -= c; a ^= rot(c, 16); c += b; \
        b -= a; b ^= rot(a, 19); a += c; \
        c -= b; c ^= rot(b, 4);  b += a; \
    } while (0)

#define FINAL(a, b, c) \
    do { \
        c ^= b; c -= rot(b, 14); \
        a ^= c; a -= rot(c, 11); \
        b ^= a; b -= rot(a, 25); \
        c ^= b; c -= rot(b, 16); \
        a ^= c; a -= rot(c, 4);  \
        b ^= a; b -= rot(a, 14); \
        c ^= b; c -= rot(b, 24); \
    } while (0)

static inline uint32_t le32(const uint8_t* k)
{
    return (uint32_t)k[0] | ((uint32_t)k[1] << 8) | ((uint32_t)k[2] << 16) | ((uint32_t)k[3] << 24);
}

/* Bytes are read as little-endian words, which is what PostgreSQL computes on the
 * little-endian hosts that store this substrate. */
uint64_t laplace_pg_hash_bytes_extended(const uint8_t* k, size_t keylen, uint64_t seed)
{
    uint32_t len = (uint32_t)keylen;
    uint32_t a, b, c;

    a = b = c = 0x9e3779b9u + len + 3923095u;
    if (seed != 0)
    {
        a += (uint32_t)(seed >> 32);
        b += (uint32_t)seed;
        MIX(a, b, c);
    }
    while (len >= 12)
    {
        a += le32(k);
        b += le32(k + 4);
        c += le32(k + 8);
        MIX(a, b, c);
        k += 12;
        len -= 12;
    }
    switch (len)
    {
        case 11: c += (uint32_t)k[10] << 24; /* fallthrough */
        case 10: c += (uint32_t)k[9] << 16;  /* fallthrough */
        case 9:  c += (uint32_t)k[8] << 8;   /* fallthrough */
        case 8:  b += (uint32_t)k[7] << 24;  /* fallthrough */
        case 7:  b += (uint32_t)k[6] << 16;  /* fallthrough */
        case 6:  b += (uint32_t)k[5] << 8;   /* fallthrough */
        case 5:  b += k[4];                  /* fallthrough */
        case 4:  a += (uint32_t)k[3] << 24;  /* fallthrough */
        case 3:  a += (uint32_t)k[2] << 16;  /* fallthrough */
        case 2:  a += (uint32_t)k[1] << 8;   /* fallthrough */
        case 1:  a += k[0];
        default: break;
    }
    FINAL(a, b, c);
    return ((uint64_t)b << 32) | c;
}

void laplace_pg_hash_partition16(const uint8_t* keys, size_t n, uint32_t modulus,
                                 uint32_t* remainders)
{
    for (size_t i = 0; i < n; i++)
    {
        uint64_t row = laplace_pg_hash_bytes_extended(keys + i * 16, 16, PG_HASH_PARTITION_SEED)
            + PG_HASH_COMBINE64;
        remainders[i] = (uint32_t)(row % modulus);
    }
}
