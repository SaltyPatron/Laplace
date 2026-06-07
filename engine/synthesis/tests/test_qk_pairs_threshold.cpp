#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "laplace/synthesis/qk_pairs_threshold.h"

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/global_control.h>
#endif

namespace {

uint64_t bits_of(double d) {
    uint64_t u;
    std::memcpy(&u, &d, sizeof(u));
    return u;
}

void neumaier_add(double& sum, double& c, double term) {
    const double t = sum + term;
    if (std::fabs(sum) >= std::fabs(term))
        c += (sum - t) + term;
    else
        c += (term - t) + sum;
    sum = t;
}

std::vector<qk_pair_f64_t> reference(
    const std::vector<float>& E, size_t vocab, size_t d_model,
    const std::vector<float>& Wq, const std::vector<float>& Wk, size_t head_dim,
    double floor, size_t q0, size_t q1) {

    auto project = [&](size_t tok, const std::vector<float>& W, std::vector<double>& proj) {
        for (size_t d = 0; d < head_dim; ++d) {
            double sum = 0.0, c = 0.0;
            for (size_t m = 0; m < d_model; ++m)
                neumaier_add(sum, c, (double)E[tok * d_model + m] * (double)W[d * d_model + m]);
            proj[d] = sum + c;
        }
    };

    std::vector<qk_pair_f64_t> out;
    std::vector<double> q_t(head_dim), k_s(head_dim);
    for (size_t t = q0; t < q1; ++t) {
        project(t, Wq, q_t);
        for (size_t s = 0; s < vocab; ++s) {
            project(s, Wk, k_s);
            double sum = 0.0, c = 0.0;
            for (size_t d = 0; d < head_dim; ++d) neumaier_add(sum, c, q_t[d] * k_s[d]);
            const double sc = sum + c;
            if (std::fabs(sc) > floor)
                out.push_back({(uint32_t)t, (uint32_t)s, sc});
        }
    }
    return out;
}

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

}

TEST(QkPairsThreshold, BadArgsRejected) {
    std::vector<float> E = rand_f32(4 * 3, 1);
    std::vector<float> Wq = rand_f32(2 * 3, 2);
    std::vector<float> Wk = rand_f32(2 * 3, 3);
    std::vector<qk_pair_f64_t> out(64);
    int ov = 0;
    const size_t vocab = 4, d_model = 3, head_dim = 2;

    EXPECT_EQ(compute_qk_pairs_above_threshold(nullptr, vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, nullptr, Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), nullptr, head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, nullptr, out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), nullptr), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), 0, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, 0, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, 0, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), 0, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 3, 2, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, 0.0, 0, vocab + 1, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, -1.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(), head_dim, std::nan(""), 0, vocab, out.data(), out.size(), &ov), -1);
}

TEST(QkPairsThreshold, ThresholdBehaviorKnownScores) {
    const size_t vocab = 3, d_model = 2, head_dim = 2;
    std::vector<float> E = {2,0, 0,3, 1,0};
    std::vector<float> I = {1,0, 0,1};
    std::vector<qk_pair_f64_t> out(64);
    int ov = -1;

    long n = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, I.data(), I.data(),
                                              head_dim, 1.5, 0, vocab, out.data(), out.size(), &ov);
    ASSERT_EQ(ov, 0);
    ASSERT_EQ(n, 4);
    std::vector<qk_pair_f64_t> got(out.begin(), out.begin() + n);
    std::vector<qk_pair_f64_t> want = {
        {0, 0, 4.0}, {0, 2, 2.0}, {1, 1, 9.0}, {2, 0, 2.0},
    };
    expect_pairs_eq(got, want);

    n = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, I.data(), I.data(),
                                         head_dim, 2.0, 0, vocab, out.data(), out.size(), &ov);
    ASSERT_EQ(ov, 0);
    ASSERT_EQ(n, 2);
    std::vector<qk_pair_f64_t> got2(out.begin(), out.begin() + n);
    std::vector<qk_pair_f64_t> want2 = {{0, 0, 4.0}, {1, 1, 9.0}};
    expect_pairs_eq(got2, want2);
}

