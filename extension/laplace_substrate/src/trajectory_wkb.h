#ifndef LAPLACE_TRAJECTORY_WKB_H
#define LAPLACE_TRAJECTORY_WKB_H

#include "postgres.h"

/* ISO-WKB or the geometry type's binary-send EWKB framing of a stored trajectory:
 * POINT ZM (one vertex) or LINESTRING ZM, with optional EWKB SRID,
 * four binary64 per vertex. Returns the first vertex; callers copy vertices into
 * aligned doubles before unpacking the manifest (child id, ordinal, run, flags). */
static inline const unsigned char *
laplace_trajectory_wkb_points(const bytea *wkb, uint32 *count)
{
    const unsigned char *base = (const unsigned char *) VARDATA_ANY(wkb);
    Size len = VARSIZE_ANY_EXHDR(wkb);
    Size offset;
    uint32 type;
    if (len < 5 || base[0] != 1)
        ereport(ERROR, (errmsg("trajectory: expected little-endian WKB")));
    memcpy(&type, base + 1, sizeof(type));
    offset = 5;
    if ((type & 0xc0000000u) == 0xc0000000u) {
        if (type & 0x20000000u) {
            if (len < offset + 4)
                ereport(ERROR, (errmsg("trajectory: truncated EWKB SRID")));
            offset += 4;
        }
        type = (type & 0x1fffffffu) + 3000u;
    }
    if (type == 3001u) {
        *count = 1;
    } else if (type == 3002u) {
        if (len < offset + 4)
            ereport(ERROR, (errmsg("trajectory: truncated LINESTRING ZM WKB")));
        memcpy(count, base + offset, sizeof(*count));
        offset += 4;
    } else {
        ereport(ERROR, (errmsg("trajectory: expected POINT/LINESTRING ZM, got type %u", type)));
    }
    if ((Size) *count > (len - offset) / (4 * sizeof(double)))
        ereport(ERROR, (errmsg("trajectory: truncated WKB vertex payload")));
    return base + offset;
}

#endif
