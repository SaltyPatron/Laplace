#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/* Process-startup initialization for liblaplace_dynamics.
 *
 * Per ADR 0030: locks MKL's threading layer to TBB (unified scheduler)
 * and sets MKL_CBWR mode for substrate determinism (RULES.md R7).
 *
 * Called once at C# app startup via the static constructor of the
 * Laplace.Engine.Dynamics binding. PG-side use: called from extension
 * _PG_init when the substrate extension loads. Idempotent. */
int laplace_dynamics_init(void);

/* Returns the dynamics library version string. */
const char* laplace_dynamics_version(void);

#ifdef __cplusplus
}
#endif