TEST(QkPairsThreshold, BitwiseMatchesSerialReference) {
    const size_t vocab = 137, d_model = 53, head_dim = 17;
    auto E  = rand_f32(vocab * d_model, 0xC0FFEEu);
    auto Wq = rand_f32(head_dim * d_model, 0xDECAFu);
    auto Wk = rand_f32(head_dim * d_model, 0xFACADEu);
    const double floor = 0.5;

    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                              head_dim, floor, 0, vocab, out.data(), out.size(), &ov);
    ASSERT_GE(n, 0);
    ASSERT_EQ(ov, 0);

    auto ref = reference(E, vocab, d_model, Wq, Wk, head_dim, floor, 0, vocab);
    std::vector<qk_pair_f64_t> got(out.begin(), out.begin() + n);
    expect_pairs_eq(got, ref);
}

TEST(QkPairsThreshold, DeterministicAcrossThreadCountsAndWindows) {
    const size_t vocab = 200, d_model = 41, head_dim = 13;
    auto E  = rand_f32(vocab * d_model, 0xBADC0DEu);
    auto Wq = rand_f32(head_dim * d_model, 0xFEEDBEEFu);
    auto Wk = rand_f32(head_dim * d_model, 0x1234567u);
    const double floor = 0.3;

    std::vector<qk_pair_f64_t> out_many(vocab * vocab);
    int ov = -1;
    long n_many = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                   head_dim, floor, 0, vocab, out_many.data(), out_many.size(), &ov);
    ASSERT_GE(n_many, 0);
    ASSERT_EQ(ov, 0);
    std::vector<qk_pair_f64_t> many(out_many.begin(), out_many.begin() + n_many);

    std::vector<qk_pair_f64_t> out_one(vocab * vocab);
#ifdef LAPLACE_HAS_MKL
    {
        oneapi::tbb::global_control gc(
            oneapi::tbb::global_control::max_allowed_parallelism, 1);
        long n_one = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                      head_dim, floor, 0, vocab, out_one.data(), out_one.size(), &ov);
        ASSERT_EQ(n_one, n_many);
    }
#else
    long n_one = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                  head_dim, floor, 0, vocab, out_one.data(), out_one.size(), &ov);
    ASSERT_EQ(n_one, n_many);
#endif
    std::vector<qk_pair_f64_t> one(out_one.begin(), out_one.begin() + n_many);
    expect_pairs_eq(one, many);

    std::vector<qk_pair_f64_t> windowed;
    const size_t bounds[] = {0, 57, 131, 200};
    for (int w = 0; w < 3; ++w) {
        std::vector<qk_pair_f64_t> buf(vocab * vocab);
        long nn = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                   head_dim, floor, bounds[w], bounds[w + 1],
                                                   buf.data(), buf.size(), &ov);
        ASSERT_GE(nn, 0);
        ASSERT_EQ(ov, 0);
        windowed.insert(windowed.end(), buf.begin(), buf.begin() + nn);
    }
    expect_pairs_eq(windowed, many);
}

TEST(QkPairsThreshold, OverflowSignalsAndPrefixIsDeterministic) {
    const size_t vocab = 80, d_model = 29, head_dim = 11;
    auto E  = rand_f32(vocab * d_model, 0xABCDEFu);
    auto Wq = rand_f32(head_dim * d_model, 0x55555u);
    auto Wk = rand_f32(head_dim * d_model, 0xAAAAAu);
    const double floor = 0.2;

    std::vector<qk_pair_f64_t> full(vocab * vocab);
    int ov = -1;
    long n_full = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                   head_dim, floor, 0, vocab, full.data(), full.size(), &ov);
    ASSERT_GT(n_full, 0);
    ASSERT_EQ(ov, 0);

    const size_t cap = (size_t)n_full / 4 + 1;
    std::vector<qk_pair_f64_t> small(cap);
    long n_small = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                    head_dim, floor, 0, vocab, small.data(), cap, &ov);
    ASSERT_EQ(ov, 1) << "expected overflow at cap=" << cap << " vs full=" << n_full;
    ASSERT_GE(n_small, 0);
    ASSERT_LE((size_t)n_small, cap);

    std::vector<qk_pair_f64_t> got(small.begin(), small.begin() + n_small);
    std::vector<qk_pair_f64_t> head(full.begin(), full.begin() + n_small);
    expect_pairs_eq(got, head);
    if ((size_t)n_small < (size_t)n_full) {
        EXPECT_NE(full[(size_t)n_small - 1].query_idx, full[(size_t)n_small].query_idx)
            << "prefix cut mid-row";
    }

    long n_zero = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq.data(), Wk.data(),
                                                   head_dim, floor, 0, vocab, small.data(), 0, &ov);
    EXPECT_EQ(n_zero, 0);
    EXPECT_EQ(ov, 1);
}
