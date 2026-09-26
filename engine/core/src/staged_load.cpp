// The staged load's native operations: claims merged per id and consensus cells scored,
// each streamed out of staging once as a binary COPY stream and handed straight back.
// See laplace/core/staged_load.h.

#include "laplace/core/staged_load.h"

#include <algorithm>
#include <cstring>
#include <exception>
#include <new>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"

namespace {

// The consensus fold's neutral prior (extension consensus_fold_math.h).
constexpr int64_t kNeutralMu = INT64_C(1500000000000);
constexpr int64_t kInitialRd = INT64_C(350000000000);
constexpr int64_t kInitialVolatility = INT64_C(60000000);

constexpr uint8_t kCopySignature[11] = {'P', 'G', 'C', 'O', 'P', 'Y', '\n', 0xff, '\r', '\n', 0};

struct key16 {
    uint8_t b[16];
    bool operator==(const key16& o) const { return std::memcmp(b, o.b, 16) == 0; }
    bool operator<(const key16& o) const { return std::memcmp(b, o.b, 16) < 0; }
};

inline uint32_t be32(const uint8_t* p) {
    return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | uint32_t(p[3]);
}
inline uint16_t be16(const uint8_t* p) { return uint16_t((uint16_t(p[0]) << 8) | p[1]); }
inline int64_t be64(const uint8_t* p) {
    uint64_t v = 0;
    for (int i = 0; i < 8; ++i) v = (v << 8) | p[i];
    return static_cast<int64_t>(v);
}

struct fail : std::runtime_error {
    using std::runtime_error::runtime_error;
};

// A PostgreSQL binary COPY stream under construction.
struct copy_stream {
    std::vector<uint8_t> bytes;
    uint64_t rows = 0;
    bool closed = false;

