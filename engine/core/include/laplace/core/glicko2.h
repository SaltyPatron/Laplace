#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    int64_t rating;
    int64_t rd;
    int64_t volatility;
    int64_t last_observed_at_unix_ns;
    int64_t observation_count;
} glicko2_state_t;

typedef struct {
    int64_t opponent_rating;
    int64_t opponent_rd;
    int64_t score;
} glicko2_observation_t;

#define LAPLACE_GLICKO2_DEFAULT_TAU 500000000LL

#define LAPLACE_GLICKO2_ILLINOIS_EPS 1000LL

#define LAPLACE_GLICKO2_RATING_PERIOD_NS 2592000000000000LL

void glicko2_init(glicko2_state_t* st,
                  int64_t r0,
                  int64_t rd0,
                  int64_t vol0);

void glicko2_update_period(glicko2_state_t* st,
                           const glicko2_observation_t* obs,
                           size_t n,
                           int64_t tau,
                           int64_t now_ns);

void glicko2_update(glicko2_state_t* st,
                    int64_t score,
                    int64_t source_credibility,
                    int64_t now_ns);

void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns);

int64_t glicko2_effective_mu(const glicko2_state_t* st);

int64_t laplace_fp_mul(int64_t a, int64_t b);
int64_t laplace_fp_div(int64_t a, int64_t b);
int64_t laplace_fp_sqrt(int64_t x);
int64_t laplace_fp_exp(int64_t x);
int64_t laplace_fp_log(int64_t x);

int64_t laplace_glicko2_g(int64_t phi);
int64_t laplace_glicko2_E(int64_t mu, int64_t mu_j, int64_t g_j);

typedef struct {
    int64_t mu;
    int64_t phi;
    int64_t v;
    int64_t delta;
    int64_t a_value;
    int64_t sigma_new;
    int64_t phi_star;
    int64_t phi_new;
    int64_t mu_new;
    int64_t r_new;
    int64_t rd_new;
    int      illinois_iters;
} glicko2_trace_t;

void laplace_glicko2_update_period_traced(glicko2_state_t* st,
                                           const glicko2_observation_t* obs,
                                           size_t n,
                                           int64_t tau,
                                           int64_t now_ns,
                                           glicko2_trace_t* trace);

#ifdef __cplusplus
}
#endif
