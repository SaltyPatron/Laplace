#pragma once

#include "laplace/dynamics/bilinear_edges.h"
#include "laplace/core/attestation_engine.h"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <vector>

struct bilinear_contraction_context {
    std::size_t entity_count = 0;
    std::size_t rank = 0;
    double arena = 0.0;
    std::vector<double> left;
    std::vector<double> right;
    bool shared_factors = false;
    // Per-subject null of the circuit's own score distribution, derived once
    // from the factors (see bilinear_contraction_significant_pairs).
    bool null_ready = false;
    std::vector<double> null_mean;
    std::vector<double> null_sd;
};

namespace laplace::dynamics::detail {

constexpr std::size_t kProjectionTileRows = 4096;

inline int score_value(double value, double arena, int64_t* score_out, int16_t* outcome_out)
{
    if (!std::isfinite(value) || !score_out || !outcome_out) return -2;
    const int64_t score = arena == 0.0
        ? 500000000LL
        : (int64_t)std::llround(0.5 * (1.0 + std::tanh(value / arena)) * 1e9);
    int16_t outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;
    if (laplace_attestation_outcome_from_score_fp(score, &outcome) != 0) return -2;
    *score_out = score;
    *outcome_out = outcome;
    return 0;
}

} // namespace laplace::dynamics::detail
