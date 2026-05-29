#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "laplace/synthesis/qk_pairs_threshold.h"          /* all-pairs kernel */
#include "laplace/synthesis/qk_pairs_threshold_pruned.h"    /* pruned kernel    */
#include "laplace/synthesis/qk_project_cached.h"            /* project + score  */

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/global_control.h>
#endif

namespace {

uint64_t bits_of(double d) {
    uint64_t u;
    std::memcpy(&u, &d, sizeof(u));
    return u;
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

/* All-pairs reference for ONE head slice (Wq + head, Wk + kv_head). */
std::vector<qk_pair_f64_t> all_pairs_head(
    const std::vector<float>& E, size_t vocab, size_t d_model,
    const float* Wq_head, const float* Wk_head, size_t head_dim,
    double floor, size_t q0, size_t q1) {
    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold(E.data(), vocab, d_model, Wq_head, Wk_head,
                                              head_dim, floor, q0, q1, out.data(), out.size(), &ov);
    EXPECT_GE(n, 0);
    EXPECT_EQ(ov, 0);
    return std::vector<qk_pair_f64_t>(out.begin(), out.begin() + (n < 0 ? 0 : n));
}

std::vector<qk_pair_f64_t> pruned_head(
    const std::vector<float>& E, size_t vocab, size_t d_model,
    const float* Wq_head, const float* Wk_head, size_t head_dim,
    double floor, size_t q0, size_t q1) {
    std::vector<qk_pair_f64_t> out(vocab * vocab);
    int ov = -1;
    long n = compute_qk_pairs_above_threshold_pruned(E.data(), vocab, d_model, Wq_head, Wk_head,
                                                     head_dim, floor, q0, q1, out.data(), out.size(), &ov);
    EXPECT_GE(n, 0);
    EXPECT_EQ(ov, 0);
    return std::vector<qk_pair_f64_t>(out.begin(), out.begin() + (n < 0 ? 0 : n));
}

} /* namespace */

/* ── 1. Project-once + score-from-cache is BIT-IDENTICAL to BOTH the all-pairs
 * kernel AND the pruned kernel, across multiple GQA heads and several floors
 * (including floor == 0). This is the correctness gate. ───────────────────── */
TEST(QkProjectCached, ParityWithAllPairsAndPrunedAcrossHeads) {
    const size_t vocab = 300, d_model = 32, head_dim = 16;
    const size_t n_heads = 8, n_kv = 2;          /* GQA: queriesPerKv = 4 */
    const size_t queries_per_kv = n_heads / n_kv;

    auto E  = rand_f32(vocab * d_model, 0xC0FFEEu);
    auto Wq = rand_f32(n_heads * head_dim * d_model, 0xDECAFu);
    auto Wk = rand_f32(n_kv    * head_dim * d_model, 0xFACADEu);

    std::vector<double> q_cache(vocab * n_heads * head_dim);
    std::vector<double> k_cache(vocab * n_kv    * head_dim);
    ASSERT_EQ(project_qk_layer(E.data(), vocab, d_model,
                               Wq.data(), n_heads, Wk.data(), n_kv, head_dim,
                               q_cache.data(), k_cache.data()), 0);

    const double floors[] = {0.0, 1.0, 1.5, 3.0};
    bool saw_nonempty = false, saw_nonfull = false;
    for (double floor : floors) {
        for (size_t head = 0; head < n_heads; ++head) {
            const size_t kv_head = head / queries_per_kv;
            const float* Wq_head = Wq.data() + head    * head_dim * d_model;
            const float* Wk_head = Wk.data() + kv_head * head_dim * d_model;

            auto ref    = all_pairs_head(E, vocab, d_model, Wq_head, Wk_head, head_dim, floor, 0, vocab);
            auto pruned = pruned_head  (E, vocab, d_model, Wq_head, Wk_head, head_dim, floor, 0, vocab);
            expect_pairs_eq(pruned, ref);  /* sanity: pruned == all-pairs */

            std::vector<qk_pair_f64_t> out(vocab * vocab);
            int ov = -1;
            long n = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                          vocab, head_dim, head, kv_head, floor, 0, vocab,
                                          out.data(), out.size(), &ov);
            ASSERT_GE(n, 0);
            ASSERT_EQ(ov, 0);
            std::vector<qk_pair_f64_t> got(out.begin(), out.begin() + n);
            expect_pairs_eq(got, ref);

            if (!ref.empty()) saw_nonempty = true;
            if (ref.size() < (size_t)vocab * vocab) saw_nonfull = true;
        }
    }
    EXPECT_TRUE(saw_nonempty) << "no head/floor admitted any pair — test is vacuous";
    EXPECT_TRUE(saw_nonfull);
}