    void reset() {
        bytes.clear();
        rows = 0;
        closed = false;
        bytes.insert(bytes.end(), kCopySignature, kCopySignature + sizeof(kCopySignature));
        put32(0);
        put32(0);
    }
    void put16(uint16_t v) { bytes.push_back(uint8_t(v >> 8)); bytes.push_back(uint8_t(v)); }
    void put32(uint32_t v) {
        for (int s = 24; s >= 0; s -= 8) bytes.push_back(uint8_t(v >> s));
    }
    void put64(int64_t value) {
        const uint64_t v = static_cast<uint64_t>(value);
        for (int s = 56; s >= 0; s -= 8) bytes.push_back(uint8_t(v >> s));
    }
    void row(uint16_t fields) { put16(fields); ++rows; }
    void field_bytes(const void* p, uint32_t n) {
        put32(n);
        const uint8_t* b = static_cast<const uint8_t*>(p);
        bytes.insert(bytes.end(), b, b + n);
    }
    void field_null() { put32(0xffffffffu); }
    void field_int2(int16_t v) { put32(2); put16(static_cast<uint16_t>(v)); }
    void field_int8(int64_t v) { put32(8); put64(v); }
    void field_bool(bool v) { put32(1); bytes.push_back(v ? 1 : 0); }
    void raw(const uint8_t* p, size_t n) { bytes.insert(bytes.end(), p, p + n); }
    void close() {
        if (!closed) { put16(0xffff); closed = true; }
    }
};

inline key16 key_of(const uint8_t* value, int32_t len, const char* what) {
    if (len != 16) throw fail(std::string("stage ") + what + " is not a 16-byte id");
    key16 k;
    std::memcpy(k.b, value, 16);
    return k;
}

struct claim {
    key16 id, subject, type, object, source, context;
    bool has_object, has_context, has_mask, admitted;
    int16_t outcome;
    int64_t ts_pg_us, games, sum, opponent_rd, opponent_rating;
    uint8_t mask[32];
    uint32_t lane;
};

struct cell_key {
    key16 subject, type, object;
    bool has_object;
    bool operator==(const cell_key& o) const {
        return has_object == o.has_object && subject == o.subject && type == o.type
            && (!has_object || object == o.object);
    }
};

struct period_group {
    key16 witness;
    int64_t opponent_rating, phi, games, sum;
};

struct cell {
    cell_key key;
    key16 id;          // consensus_id: blake3(subject || type || object-or-zero)
    int64_t games = 0;
    int64_t ts_pg_us = INT64_MIN;
    std::vector<period_group> groups;
};

struct fold_memo_key {
    int64_t in[7];
    bool operator==(const fold_memo_key& o) const { return std::memcmp(in, o.in, sizeof(in)) == 0; }
};
struct fold_memo_hash {
    size_t operator()(const fold_memo_key& k) const {
        uint64_t h = 1469598103934665603ull;
        for (int64_t v : k.in) { h ^= static_cast<uint64_t>(v); h *= 1099511628211ull; }
        return static_cast<size_t>(h);
    }
};

int64_t int8_field(const uint8_t* v, int32_t len, const char* what) {
    if (len != 8) throw fail(std::string("stage ") + what + " is not an int8");
    return be64(v);
}

// One claim from the 14 attestation columns. Only replayable testimony folds from
// its row alone, and a claim is at least one game.
claim parse_claim(const uint8_t* const* v, const int32_t* len) {
    claim c{};
    c.id = key_of(v[0], len[0], "attestation id");
    c.subject = key_of(v[1], len[1], "attestation subject");
    c.type = key_of(v[2], len[2], "attestation type");
    c.has_object = len[3] != -1;
    if (c.has_object) c.object = key_of(v[3], len[3], "attestation object");
    c.source = key_of(v[4], len[4], "attestation source");
    c.has_context = len[5] != -1;
    if (c.has_context) c.context = key_of(v[5], len[5], "attestation context");
    if (len[6] != 2) throw fail("stage attestation outcome is not an int2");
    c.outcome = static_cast<int16_t>(be16(v[6]));
    c.ts_pg_us = int8_field(v[7], len[7], "attestation timestamp");
    c.games = int8_field(v[8], len[8], "observation count");
    c.sum = int8_field(v[9], len[9], "score sum");
    c.opponent_rd = int8_field(v[10], len[10], "opponent rd");
    c.opponent_rating = int8_field(v[11], len[11], "opponent rating");
    if (len[12] != 1) throw fail("stage fold_replayable is not a bool");
    if (v[12][0] == 0)
        throw fail("non-replayable testimony needs its transient fold input; the source reducer folds replayable claims only");
    c.has_mask = len[13] != -1;
    if (c.has_mask) {
        if (len[13] != 32) throw fail("stage qualifier mask is not 32 bytes");
        std::memcpy(c.mask, v[13], 32);
    }
    c.admitted = true;
    if (c.games <= 0) throw fail("stage claim has no games");
    return c;
}

// One claim identity, observed again: add the games and score, OR the qualifiers,
// keep the latest observation's row, classify the totals.
void merge_claim(claim& prior, const claim& c) {
    if (__builtin_add_overflow(prior.games, c.games, &prior.games)
        || __builtin_add_overflow(prior.sum, c.sum, &prior.sum))
        throw fail("merged claim games or score exceed int8");
    if (c.has_mask) {
        if (!prior.has_mask) { std::memset(prior.mask, 0, 32); prior.has_mask = true; }
        for (int b = 0; b < 32; ++b) prior.mask[b] |= c.mask[b];
    }
    if (c.ts_pg_us > prior.ts_pg_us) {
        prior.ts_pg_us = c.ts_pg_us;
        prior.opponent_rd = c.opponent_rd;
        prior.opponent_rating = c.opponent_rating;
    }
    int16_t outcome;
    if (laplace_attestation_outcome_from_totals_fp(prior.games, prior.sum, &outcome) != 0)
        throw fail("merged claim totals cannot be classified");
    prior.outcome = outcome;
}

void emit_claim(copy_stream& s, const claim& c) {
    s.row(14);
    s.field_bytes(c.id.b, 16);
    s.field_bytes(c.subject.b, 16);
    s.field_bytes(c.type.b, 16);
    if (c.has_object) s.field_bytes(c.object.b, 16); else s.field_null();
    s.field_bytes(c.source.b, 16);
    if (c.has_context) s.field_bytes(c.context.b, 16); else s.field_null();
    s.field_int2(c.outcome);
    s.field_int8(c.ts_pg_us);
    s.field_int8(c.games);
    s.field_int8(c.sum);
    s.field_int8(c.opponent_rd);
    s.field_int8(c.opponent_rating);
    s.field_bool(true);
    if (c.has_mask) s.field_bytes(c.mask, 32); else s.field_null();
}

using fold_memo = std::unordered_map<fold_memo_key, glicko2_state_t, fold_memo_hash>;

void fold_period(fold_memo& memo, glicko2_state_t& st, const period_group* groups, size_t n) {
    if (n == 1) {
        fold_memo_key key{{st.rating, st.rd, st.volatility, groups[0].opponent_rating,
                           groups[0].phi, groups[0].games, groups[0].sum}};
        auto it = memo.find(key);
        if (it != memo.end()) { st = it->second; return; }
        if (glicko2_fold_uniform_period(&st, groups[0].opponent_rating, groups[0].phi,
                groups[0].games, groups[0].sum, LAPLACE_GLICKO2_DEFAULT_TAU, 0) != 0)
            throw fail("native rating-period update failed");
        memo.emplace(key, st);
        return;
    }
    std::vector<int64_t> opponents(n), phis(n), games(n), sums(n);
    for (size_t g = 0; g < n; ++g) {
        opponents[g] = groups[g].opponent_rating == 0 ? kNeutralMu : groups[g].opponent_rating;
        phis[g] = groups[g].phi;
        games[g] = groups[g].games;
        sums[g] = groups[g].sum;
    }
    if (glicko2_fold_grouped_period(&st, opponents.data(), phis.data(), games.data(), sums.data(),
            n, LAPLACE_GLICKO2_DEFAULT_TAU, 0) != 0)
        throw fail("exact rating period exceeds fixed-point capacity");
}

}  // namespace

