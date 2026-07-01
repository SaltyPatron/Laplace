#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/** Process host policy for MKL/TBB thread counts. PG passes laplace_substrate.native_mkl_threads GUC. */
#define LAPLACE_RUNTIME_HOST_CLI        0
#define LAPLACE_RUNTIME_HOST_PG         1
#define LAPLACE_RUNTIME_HOST_SYNTHESIS  2

/** MKL_NUM_THREADS → TBB_NUM_THREADS → LAPLACE_NATIVE_THREADS; 0 when unset. */
int laplace_runtime_resolve_thread_count(void);

/** Single native init: CBWR, MKL threads, TBB arena policy.
 *  mkl_threads > 0 uses that count; CLI/SYNTHESIS mkl_threads < 1 reads env (returns -1 if unset);
 *  PG mkl_threads < 1 returns -1 (caller must pass GUC).
 *  Returns 0 ok, -1 config/CBWR failure, -2 MKL required but absent. */
int laplace_runtime_init(int host, int mkl_threads);

/** Host passed to the last successful laplace_runtime_init; -1 if never initialized. */
int laplace_runtime_host(void);

int laplace_dynamics_init(void);

const char* laplace_dynamics_version(void);

/** 1 when compiled with MKL (LAPLACE_HAS_MKL), else 0. */
int laplace_dynamics_has_mkl(void);

/** Configured MKL thread count from init; -1 when MKL is absent or init not run. */
int laplace_dynamics_mkl_num_threads(void);

/** After successful init; -1 when MKL is absent. */
int laplace_dynamics_cbwr_branch(void);

#ifdef __cplusplus
}
#endif
