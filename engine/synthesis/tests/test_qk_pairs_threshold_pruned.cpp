#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "laplace/synthesis/qk_pairs_threshold.h"          /* all-pairs kernel */
#include "laplace/synthesis/qk_pairs_threshold_pruned.h"    /* pruned kernel    */

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/global_control.h>
#endif

namespace {

/* Bit pattern of a double for bitwise-identical comparison. */
uint64_t bits_of(double d) {
    uint64_t u;
    std::memcpy(&u, &d, sizeof(u));
    return u;
}

/* Random f32 in [-1, 1). */
std::vector<float> rand_f32(size_t n, uint32_t seed) {
    std::vector<float> v(n);
    std::mt19937 rng(seed);
    std::uniform_real_distribution<float> dist(-1.0f, 1.0f);
    for (auto& x : v) x = dist(rng);
    return v;
}

void expect_pairs_eq(const std::vector<qk_pair_f64_t>& a,
                     const std::vector<qk_pair_f64_t>& b) {
    ASSERT_EQ(a.size(), b.size());
    for (size_t i = 0; i < a.size(); ++i) {
        EXPECT_EQ(a[i].query_idx, b[i].query_idx) << "pair " << i;
        EXPECT_EQ(a[i].key_idx, b[i].key_idx)     << "pair " << i;
        EXPECT_EQ(bits_of(a[i].score), bits_of(b[i].score)) << "pair " << i;
    }
}

/* Run the all-pairs reference kernel over the full vocab and return its pairs. */
std::vector<qk_pair_f64_t> all_pairs(
    const std::vector<float>& E, size_t vocab, size_t d_model,
    const std::vector<float>& Wq, const std::vector<float>& Wk, size_t head_dim,
    double floor, size_t q0, size_t q1) {
    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                              head_dim, floor, q0, q1, out.data(), out.size(), &ov);
    EXPECT_GE(n, 0);
    EXPECT_EQ(ov, 0);
    return std::vector<qk_pair_f64_t>(out.begin(), out.begin() + (n < 0 ? 0 : n));
}

} /* namespace */

/* ── 1. PARITY (the key test): pruned kernel output is BITWISE-IDENTICAL to the
 * all-pairs kernel on the same input (same count, same (q,k) pairs in the same
 * order, same f64 score bits). ─────────────────────────────────────────────── */
TEST(QkPairsThresholdPruned, BitwiseParityWithAllPairs) {
    const size_t vocab = 300, d_model = 32, head_dim = 16;
    auto E  = rand_f32(vocab * d_model, 0xC0FFEEu);
    auto Wq = rand_f32(head_dim * d_model, 0xDECAFu);
    auto Wk = rand_f32(head_dim * d_model, 0xFACADEu);
    const double floor = 1.5;  /* admits a moderate subset of the v^2 pairs */

    auto ref = all_pairs(E, vocab, d_model, Wq, Wk, head_dim, floor, 0, vocab);
    ASSERT_GT(ref.size(), 0u) << "floor admits nothing — pick a smaller floor";
    ASSERT_LT(ref.size(), (size_t)vocab * vocab)
        << "floor admits everything — pick a larger floor";

    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                     head_dim, floor, 0, vocab, out.data(), out.size(), &ov);
    ASSERT_GE(n, 0);
    ASSERT_EQ(ov, 0);
    std::vector<qk_pair_f64_t> got(out.begin(), out.begin() + n);
    expect_pairs_eq(got, ref);
}

/* ── 2. Determinism across thread counts AND window splits (bitwise). ───────── */
TEST(QkPairsThresholdPruned, DeterministicAcrossThreadCountsAndWindows) {
    const size_t vocab = 200, d_model = 41, head_dim = 13;
    auto E  = rand_f32(vocab * d_model, 0xBADC0DEu);
    auto Wq = rand_f32(head_dim * d_model, 0xFEEDBEEFu);
    auto Wk = rand_f32(head_dim * d_model, 0x1234567u);
    const double floor = 0.9;

    std::vector<qk_pair_f64_t> out_many(vocab * vocab);
    int ov = -1;
    long n_many = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                          head_dim, floor, 0, vocab, out_many.data(), out_many.size(), &ov);
    ASSERT_GE(n_many, 0);
    ASSERT_EQ(ov, 0);
    std::vector<qk_pair_f64_t> many(out_many.begin(), out_many.begin() + n_many);

    /* Single-threaded run must match the multi-threaded run bit-for-bit. */
    std::vector<qk_pair_f64_t> out_one(vocab * vocab);
#ifdef LAPLACE_HAS_MKL
    {
        oneapi::tbb::global_control gc(
            oneapi::tbb::global_control::max_allowed_parallelism, 1);
        long n_one = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                             head_dim, floor, 0, vocab, out_one.data(), out_one.size(), &ov);
        ASSERT_EQ(n_one, n_many);
    }
#else
    long n_one = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                         head_dim, floor, 0, vocab, out_one.data(), out_one.size(), &ov);
    ASSERT_EQ(n_one, n_many);
#endif
    std::vector<qk_pair_f64_t> one(out_one.begin(), out_one.begin() + n_many);
    expect_pairs_eq(one, many);

    /* Windowed: [0,57)+[57,131)+[131,200) concatenated must equal the full run. */
    std::vector<qk_pair_f64_t> windowed;
    const size_t bounds[] = {0, 57, 131, 200};
    for (int w = 0; w < 3; ++w) {
        std::vector<qk_pair_f64_t> buf(vocab * vocab);
        long nn = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                          head_dim, floor, bounds[w], bounds[w + 1],
                                                          buf.data(), buf.size(), &ov);
        ASSERT_GE(nn, 0);
        ASSERT_EQ(ov, 0);
        windowed.insert(windowed.end(), buf.begin(), buf.begin() + nn);
    }
    expect_pairs_eq(windowed, many);

    /* And the full run must equal the all-pairs kernel (parity at this floor). */
    auto ref = all_pairs(E, vocab, d_model, Wq, Wk, head_dim, floor, 0, vocab);
    expect_pairs_eq(many, ref);
}