// ---------------------------------------------------------------------------
// The staged load. A source is extracted completely into staging first; these
// operations then stream the staged records out of PostgreSQL once, in the order
// the operation needs, and hand the result straight back. Memory is bounded by one
// claim (merge) or one consensus cell (score), not by the source.
// ---------------------------------------------------------------------------

namespace {

// A binary COPY stream arriving in slices of any size (a COPY TO STDOUT read in
// pieces). Whole rows are consumed as they complete; a partial row waits for the
// next slice. Field pointers stay valid until the next append.
struct copy_slices {
    std::vector<uint8_t> buf;
    size_t at = 0;
    bool header = false, ended = false;

    void append(const uint8_t* p, size_t n) {
        if (at > 0) { buf.erase(buf.begin(), buf.begin() + static_cast<std::ptrdiff_t>(at)); at = 0; }
        buf.insert(buf.end(), p, p + n);
    }
    // 1: a row is in values/lengths; 0: more bytes are needed; -1: the stream ended.
    int next(int expected, const uint8_t** values, int32_t* lengths) {
        if (ended) return -1;
        const size_t n = buf.size();
        const uint8_t* p = buf.data();
        if (!header) {
            if (n - at < 19) return 0;
            if (std::memcmp(p + at, kCopySignature, sizeof(kCopySignature)) != 0)
                throw fail("binary COPY stream has no signature");
            const uint32_t extension = be32(p + at + 15);
            if (n - at < 19 + static_cast<size_t>(extension)) return 0;
            at += 19 + extension;
            header = true;
        }
        size_t cur = at;
        if (n - cur < 2) return 0;
        const uint16_t fields = be16(p + cur);
        cur += 2;
        if (fields == 0xffff) { at = cur; ended = true; return -1; }
        if (fields != expected) throw fail("staged row has an unexpected field count");
        for (int f = 0; f < expected; ++f) {
            if (n - cur < 4) return 0;
            const int32_t len = static_cast<int32_t>(be32(p + cur));
            cur += 4;
            if (len < -1) throw fail("binary COPY field has a negative length");
            if (len > 0 && static_cast<size_t>(len) > n - cur) return 0;
            values[f] = p + cur;
            lengths[f] = len;
            if (len > 0) cur += static_cast<size_t>(len);
        }
        at = cur;
        return 1;
    }
};

// Output handed back in pieces: the first piece carries the COPY header, the last
// the trailer.
struct copy_output {
    copy_stream s;
    bool started = false;
    void begin() { if (!started) { s.reset(); started = true; } }
    void take_done() { s.bytes.clear(); }
};

}  // namespace

// Staged claims sorted by attestation id, merged per id: every observation of one
// claim identity in the source becomes one claim row.
struct laplace_staged_claims {
    std::string error;
    copy_slices in;
    copy_output out;
    claim current{};
    bool has = false;
    uint64_t rows_in = 0, rows_out = 0;
};

