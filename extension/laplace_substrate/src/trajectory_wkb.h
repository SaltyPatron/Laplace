#ifndef LAPLACE_TRAJECTORY_WKB_H
#define LAPLACE_TRAJECTORY_WKB_H

#include "postgres.h"

/* Shared ISO-WKB framing for native trajectory readers. Vertex payloads are
 * copied into aligned doubles by the caller before canonical mantissa decode. */
static inline const unsigned char *
laplace_trajectory_wkb_points(const bytea *wkb, uint32 *count)
{
    const unsigned char *base = (const unsigned char *) VARDATA_ANY(wkb);
    Size len = VARSIZE_ANY_EXHDR(wkb);
    Size offset;
    uint32 type;
    if (len < 5 || base[0] != 1)
        ereport(ERROR, (errmsg("trajectory: expected little-endian ISO WKB")));
    memcpy(&type, base + 1, sizeof(type));
    if (type == 3001u) {
        *count = 1;
        offset = 5;
    } else if (type == 3002u) {
        if (len < 9)
            ereport(ERROR, (errmsg("trajectory: truncated LINESTRING ZM WKB")));
        memcpy(count, base + 5, sizeof(*count));
        offset = 9;
    } else {
        ereport(ERROR, (errmsg("trajectory: expected POINT/LINESTRING ZM, got type %u", type)));
    }
    if ((Size) *count > (len - offset) / (4 * sizeof(double)))
        ereport(ERROR, (errmsg("trajectory: truncated WKB vertex payload")));
    return base + offset;
}

#endif
