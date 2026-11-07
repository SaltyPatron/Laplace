#ifndef LAPLACE_SUBSTRATE_HIGHWAY_MASK_INTERNAL_H
#define LAPLACE_SUBSTRATE_HIGHWAY_MASK_INTERNAL_H

#include "postgres.h"
#include "laplace/core/highway_table.h"
#include "entity_mask_write.h"

#define HIGHWAY_MASK_BYTES 32
#define HASH128_BYTES 16

typedef struct highway_deposit_type
{
    unsigned char id[HASH128_BYTES];
    laplace_mask256_t mask;
} highway_deposit_type;

typedef LaplaceEntityMaskDelta highway_deposit_entity;

static inline int
deposit_id_cmp(const void *a, const void *b)
{
    return memcmp(a, b, HASH128_BYTES);
}

static inline bytea *
deposit_bytea(const void *bytes, Size len)
{
    bytea *out = (bytea *) palloc(VARHDRSZ + len);

    SET_VARSIZE(out, VARHDRSZ + len);
    memcpy(VARDATA(out), bytes, len);
    return out;
}

static inline void
deposit_read_id(Datum value, unsigned char out[HASH128_BYTES], const char *which)
{
    bytea *id = DatumGetByteaPP(value);
    Size len = VARSIZE_ANY_EXHDR(id);

    if (len != HASH128_BYTES)
        ereport(ERROR,
                (errcode(ERRCODE_STRING_DATA_LENGTH_MISMATCH),
                 errmsg("highway_mask_deposit: %s must be exactly 16 bytes (got %zu)",
                        which, (size_t) len)));
    memcpy(out, VARDATA_ANY(id), HASH128_BYTES);
}

static inline void
deposit_mask_set(laplace_mask256_t *mask, uint8_t bit)
{
    unsigned char *bytes = (unsigned char *) mask;

    bytes[bit >> 3] |= (unsigned char) (1u << (bit & 7));
}

static inline void
deposit_mask_or(laplace_mask256_t *dst, const laplace_mask256_t *src)
{
    for (int i = 0; i < 4; i++)
        dst->w[i] |= src->w[i];
}

static inline bool
deposit_mask_empty(const laplace_mask256_t *mask)
{
    return (mask->w[0] | mask->w[1] | mask->w[2] | mask->w[3]) == 0;
}

void require_highway_table(const char *fn);
bool highway_registry_available(void);

#endif