/* ── 3. Spread norms so pruning actually prunes most keys — still bit-identical
 * to the all-pairs kernel. Key rows are scaled by geometrically decaying factors
 * so the vast majority have tiny norm and fall below every per-query cutoff,
 * yet the survivor set must be exactly the all-pairs survivor set. ──────────── */
TEST(QkPairsThresholdPruned, HeavyPruningSpreadNormsMatchesAllPairs) {
    const size_t vocab = 400, d_model = 24, head_dim = 12;
    auto E  = rand_f32(vocab * d_model, 0x5EED01u);
    auto Wq = rand_f32(head_dim * d_model, 0x5EED02u);
    auto Wk = rand_f32(head_dim * d_model, 0x5EED03u);

    /* Scale each embedding row so norms span many orders of magnitude: row r
     * scaled by ~ 0.97^r. A handful of low-index rows dominate; the rest are
     * negligible and should be pruned for nearly every query. */
    for (size_t r = 0; r < vocab; ++r) {
        const float scale = std::pow(0.97f, (float)r);
        for (size_t m = 0; m < d_model; ++m) E[r * d_model + m] *= scale;
    }

    /* Floor chosen to admit a small but nonzero subset. */
    const double floor = 0.5;
    auto ref = all_pairs(E, vocab, d_model, Wq, Wk, head_dim, floor, 0, vocab);
    ASSERT_GT(ref.size(), 0u);
    ASSERT_LT(ref.size(), (size_t)vocab * vocab / 4)
        << "pruning should leave the survivor set small here";

    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                     head_dim, floor, 0, vocab, out.data(), out.size(), &ov);
    ASSERT_GE(n, 0);
    ASSERT_EQ(ov, 0);
    std::vector<qk_pair_f64_t> got(out.begin(), out.begin() + n);
    expect_pairs_eq(got, ref);
}

/* ── 4. Bad args / overflow contract — identical to the sibling. ───────────── */
TEST(QkPairsThresholdPruned, BadArgsRejected) {
    std::vector<float> E = rand_f32(4 * 3, 1);
    std::vector<float> Wq = rand_f32(2 * 3, 2);
    std::vector<float> Wk = rand_f32(2 * 3, 3);
    std::vector<qk_pair_f64_t> out(64);
    int ov = 0;
    const size_t vocab = 4, d_model = 3, head_dim = 2;

    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(nullptr, vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, nullptr, Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), nullptr, head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, nullptr, out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), nullptr), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), 0, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, 0, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, 0, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), 0, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 3, 2, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab + 1, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, -1.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, std::nan(""), 0, vocab, out.data(), out.size(), &ov), -1);
}

/* Overflow signaling: tiny out_cap => overflow=1, whole-row deterministic prefix
 * matching the head of the full pruned run AND the all-pairs run. */
TEST(QkPairsThresholdPruned, OverflowSignalsAndPrefixIsDeterministic) {
    const size_t vocab = 120, d_model = 29, head_dim = 11;
    auto E  = rand_f32(vocab * d_model, 0xABCDEFu);
    auto Wq = rand_f32(head_dim * d_model, 0x55555u);
    auto Wk = rand_f32(head_dim * d_model, 0xAAAAAu);
    const double floor = 0.6;

    auto ref = all_pairs(E, vocab, d_model, Wq, Wk, head_dim, floor, 0, vocab);
    const long n_full = (long)ref.size();
    ASSERT_GT(n_full, 0);

    const size_t cap = (size_t)n_full / 4 + 1;
    std::vector<qk_pair_f64_t> small(cap);
    int ov = -1;
    long n_small = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                           head_dim, floor, 0, vocab, small.data(), cap, &ov);
    ASSERT_EQ(ov, 1) << "expected overflow at cap=" << cap << " vs full=" << n_full;
    ASSERT_GE(n_small, 0);
    ASSERT_LE((size_t)n_small, cap);

    std::vector<qk_pair_f64_t> got(small.begin(), small.begin() + n_small);
    std::vector<qk_pair_f64_t> head(ref.begin(), ref.begin() + n_small);
    expect_pairs_eq(got, head);
    if ((size_t)n_small < (size_t)n_full) {
        EXPECT_NE(ref[(size_t)n_small - 1].query_idx, ref[(size_t)n_small].query_idx)
            << "prefix cut mid-row";
    }

    long n_zero = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                          head_dim, floor, 0, vocab, small.data(), 0, &ov);
    EXPECT_EQ(n_zero, 0);
    EXPECT_EQ(ov, 1);
}

/* ── 5. floor == 0 admits every nonzero pair: parity with all-pairs holds even
 * with no pruning prefix shrinkage. ────────────────────────────────────────── */
TEST(QkPairsThresholdPruned, ZeroFloorParity) {
    const size_t vocab = 60, d_model = 16, head_dim = 8;
    auto E  = rand_f32(vocab * d_model, 0x0F1001u);
    auto Wq = rand_f32(head_dim * d_model, 0x0F1002u);
    auto Wk = rand_f32(head_dim * d_model, 0xBEEF01u);

    auto ref = all_pairs(E, vocab, d_model, Wq, Wk, head_dim, 0.0, 0, vocab);
    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                     head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov);
    ASSERT_GE(n, 0);
    ASSERT_EQ(ov, 0);
    std::vector<qk_pair_f64_t> got(out.begin(), out.begin() + n);
    expect_pairs_eq(got, ref);
}