// New evidence sorted by consensus cell and witness, each row carrying its cell's
// prior standing: one rating period per witness per cell, exactly as the source
// reducer folds.
struct laplace_staged_score {
    std::string error;
    copy_slices in;
    copy_output novel, standing;
    cell current;
    bool has = false, has_prior = false;
    int64_t prior_rating = 0, prior_rd = 0, prior_volatility = 0;
    fold_memo memo;
    uint64_t cells = 0, games = 0;
};

namespace {

void claims_flush(laplace_staged_claims* m) {
    if (!m->has) return;
    emit_claim(m->out.s, m->current);
    ++m->rows_out;
    m->has = false;
}

void claims_feed(laplace_staged_claims* m, const uint8_t* p, size_t n, int final) {
    m->out.begin();
    m->in.append(p, n);
    const uint8_t* v[14];
    int32_t len[14];
    for (int got; (got = m->in.next(14, v, len)) == 1;) {
        ++m->rows_in;
        const claim c = parse_claim(v, len);
        if (m->has && m->current.id == c.id) { merge_claim(m->current, c); continue; }
        if (m->has && c.id < m->current.id) throw fail("staged claims are not sorted by id");
        claims_flush(m);
        m->current = c;
        m->has = true;
    }
    if (final) {
        claims_flush(m);
        m->out.s.close();
    }
}

void score_flush(laplace_staged_score* s) {
    if (!s->has) return;
    cell& target = s->current;
    glicko2_state_t st;
    if (s->has_prior) glicko2_init(&st, s->prior_rating, s->prior_rd, s->prior_volatility);
    else glicko2_init(&st, kNeutralMu, kInitialRd, kInitialVolatility);
    // Rows arrive in (witness, opponent rating, phi) order within the cell, so equal
    // groups are adjacent and each witness's groups form one rating period.
    size_t begin = 0;
    while (begin < target.groups.size()) {
        size_t end = begin + 1;
        while (end < target.groups.size() && target.groups[end].witness == target.groups[begin].witness) ++end;
        fold_period(s->memo, st, target.groups.data() + begin, end - begin);
        begin = end;
    }
    copy_stream& out = s->has_prior ? s->standing.s : s->novel.s;
    out.row(9);
    out.field_bytes(target.id.b, 16);
    out.field_bytes(target.key.subject.b, 16);
    out.field_bytes(target.key.type.b, 16);
    if (target.key.has_object) out.field_bytes(target.key.object.b, 16);
    else out.field_null();
    out.field_int8(st.rating);
    out.field_int8(st.rd);
    out.field_int8(st.volatility);
    out.field_int8(target.games);
    out.field_int8(target.ts_pg_us);
    ++s->cells;
    s->games += static_cast<uint64_t>(target.games);
    s->has = false;
}

// Row: subject, type, object?, witness, opponent rating, opponent rd, games, score sum,
// observed at, prior rating?, prior rd?, prior volatility?. The cell is (subject, type,
// object); its id is computed here exactly as the source reducer computes it.
void score_feed(laplace_staged_score* s, const uint8_t* p, size_t n, int final) {
    s->novel.begin();
    s->standing.begin();
    s->in.append(p, n);
    const uint8_t* v[12];
    int32_t len[12];
    for (int got; (got = s->in.next(12, v, len)) == 1;) {
        cell_key k{};
        k.subject = key_of(v[0], len[0], "cell subject");
        k.type = key_of(v[1], len[1], "cell type");
        k.has_object = len[2] != -1;
        if (k.has_object) k.object = key_of(v[2], len[2], "cell object");
        if (!s->has || !(s->current.key == k)) {
            score_flush(s);
            s->current = cell{};
            s->current.key = k;
            uint8_t buf[48];
            std::memcpy(buf, k.subject.b, 16);
            std::memcpy(buf + 16, k.type.b, 16);
            if (k.has_object) std::memcpy(buf + 32, k.object.b, 16);
            else std::memset(buf + 32, 0, 16);
            hash128_t h;
            hash128_blake3(buf, sizeof(buf), &h);
            std::memcpy(s->current.id.b, &h, 16);
            s->has_prior = len[9] != -1;
            if (s->has_prior) {
                s->prior_rating = int8_field(v[9], len[9], "prior rating");
                s->prior_rd = int8_field(v[10], len[10], "prior rd");
                s->prior_volatility = int8_field(v[11], len[11], "prior volatility");
            }
            s->has = true;
        }
    const key16 witness = key_of(v[3], len[3], "witness");
        const int64_t opponent_rating = int8_field(v[4], len[4], "opponent rating");
        const int64_t phi = int8_field(v[5], len[5], "opponent rd");
        const int64_t games = int8_field(v[6], len[6], "games");
        const int64_t sum = int8_field(v[7], len[7], "score sum");
        const int64_t ts = int8_field(v[8], len[8], "observed at");
        cell& target = s->current;
        if (__builtin_add_overflow(target.games, games, &target.games))
            throw fail("cell games exceed int8");
        target.ts_pg_us = std::max(target.ts_pg_us, ts);
        if (!target.groups.empty()) {
            period_group& last = target.groups.back();
            if (last.witness == witness && last.opponent_rating == opponent_rating && last.phi == phi) {
                if (__builtin_add_overflow(last.games, games, &last.games)
                    || __builtin_add_overflow(last.sum, sum, &last.sum))
                    throw fail("rating-period group exceeds int8");
                continue;
            }
        }
        target.groups.push_back({witness, opponent_rating, phi, games, sum});
    }
    if (final) {
        score_flush(s);
        s->novel.s.close();
        s->standing.s.close();
    }
}

template <class T, class F>
int staged_guard(T* o, F&& body) {
    if (!o) return -1;
    try {
        body();
        return 0;
    } catch (const std::bad_alloc&) {
        o->error = "staged load exhausted memory";
    } catch (const std::exception& e) {
        o->error = e.what();
    } catch (...) {
        o->error = "staged load failed";
    }
    return -2;
}

}  // namespace

