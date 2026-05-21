/*
 * Laplace PostgreSQL extension — entry point.
 *
 * Currently exposes only the version via laplace_version().
 * Real custom 4D-aware functions (laplace_distance_4d, laplace_hilbert_encode,
 * laplace_mantissa_pack, laplace_frechet_4d, laplace_glicko2_*, laplace_astar_path,
 * etc.) populate in subsequent chunks per DESIGN.md Section III.
 *
 * Per RULES.md R6: this is a THIN wrapper. Real entity math lives in the engine.
 * Per RULES.md R14: extern "C" boundary; PG_TRY/PG_CATCH around anything fallible;
 * bounds-checked deserialization (zero EOF reads).
 */

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"

PG_MODULE_MAGIC;

/* Module-load hook (PG calls this when the .so is loaded). */
void _PG_init(void);
void
_PG_init(void)
{
    /* No-op for skeleton. Future chunks register hooks, shmem, etc. */
}

PG_FUNCTION_INFO_V1(pg_laplace_version);

Datum
pg_laplace_version(PG_FUNCTION_ARGS)
{
    PG_RETURN_TEXT_P(cstring_to_text("0.1.0"));
}
