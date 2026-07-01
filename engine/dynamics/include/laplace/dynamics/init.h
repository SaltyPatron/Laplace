#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_RUNTIME_HOST_CLI        0
#define LAPLACE_RUNTIME_HOST_PG         1
#define LAPLACE_RUNTIME_HOST_SYNTHESIS  2

int laplace_runtime_resolve_thread_count(void);

int laplace_runtime_init(int host, int mkl_threads);

int laplace_runtime_host(void);

int laplace_dynamics_init(void);

const char* laplace_dynamics_version(void);

int laplace_dynamics_has_mkl(void);

int laplace_dynamics_mkl_num_threads(void);

int laplace_dynamics_cbwr_branch(void);

#ifdef __cplusplus
}
#endif