/* ── 2. Cache path matches across thread counts and across window splits, and the
 * concatenated windows equal the pruned single-call run. ──────────────────── */
TEST(QkProjectCached, DeterministicAcrossThreadsAndWindows) {
    const size_t vocab = 200, d_model = 41, head_dim = 13;
    const size_t n_heads = 4, n_kv = 2;
    const size_t queries_per_kv = n_heads / n_kv;
    const size_t head = 3, kv_head = head / queries_per_kv;  /* kv_head = 1 */
    const double floor = 0.9;

    auto E  = rand_f32(vocab * d_model, 0xBADC0DEu);
    auto Wq = rand_f32(n_heads * head_dim * d_model, 0xFEEDBEEFu);
    auto Wk = rand_f32(n_kv    * head_dim * d_model, 0x1234567u);

    std::vector<double> q_cache(vocab * n_heads * head_dim);
    std::vector<double> k_cache(vocab * n_kv    * head_dim);
    ASSERT_EQ(project_qk_layer(E.data(), vocab, d_model,
                               Wq.data(), n_heads, Wk.data(), n_kv, head_dim,
                               q_cache.data(), k_cache.data()), 0);

    std::vector<qk_pair_f64_t> out_many(vocab * vocab);
    int ov = -1;
    long n_many = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                       vocab, head_dim, head, kv_head, floor, 0, vocab,
                                       out_many.data(), out_many.size(), &ov);
    ASSERT_GE(n_many, 0);
    ASSERT_EQ(ov, 0);
    std::vector<qk_pair_f64_t> many(out_many.begin(), out_many.begin() + n_many);

    /* Single-threaded run must match the multi-threaded run bit-for-bit. */
    std::vector<qk_pair_f64_t> out_one(vocab * vocab);
#ifdef LAPLACE_HAS_MKL
    {
        oneapi::tbb::global_control gc(
            oneapi::tbb::global_control::max_allowed_parallelism, 1);
        long n_one = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                          vocab, head_dim, head, kv_head, floor, 0, vocab,
                                          out_one.data(), out_one.size(), &ov);
        ASSERT_EQ(n_one, n_many);
    }
#else
    long n_one = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                      vocab, head_dim, head, kv_head, floor, 0, vocab,
                                      out_one.data(), out_one.size(), &ov);
    ASSERT_EQ(n_one, n_many);
#endif
    std::vector<qk_pair_f64_t> one(out_one.begin(), out_one.begin() + n_many);
    expect_pairs_eq(one, many);

    /* Windowed: [0,57)+[57,131)+[131,200) concatenated must equal the full run. */
    std::vector<qk_pair_f64_t> windowed;
    const size_t bounds[] = {0, 57, 131, 200};
    for (int w = 0; w < 3; ++w) {
        std::vector<qk_pair_f64_t> buf(vocab * vocab);
        long nn = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                       vocab, head_dim, head, kv_head, floor,
                                       bounds[w], bounds[w + 1], buf.data(), buf.size(), &ov);
        ASSERT_GE(nn, 0);
        ASSERT_EQ(ov, 0);
        windowed.insert(windowed.end(), buf.begin(), buf.begin() + nn);
    }
    expect_pairs_eq(windowed, many);

    /* And the full cache run must equal the pruned single-head kernel. */
    const float* Wq_head = Wq.data() + head    * head_dim * d_model;
    const float* Wk_head = Wk.data() + kv_head * head_dim * d_model;
    auto pruned = pruned_head(E, vocab, d_model, Wq_head, Wk_head, head_dim, floor, 0, vocab);
    expect_pairs_eq(many, pruned);
}

/* ── 3. Overflow contract: tiny out_cap => overflow=1, whole-row deterministic
 * prefix matching the head of the pruned (== all-pairs) run. ──────────────── */