extern "C" {

laplace_staged_claims_t* laplace_staged_claims_new(void) { return new (std::nothrow) laplace_staged_claims(); }
void laplace_staged_claims_free(laplace_staged_claims_t* m) { delete m; }
const char* laplace_staged_claims_error(const laplace_staged_claims_t* m) { return m ? m->error.c_str() : "null staged claims"; }
int laplace_staged_claims_feed(laplace_staged_claims_t* m, const uint8_t* bytes, size_t n, int final) {
    return staged_guard(m, [&] { claims_feed(m, bytes, n, final); });
}
const uint8_t* laplace_staged_claims_output(laplace_staged_claims_t* m, size_t* len) {
    if (!m || !len) return nullptr;
    *len = m->out.s.bytes.size();
    return m->out.s.bytes.data();
}
void laplace_staged_claims_consume(laplace_staged_claims_t* m) { if (m) m->out.take_done(); }
void laplace_staged_claims_counts(const laplace_staged_claims_t* m, uint64_t* rows_in, uint64_t* rows_out) {
    if (!m) return;
    if (rows_in) *rows_in = m->rows_in;
    if (rows_out) *rows_out = m->rows_out;
}

laplace_staged_score_t* laplace_staged_score_new(void) { return new (std::nothrow) laplace_staged_score(); }
void laplace_staged_score_free(laplace_staged_score_t* s) { delete s; }
const char* laplace_staged_score_error(const laplace_staged_score_t* s) { return s ? s->error.c_str() : "null staged score"; }
int laplace_staged_score_feed(laplace_staged_score_t* s, const uint8_t* bytes, size_t n, int final) {
    return staged_guard(s, [&] { score_feed(s, bytes, n, final); });
}
const uint8_t* laplace_staged_score_output(laplace_staged_score_t* s, int standing, size_t* len) {
    if (!s || !len) return nullptr;
    copy_stream& out = standing ? s->standing.s : s->novel.s;
    *len = out.bytes.size();
    return out.bytes.data();
}
void laplace_staged_score_consume(laplace_staged_score_t* s, int standing) {
    if (s) (standing ? s->standing : s->novel).take_done();
}
void laplace_staged_score_counts(const laplace_staged_score_t* s, uint64_t* cells, uint64_t* games) {
    if (!s) return;
    if (cells) *cells = s->cells;
    if (games) *games = s->games;
}

}  // extern "C"
