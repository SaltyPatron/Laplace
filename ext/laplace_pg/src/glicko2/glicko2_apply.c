/*
 * glicko2_apply.c — paper-faithful Glicko-2 single-period update.
 *
 * Reference: Mark E. Glickman, "Example of the Glicko-2 system", 2013.
 * Symbol names match the paper:
 *   g(phi)        — eq. (1)
 *   E(mu, mu_j, phi_j) — eq. (2)
 *   v             — eq. (3)
 *   delta         — eq. (4)
 *   new_sigma     — §A iterative algorithm (Illinois method)
 *   new_phi       — §3 eq. (8)
 *   new_mu        — §3 eq. (9)
 */

#include "laplace_pg/glicko2.h"

#include <math.h>

#ifndef M_PI
#  define M_PI 3.14159265358979323846
#endif

static double g_func(double phi)
{
    return 1.0 / sqrt(1.0 + 3.0 * phi * phi / (M_PI * M_PI));
}

static double E_func(double mu, double mu_j, double phi_j)
{
    return 1.0 / (1.0 + exp(-g_func(phi_j) * (mu - mu_j)));
}

/* §A — iterative volatility update via Illinois method. */
static double update_volatility(double sigma, double phi, double v, double delta, double tau)
{
    const double a       = log(sigma * sigma);
    const double tau2    = tau * tau;
    const double phi2    = phi * phi;
    const double delta2  = delta * delta;
    const double epsilon = 1e-6;

    /* f(x) per Glickman 2013 §A: */
#define LAPLACE_GLICKO_F(x_) ( \
        (exp((x_)) * (delta2 - phi2 - v - exp((x_)))) / \
        (2.0 * (phi2 + v + exp((x_))) * (phi2 + v + exp((x_)))) - \
        ((x_) - a) / tau2 \
    )

    double A = a;
    double B;
    if (delta2 > phi2 + v) {
        B = log(delta2 - phi2 - v);
    } else {
        int k = 1;
        while (LAPLACE_GLICKO_F(a - (double) k * tau) < 0.0) {
            ++k;
            if (k > 100) {
                break;
            }
        }
        B = a - (double) k * tau;
    }

    double fA = LAPLACE_GLICKO_F(A);
    double fB = LAPLACE_GLICKO_F(B);

    int iter = 0;
    while (fabs(B - A) > epsilon && iter < 100) {
        const double C  = A + (A - B) * fA / (fB - fA);
        const double fC = LAPLACE_GLICKO_F(C);
        if (fC * fB <= 0.0) {
            A  = B;
            fA = fB;
        } else {
            fA = fA / 2.0;
        }
        B  = C;
        fB = fC;
        ++iter;
    }
#undef LAPLACE_GLICKO_F

    return exp(A / 2.0);
}

void laplace_glicko2_period_decay(const laplace_glicko2_state_t *in,
                                  laplace_glicko2_state_t       *out)
{
    out->mu    = in->mu;
    out->phi   = sqrt(in->phi * in->phi + in->sigma * in->sigma);
    out->sigma = in->sigma;
    out->games = in->games;
}

void laplace_glicko2_apply(const laplace_glicko2_state_t        *in,
                           const laplace_glicko2_observation_t  *obs,
                           size_t                                n_observations,
                           double                                tau,
                           laplace_glicko2_state_t              *out)
{
    if (n_observations == 0 || obs == NULL) {
        laplace_glicko2_period_decay(in, out);
        return;
    }

    /* Step 3 — compute v and delta_sum. */
    double v_inv      = 0.0;
    double delta_sum  = 0.0;
    double total_w    = 0.0;
    for (size_t j = 0; j < n_observations; ++j) {
        const double w     = obs[j].weight > 0.0 ? obs[j].weight : 1.0;
        const double gphij = g_func(obs[j].opponent_phi);
        const double Ej    = E_func(in->mu, obs[j].opponent_mu, obs[j].opponent_phi);
        v_inv      += w * gphij * gphij * Ej * (1.0 - Ej);
        delta_sum  += w * gphij * (obs[j].score - Ej);
        total_w    += w;
    }

    if (v_inv <= 0.0) {
        laplace_glicko2_period_decay(in, out);
        return;
    }
    const double v     = 1.0 / v_inv;
    const double delta = v * delta_sum;

    /* Step 5 — new sigma via Illinois iteration. */
    const double new_sigma = update_volatility(in->sigma, in->phi, v, delta, tau);

    /* Step 6 — phi_star, then phi'. */
    const double phi_star = sqrt(in->phi * in->phi + new_sigma * new_sigma);
    const double new_phi  = 1.0 / sqrt(1.0 / (phi_star * phi_star) + 1.0 / v);

    /* Step 7 — new mu. */
    const double new_mu = in->mu + new_phi * new_phi * delta_sum;

    out->mu    = new_mu;
    out->phi   = new_phi;
    out->sigma = new_sigma;
    out->games = in->games + (int) n_observations;
    (void) total_w;
}

double laplace_glicko2_to_rating(double mu)            { return 173.7178 * mu + 1500.0; }
double laplace_glicko2_to_rating_dev(double phi)       { return 173.7178 * phi; }
double laplace_glicko2_from_rating(double rating)      { return (rating - 1500.0) / 173.7178; }
double laplace_glicko2_from_rating_dev(double r_dev)   { return r_dev / 173.7178; }
