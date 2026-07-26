#include <gtest/gtest.h>

#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "laplace/core/absence_filter.h"
#include "laplace/core/hash128.h"

namespace {

hash128_t id_of(const std::string& s) {
    hash128_t h;
    hash128_blake3(reinterpret_cast<const uint8_t*>(s.data()), s.size(), &h);
    return h;
}

struct Filter {
    laplace_absence_filter_t f{};
    Filter(uint64_t cap, double fpr) { EXPECT_EQ(0, laplace_absence_create(&f, cap, fpr)); }
    ~Filter() { laplace_absence_destroy(&f); }
};

}  // namespace

// The whole contract. Anything added must report maybe-present, forever, with no
// exceptions — a single false NEGATIVE would let the apply path treat a row that
// exists as novel and re-COPY it.
TEST(AbsenceFilter, NeverFalseNegative) {
    Filter f(100000, 0.01);
    std::vector<hash128_t> added;
    for (int i = 0; i < 100000; ++i) {
        hash128_t h = id_of("attestation/" + std::to_string(i));
        laplace_absence_add(&f.f, &h);
        added.push_back(h);
    }
    for (const auto& h : added)
        ASSERT_TRUE(laplace_absence_maybe_present(&f.f, &h));
}

// The win: ids never added must overwhelmingly prove absent. This is what removes
// the descent, so a regression here is a silent return to 1.6M probes per batch.
TEST(AbsenceFilter, AbsentIdsAreProvenAbsent) {
    Filter f(100000, 0.01);
    for (int i = 0; i < 100000; ++i) {
        hash128_t h = id_of("present/" + std::to_string(i));
        laplace_absence_add(&f.f, &h);
    }

    int maybe = 0;
    const int probes = 100000;
    for (int i = 0; i < probes; ++i) {
        hash128_t h = id_of("absent/" + std::to_string(i));
        if (laplace_absence_maybe_present(&f.f, &h)) ++maybe;
    }
    double observed = static_cast<double>(maybe) / probes;
    // Sized for 1%; power-of-two rounding only lowers it. Allow 2x headroom.
    EXPECT_LT(observed, 0.02) << "false-positive rate " << observed;
}

// An empty filter must prove EVERYTHING absent — that is what makes a fresh
// filter useful rather than merely safe.
TEST(AbsenceFilter, EmptyFilterProvesAbsence) {
    Filter f(1000, 0.01);
    for (int i = 0; i < 1000; ++i) {
        hash128_t h = id_of("nothing/" + std::to_string(i));
        EXPECT_FALSE(laplace_absence_maybe_present(&f.f, &h));
    }
}

// A filter that does not exist proves nothing. Absence must never be claimed by
// accident — a null/zeroed filter says "maybe", so the caller still probes.
TEST(AbsenceFilter, NullFilterSaysMaybe) {
    hash128_t h = id_of("x");
    EXPECT_TRUE(laplace_absence_maybe_present(nullptr, &h));

    laplace_absence_filter_t zeroed{};
    EXPECT_TRUE(laplace_absence_maybe_present(&zeroed, &h));
}

TEST(AbsenceFilter, AddIsIdempotent) {
    Filter a(1000, 0.01);
    Filter b(1000, 0.01);
    hash128_t h = id_of("repeated");

    laplace_absence_add(&a.f, &h);
    for (int i = 0; i < 50; ++i) laplace_absence_add(&b.f, &h);

    ASSERT_EQ(a.f.word_count, b.f.word_count);
    EXPECT_EQ(0, std::memcmp(a.f.bits, b.f.bits, a.f.word_count * sizeof(uint64_t)));
}

// Order independence is what lets the blob be persisted and appended in place:
// the same id set must yield a bit-identical array however it was built.
TEST(AbsenceFilter, BitsAreOrderIndependent) {
    Filter fwd(10000, 0.01);
    Filter rev(10000, 0.01);

    for (int i = 0; i < 5000; ++i) { hash128_t h = id_of("o/" + std::to_string(i)); laplace_absence_add(&fwd.f, &h); }
    for (int i = 4999; i >= 0; --i) { hash128_t h = id_of("o/" + std::to_string(i)); laplace_absence_add(&rev.f, &h); }

    ASSERT_EQ(fwd.f.word_count, rev.f.word_count);
    EXPECT_EQ(0, std::memcmp(fwd.f.bits, rev.f.bits, fwd.f.word_count * sizeof(uint64_t)));
}

