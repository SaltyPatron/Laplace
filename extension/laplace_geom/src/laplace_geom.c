/*
 * extension/laplace_geom/src/laplace_geom.c
 *
 * Thin PG_FUNCTION_INFO_V1 wrappers for the laplace_geom extension per
 * RULES.md R6 (DB as dumb store; entity math in C/C++). Real wrappers
 * for ST_*_4d, hash128, hilbert, mantissa, opclasses land per-Chunk
 * (Chunks 1-3 + 5).
 *
 * For now: a single laplace_geom_version() function proving the
 * extension loads and the engine library links correctly.
 */

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"

#include "laplace/core/version.h"

PG_MODULE_MAGIC;

PG_FUNCTION_INFO_V1(pg_laplace_geom_version);

Datum
pg_laplace_geom_version(PG_FUNCTION_ARGS)
{
    /* Return the engine's laplace_core version string — proves the
     * extension links liblaplace_core.so + can call into it. */
    const char* v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}