TEST(QkProjectCached, OverflowWholeRowPrefix) {
    const size_t vocab = 120, d_model = 29, head_dim = 11;
    const size_t n_heads = 4, n_kv = 4;          /* MHA */
    const size_t head = 2, kv_head = 2;
    const double floor = 0.6;

    auto E  = rand_f32(vocab * d_model, 0xABCDEFu);
    auto Wq = rand_f32(n_heads * head_dim * d_model, 0x55555u);
    auto Wk = rand_f32(n_kv    * head_dim * d_model, 0xAAAAAu);

    std::vector<double> q_cache(vocab * n_heads * head_dim);
    std::vector<double> k_cache(vocab * n_kv    * head_dim);
    ASSERT_EQ(project_qk_layer(E.data(), vocab, d_model,
                               Wq.data(), n_heads, Wk.data(), n_kv, head_dim,
                               q_cache.data(), k_cache.data()), 0);

    const float* Wq_head = Wq.data() + head    * head_dim * d_model;
    const float* Wk_head = Wk.data() + kv_head * head_dim * d_model;
    auto ref = all_pairs_head(E, vocab, d_model, Wq_head, Wk_head, head_dim, floor, 0, vocab);
    const long n_full = (long)ref.size();
    ASSERT_GT(n_full, 0);

    const size_t cap = (size_t)n_full / 4 + 1;
    std::vector<qk_pair_f64_t> small(cap);
    int ov = -1;
    long n_small = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                        vocab, head_dim, head, kv_head, floor, 0, vocab,
                                        small.data(), cap, &ov);
    ASSERT_EQ(ov, 1) << "expected overflow at cap=" << cap << " vs full=" << n_full;
    ASSERT_GE(n_small, 0);
    ASSERT_LE((size_t)n_small, cap);

    std::vector<qk_pair_f64_t> got(small.begin(), small.begin() + n_small);
    std::vector<qk_pair_f64_t> head_ref(ref.begin(), ref.begin() + n_small);
    expect_pairs_eq(got, head_ref);
    if ((size_t)n_small < (size_t)n_full) {
        EXPECT_NE(ref[(size_t)n_small - 1].query_idx, ref[(size_t)n_small].query_idx)
            << "prefix cut mid-row";
    }

    long n_zero = score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv,
                                       vocab, head_dim, head, kv_head, floor, 0, vocab,
                                       small.data(), 0, &ov);
    EXPECT_EQ(n_zero, 0);
    EXPECT_EQ(ov, 1);
}

/* ── 4. Bad-args contract for both entry points. ──────────────────────────── */
TEST(QkProjectCached, BadArgsRejected) {
    const size_t vocab = 4, d_model = 3, head_dim = 2, n_heads = 2, n_kv = 1;
    auto E  = rand_f32(vocab * d_model, 1);
    auto Wq = rand_f32(n_heads * head_dim * d_model, 2);
    auto Wk = rand_f32(n_kv    * head_dim * d_model, 3);
    std::vector<double> q_cache(vocab * n_heads * head_dim);
    std::vector<double> k_cache(vocab * n_kv    * head_dim);

    EXPECT_EQ(project_qk_layer(nullptr, vocab, d_model, Wq.data(), n_heads, Wk.data(), n_kv, head_dim, q_cache.data(), k_cache.data()), -1);
    EXPECT_EQ(project_qk_layer(E.data(), 0, d_model, Wq.data(), n_heads, Wk.data(), n_kv, head_dim, q_cache.data(), k_cache.data()), -1);
    EXPECT_EQ(project_qk_layer(E.data(), vocab, 0, Wq.data(), n_heads, Wk.data(), n_kv, head_dim, q_cache.data(), k_cache.data()), -1);
    EXPECT_EQ(project_qk_layer(E.data(), vocab, d_model, nullptr, n_heads, Wk.data(), n_kv, head_dim, q_cache.data(), k_cache.data()), -1);
    EXPECT_EQ(project_qk_layer(E.data(), vocab, d_model, Wq.data(), 0, Wk.data(), n_kv, head_dim, q_cache.data(), k_cache.data()), -1);
    EXPECT_EQ(project_qk_layer(E.data(), vocab, d_model, Wq.data(), n_heads, Wk.data(), n_kv, head_dim, nullptr, k_cache.data()), -1);

    ASSERT_EQ(project_qk_layer(E.data(), vocab, d_model, Wq.data(), n_heads, Wk.data(), n_kv, head_dim, q_cache.data(), k_cache.data()), 0);

    std::vector<qk_pair_f64_t> out(64);
    int ov = 0;
    EXPECT_EQ(score_qk_head_cached(nullptr, n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, nullptr, n_kv, vocab, head_dim, 0, 0, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, 0.0, 0, vocab, nullptr, out.size(), &ov), -1);
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, 0.0, 0, vocab, out.data(), out.size(), nullptr), -1);
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, 0, head_dim, 0, 0, 0.0, 0, 0, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, 0, 0, 0, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, n_heads, 0, 0.0, 0, vocab, out.data(), out.size(), &ov), -1); /* head out of range */
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, n_kv, 0.0, 0, vocab, out.data(), out.size(), &ov), -1);   /* kv_head out of range */
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, 0.0, 3, 2, out.data(), out.size(), &ov), -1);          /* q0 > q1 */
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, 0.0, 0, vocab + 1, out.data(), out.size(), &ov), -1);  /* q1 > vocab */
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, -1.0, 0, vocab, out.data(), out.size(), &ov), -1);     /* floor < 0 */
    EXPECT_EQ(score_qk_head_cached(q_cache.data(), n_heads, k_cache.data(), n_kv, vocab, head_dim, 0, 0, std::nan(""), 0, vocab, out.data(), out.size(), &ov), -1);
}