TEST(AbsenceFilter, SaveAttachRoundTrips) {
    Filter src(10000, 0.01);
    std::vector<hash128_t> added;
    for (int i = 0; i < 5000; ++i) {
        hash128_t h = id_of("blob/" + std::to_string(i));
        laplace_absence_add(&src.f, &h);
        added.push_back(h);
    }

    std::vector<uint8_t> blob(laplace_absence_blob_size(&src.f));
    ASSERT_EQ(0, laplace_absence_save(&src.f, blob.data(), blob.size()));

    laplace_absence_filter_t dst{};
    ASSERT_EQ(0, laplace_absence_attach(&dst, blob.data(), blob.size()));
    EXPECT_EQ(src.f.hdr.bit_count, dst.hdr.bit_count);
    EXPECT_EQ(src.f.hdr.hash_count, dst.hdr.hash_count);

    for (const auto& h : added) EXPECT_TRUE(laplace_absence_maybe_present(&dst, &h));
    // and an attached filter still proves absence
    hash128_t missing = id_of("blob/definitely-not-added");
    EXPECT_FALSE(laplace_absence_maybe_present(&dst, &missing));
}

TEST(AbsenceFilter, AttachRejectsGarbage) {
    laplace_absence_filter_t f{};
    std::vector<uint8_t> junk(1024, 0xAB);
    EXPECT_EQ(-3, laplace_absence_attach(&f, junk.data(), junk.size()));

    // Right magic, wrong version.
    Filter good(1000, 0.01);
    std::vector<uint8_t> blob(laplace_absence_blob_size(&good.f));
    ASSERT_EQ(0, laplace_absence_save(&good.f, blob.data(), blob.size()));
    reinterpret_cast<laplace_absence_header_t*>(blob.data())->format_version = 999;
    EXPECT_EQ(-3, laplace_absence_attach(&f, blob.data(), blob.size()));

    // Truncated blob.
    ASSERT_EQ(0, laplace_absence_save(&good.f, blob.data(), blob.size()));
    EXPECT_EQ(-3, laplace_absence_attach(&f, blob.data(), sizeof(laplace_absence_header_t) + 8));
}

TEST(AbsenceFilter, RejectsBadParameters) {
    laplace_absence_filter_t f{};
    EXPECT_EQ(-1, laplace_absence_create(&f, 0, 0.01));
    EXPECT_EQ(-1, laplace_absence_create(&f, 100, 0.0));
    EXPECT_EQ(-1, laplace_absence_create(&f, 100, 1.0));
    EXPECT_EQ(-1, laplace_absence_create(nullptr, 100, 0.01));
}

// The apply lane stages across parallel connections, so adds and queries race.
// Concurrency may never manufacture a false negative.
TEST(AbsenceFilter, ConcurrentAddsKeepTheNoFalseNegativeContract) {
    Filter f(200000, 0.01);
    const int threads = 8, per = 10000;

    std::vector<std::thread> pool;
    for (int t = 0; t < threads; ++t) {
        pool.emplace_back([&, t] {
            for (int i = 0; i < per; ++i) {
                hash128_t h = id_of("par/" + std::to_string(t) + "/" + std::to_string(i));
                laplace_absence_add(&f.f, &h);
            }
        });
    }
    for (auto& th : pool) th.join();

    for (int t = 0; t < threads; ++t)
        for (int i = 0; i < per; ++i) {
            hash128_t h = id_of("par/" + std::to_string(t) + "/" + std::to_string(i));
            ASSERT_TRUE(laplace_absence_maybe_present(&f.f, &h));
        }
}

// A degenerate h2 would collapse k probes onto one bit and quietly destroy the
// error rate. h2 is forced odd; this pins that an id with hi == 0 still spreads.
TEST(AbsenceFilter, DegenerateHighHalfStillSpreadsProbes) {
    Filter f(10000, 0.001);
    hash128_t h{};
    h.hi = 0;
    h.lo = 0x0123456789ABCDEFull;
    laplace_absence_add(&f.f, &h);

    int set = 0;
    for (size_t w = 0; w < f.f.word_count; ++w)
        set += __builtin_popcountll(f.f.bits[w]);
    EXPECT_EQ(static_cast<int>(f.f.hdr.hash_count), set)
        << "k distinct bits expected; a degenerate stride collapsed them";
}
