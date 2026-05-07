/*
 * linestring4d_io.c — LINESTRING4D text + binary I/O.
 *
 * Wire format (text): "LINESTRING4D(x1 y1 z1 w1, x2 y2 z2 w2, ...)"
 * Wire format (binary): N (uint32 NBO) | flags (uint32 NBO) | N * 4 doubles (NBO)
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "lib/stringinfo.h"
#include "libpq/pqformat.h"

#include "laplace_pg/linestring4d_type.h"

#include <stdio.h>
#include <string.h>
#include <ctype.h>

/* Count comma-separated vertex groups inside the parens. */
static int count_vertices(const char *s)
{
    int     count   = 0;
    int     in_paren = 0;
    int     non_ws_seen = 0;
    while (*s) {
        if (*s == '(') { in_paren = 1; ++s; continue; }
        if (*s == ')') { if (non_ws_seen) { ++count; } break; }
        if (in_paren) {
            if (*s == ',') { ++count; non_ws_seen = 0; }
            else if (!isspace((unsigned char) *s)) { non_ws_seen = 1; }
        }
        ++s;
    }
    return count;
}

PG_FUNCTION_INFO_V1(linestring4d_in);
Datum linestring4d_in(PG_FUNCTION_ARGS)
{
    char *str = PG_GETARG_CSTRING(0);

    /* Skip optional "LINESTRING4D" prefix. */
    const char *cursor = str;
    while (isspace((unsigned char) *cursor)) { ++cursor; }
    if (strncasecmp(cursor, "LINESTRING4D", 12) == 0) { cursor += 12; }
    while (isspace((unsigned char) *cursor)) { ++cursor; }
    if (*cursor != '(') {
        ereport(ERROR, (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                        errmsg("invalid input syntax for type linestring4d: \"%s\"", str)));
    }

    int n = count_vertices(cursor);
    if (n < 2) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                        errmsg("linestring4d requires >= 2 vertices, got %d", n)));
    }

    size_t total = LAPLACE_LS4D_TOTAL_BYTES(n);
    laplace_linestring4d_pg_t *ls = (laplace_linestring4d_pg_t *) palloc(total);
    SET_VARSIZE(ls, total);
    ls->vertex_count = (uint32) n;
    ls->flags = 0;

    /* Parse vertex tuples. */
    const char *p = cursor + 1;
    for (int i = 0; i < n; ++i) {
        double x, y, z, w;
        int consumed = 0;
        if (sscanf(p, " %lf %lf %lf %lf%n", &x, &y, &z, &w, &consumed) != 4) {
            ereport(ERROR, (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                            errmsg("linestring4d vertex %d parse failure", i)));
        }
        ls->vertices[i * 4 + 0] = x;
        ls->vertices[i * 4 + 1] = y;
        ls->vertices[i * 4 + 2] = z;
        ls->vertices[i * 4 + 3] = w;
        p += consumed;
        while (isspace((unsigned char) *p)) { ++p; }
        if (i + 1 < n) {
            if (*p != ',') {
                ereport(ERROR, (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                                errmsg("linestring4d expected ',' after vertex %d", i)));
            }
            ++p;
        }
    }
    PG_RETURN_LINESTRING4D(ls);
}

PG_FUNCTION_INFO_V1(linestring4d_out);
Datum linestring4d_out(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    StringInfoData buf;
    initStringInfo(&buf);
    appendStringInfoString(&buf, "LINESTRING4D(");
    for (uint32 i = 0; i < ls->vertex_count; ++i) {
        if (i > 0) { appendStringInfoString(&buf, ", "); }
        appendStringInfo(&buf, "%.17g %.17g %.17g %.17g",
                         ls->vertices[i * 4 + 0],
                         ls->vertices[i * 4 + 1],
                         ls->vertices[i * 4 + 2],
                         ls->vertices[i * 4 + 3]);
    }
    appendStringInfoChar(&buf, ')');
    PG_RETURN_CSTRING(buf.data);
}

PG_FUNCTION_INFO_V1(linestring4d_send);
Datum linestring4d_send(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    StringInfoData buf;
    pq_begintypsend(&buf);
    pq_sendint32(&buf, (int32) ls->vertex_count);
    pq_sendint32(&buf, (int32) ls->flags);
    for (uint32 i = 0; i < ls->vertex_count * 4; ++i) {
        pq_sendfloat8(&buf, ls->vertices[i]);
    }
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

PG_FUNCTION_INFO_V1(linestring4d_recv);
Datum linestring4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo buf = (StringInfo) PG_GETARG_POINTER(0);
    int32 n = pq_getmsgint(buf, 4);
    if (n < 2) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                        errmsg("linestring4d requires >= 2 vertices, got %d", n)));
    }
    int32 flags = pq_getmsgint(buf, 4);
    size_t total = LAPLACE_LS4D_TOTAL_BYTES(n);
    laplace_linestring4d_pg_t *ls = (laplace_linestring4d_pg_t *) palloc(total);
    SET_VARSIZE(ls, total);
    ls->vertex_count = (uint32) n;
    ls->flags = (uint32) flags;
    for (int i = 0; i < n * 4; ++i) {
        ls->vertices[i] = pq_getmsgfloat8(buf);
    }
    PG_RETURN_LINESTRING4D(ls);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
