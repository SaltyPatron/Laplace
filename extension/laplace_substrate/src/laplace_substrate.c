/*
 * extension/laplace_substrate/src/laplace_substrate.c
 *
 * Thin PG_FUNCTION_INFO_V1 wrappers for the laplace_substrate extension
 * per RULES.md R6. Real wrappers for Glicko-2 SFUNC + FINALFUNC, cascade
 * SRFs, SP-GiST + BRIN opclass support functions land per-Chunk (Chunks
 * 2-5).
 *
 * For now: a version function proving the extension loads + links the
 * full engine stack (laplace_core + laplace_dynamics).
 */

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"

#include "laplace/core/version.h"
#include "laplace/dynamics/init.h"

PG_MODULE_MAGIC;

PG_FUNCTION_INFO_V1(pg_laplace_substrate_version);

Datum
pg_laplace_substrate_version(PG_FUNCTION_ARGS)
{
    /* Returns the core version string. Proves laplace_substrate links
     * BOTH liblaplace_core AND liblaplace_dynamics. */
    const char* v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

/*
 * _PG_init — called when the extension is loaded.
 * Per ADR 0030: laplace_dynamics_init() locks MKL threading layer to TBB
 * and sets MKL_CBWR for substrate determinism. Idempotent.
 */
void _PG_init(void);
void
_PG_init(void)
{
    (void)laplace_dynamics_init();
}
