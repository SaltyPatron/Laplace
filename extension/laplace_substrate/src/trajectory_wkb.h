#ifndef LAPLACE_TRAJECTORY_WKB_H
#define LAPLACE_TRAJECTORY_WKB_H

#include "postgres.h"
#include "laplace/geometry.h"

/* ISO-WKB or the geometry type's binary-send EWKB framing of a stored trajectory:
 * POINT ZM (one vertex) or LINESTRING ZM, with optional EWKB SRID,
 * four binary64 per vertex. Validated and unpacked through Laplace-Native's
 * lp_ewkb_visit / lp_ewkb_vertices so both extensions share one EWKB parser. */
static inline const unsigned char *
laplace_trajectory_wkb_points(const bytea *wkb, uint32 *count)
{
    const uint8_t *base = (const uint8_t *) VARDATA_ANY(wkb);
    Size len = VARSIZE_ANY_EXHDR(wkb);
    const uint8_t *vertices = NULL;
    lp_ewkb_info info;

    if (len < 5 || base[0] != 1)
        ereport(ERROR, (errmsg("trajectory: expected little-endian WKB")));
    if (!lp_ewkb_visit(base, len, NULL, NULL, &info))
        ereport(ERROR, (errmsg("trajectory: invalid or truncated POINT/LINESTRING ZM WKB")));
    if (info.type != LP_POINT && info.type != LP_LINESTRING)
        ereport(ERROR, (errmsg("trajectory: expected POINT/LINESTRING ZM, got type %u", info.type)));
    if (info.vertices > PG_UINT32_MAX)
        ereport(ERROR, (errmsg("trajectory: vertex count exceeds uint32")));
    *count = (uint32) info.vertices;
    if (info.vertices == 0)
        return base + (info.has_srid ? 9 : 5) + (info.type == LP_LINESTRING ? 4 : 0);
    if (lp_ewkb_vertices(base, len, &vertices) != info.vertices || vertices == NULL)
        ereport(ERROR, (errmsg("trajectory: failed to resolve contiguous WKB vertex block")));
    return vertices;
}

#endif
