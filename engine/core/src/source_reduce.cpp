// Per-source reduction, routing and rating-period fold of native intent stages.
// See laplace/core/source_reduce.h for the order of operations this file owns.

#include "laplace/core/source_reduce.h"

#include <algorithm>
#include <cstring>
#include <exception>
#include <new>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/highway_table.h"

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

struct key16_hash {
    size_t operator()(const key16& k) const {
        uint64_t v;
        std::memcpy(&v, k.b + 8, sizeof(v));
        return static_cast<size_t>(v ^ (v >> 29));
    }
};

inline uint32_t lane_of(const key16& k, uint32_t lanes) {
    uint64_t v;
    std::memcpy(&v, k.b, sizeof(v));
    return static_cast<uint32_t>(v % lanes);
}

// PostgreSQL's HASH partition routing for one bytea key: hash_bytes_extended
// (src/common/hashfn.c, little-endian path) seeded with HASH_PARTITION_SEED, folded
// by hash_combine64 from zero (compute_partition_hash_value), modulo the greatest
// modulus (get_partition_for_tuple). A lane that owns whole partitions writes index
// trees no other lane touches.
inline uint32_t rotl32(uint32_t x, int k) { return (x << k) | (x >> (32 - k)); }

uint64_t pg_hash_bytes_extended(const uint8_t* k, uint32_t keylen, uint64_t seed) {
    uint32_t a, b, c, len = keylen;
    a = b = c = 0x9e3779b9u + len + 3923095u;
    auto mix = [&] {
        a -= c; a ^= rotl32(c, 4);  c += b;
        b -= a; b ^= rotl32(a, 6);  a += c;
        c -= b; c ^= rotl32(b, 8);  b += a;
        a -= c; a ^= rotl32(c, 16); c += b;
        b -= a; b ^= rotl32(a, 19); a += c;
        c -= b; c ^= rotl32(b, 4);  b += a;
    };
    if (seed != 0) {
        a += static_cast<uint32_t>(seed >> 32);
        b += static_cast<uint32_t>(seed);
        mix();
    }
    while (len >= 12) {
        a += k[0] + (uint32_t(k[1]) << 8) + (uint32_t(k[2]) << 16) + (uint32_t(k[3]) << 24);
        b += k[4] + (uint32_t(k[5]) << 8) + (uint32_t(k[6]) << 16) + (uint32_t(k[7]) << 24);
        c += k[8] + (uint32_t(k[9]) << 8) + (uint32_t(k[10]) << 16) + (uint32_t(k[11]) << 24);
        mix();
        k += 12;
        len -= 12;
    }
    switch (len) {
        case 11: c += uint32_t(k[10]) << 24; [[fallthrough]];
        case 10: c += uint32_t(k[9]) << 16; [[fallthrough]];
        case 9: c += uint32_t(k[8]) << 8; [[fallthrough]];
        case 8: b += uint32_t(k[7]) << 24; [[fallthrough]];
        case 7: b += uint32_t(k[6]) << 16; [[fallthrough]];
        case 6: b += uint32_t(k[5]) << 8; [[fallthrough]];
        case 5: b += k[4]; [[fallthrough]];
        case 4: a += uint32_t(k[3]) << 24; [[fallthrough]];
        case 3: a += uint32_t(k[2]) << 16; [[fallthrough]];
        case 2: a += uint32_t(k[1]) << 8; [[fallthrough]];
        case 1: a += k[0]; break;
        default: break;
    }
    c ^= b; c -= rotl32(b, 14);
    a ^= c; a -= rotl32(c, 11);
    b ^= a; b -= rotl32(a, 25);
    c ^= b; c -= rotl32(b, 16);
    a ^= c; a -= rotl32(c, 4);
    b ^= a; b -= rotl32(a, 14);
    c ^= b; c -= rotl32(b, 24);
    return (uint64_t(b) << 32) | c;
}

inline uint32_t partition_of(const key16& k, uint32_t modulus) {
    const uint64_t hash = pg_hash_bytes_extended(k.b, 16, UINT64_C(0x7A5B22367996DCFD));
    const uint64_t row = hash + UINT64_C(0x49a0f4dd15e5a8e3);  // hash_combine64(0, hash)
    return static_cast<uint32_t>(row % modulus);
}

// The lane that writes a key: whole PostgreSQL partitions per lane when the table is
// HASH partitioned (modulus > 0), otherwise an even split of the id space.
inline uint32_t route_of(const key16& k, uint32_t modulus, uint32_t lanes, uint32_t* partition) {
    if (modulus == 0) { *partition = 0; return lane_of(k, lanes); }
    *partition = partition_of(k, modulus);
    return *partition % lanes;
}

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

// Reads the rows of a binary COPY stream (header and optional trailer included).
struct copy_reader {
    const uint8_t* p;
    size_t n;
    size_t at = 0;
    copy_reader(const uint8_t* bytes, size_t len) : p(bytes), n(len) {
        if (n == 0) return;
        if (n < 19 || std::memcmp(p, kCopySignature, sizeof(kCopySignature)) != 0)
            throw fail("binary COPY stream has no signature");
        at = 11 + 4;
        const uint32_t extension = be32(p + at);
        at += 4;
        if (extension > n - at) throw fail("binary COPY header extension overruns the stream");
        at += extension;
    }
    // Returns the field count of the next row, or -1 at the end of the stream.
    int next_row() {
        if (at >= n) return -1;
        if (n - at < 2) throw fail("truncated binary COPY row");
        const uint16_t fields = be16(p + at);
        at += 2;
        if (fields == 0xffff) return -1;
        return fields;
    }
    // Returns the field length (-1 for NULL) and advances past it; `out` points at the value.
    int32_t field(const uint8_t** out) {
        if (n - at < 4) throw fail("truncated binary COPY field");
        const int32_t len = static_cast<int32_t>(be32(p + at));
        at += 4;
        if (len < -1 || (len > 0 && static_cast<size_t>(len) > n - at))
            throw fail("binary COPY field overruns the stream");
        *out = p + at;
        if (len > 0) at += static_cast<size_t>(len);
        return len;
    }
};

// Walks one stage tuple buffer (rows without header or trailer).
struct tuple_walk {
    const uint8_t* p;
    size_t n;
    size_t at = 0;
    tuple_walk(const uint8_t* bytes, size_t len) : p(bytes), n(len) {}
    bool done() const { return at >= n; }
    // Parses the next row into field pointers/lengths; returns the row's byte span.
    size_t row(int expected, const uint8_t** values, int32_t* lengths, size_t* row_start) {
        *row_start = at;
        if (n - at < 2) throw fail("truncated stage row");
        const uint16_t fields = be16(p + at);
        if (fields != expected) throw fail("stage row has an unexpected field count");
        at += 2;
        for (int f = 0; f < expected; ++f) {
            if (n - at < 4) throw fail("truncated stage field");
            const int32_t len = static_cast<int32_t>(be32(p + at));
            at += 4;
            if (len < -1 || (len > 0 && static_cast<size_t>(len) > n - at))
                throw fail("stage field overruns its row");
            values[f] = p + at;
            lengths[f] = len;
            if (len > 0) at += static_cast<size_t>(len);
        }
        return at - *row_start;
    }
};

inline key16 key_of(const uint8_t* value, int32_t len, const char* what) {
    if (len != 16) throw fail(std::string("stage ") + what + " is not a 16-byte id");
    key16 k;
    std::memcpy(k.b, value, 16);
    return k;
}

struct raw_row {
    key16 id;
    uint64_t offset;   // into the reducer's row arena
    uint32_t length;
    uint32_t lane;
    uint32_t partition;
};

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

struct cell_key_hash {
    size_t operator()(const cell_key& k) const {
        return key16_hash{}(k.subject) ^ (key16_hash{}(k.type) * 31u) ^ (k.has_object ? key16_hash{}(k.object) * 131u : 0u);
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

struct lane_state {
    std::vector<uint32_t> claims;  // indexes into reducer claims, sorted by claim id
    std::vector<cell> cells;
    copy_stream entities, physicalities, attestations, cell_keys, consensus, standing, masks;
    uint64_t folded_cells = 0, folded_games = 0;  // this lane's latest fold
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

}  // namespace

struct laplace_source_reduce {
    std::string error;
    std::vector<uint8_t> arena;
    std::vector<raw_row> entities, physicalities;
    std::unordered_map<key16, uint32_t, key16_hash> entity_index, physicality_index;
    std::unordered_set<key16, key16_hash> placed;  // entity ids that own a physicality
    std::vector<claim> claims;
    std::unordered_map<key16, uint32_t, key16_hash> claim_index;
    std::unordered_set<key16, key16_hash> excluded_types;
    std::vector<lane_state> lanes;
    std::unordered_map<key16, laplace_mask256_t, key16_hash> masks;
    std::vector<hash128_t> unresolved;  // (entity, type) pairs
    std::unordered_set<std::string> unresolved_seen;
    std::unordered_map<key16, int, key16_hash> type_bits;  // -1 = no Highway bit
    std::unordered_map<fold_memo_key, glicko2_state_t, fold_memo_hash> memo;
    laplace_source_reduce_stats_t stats{};
    uint32_t modulus[3] = {0, 0, 0};  // HASH partition moduli: entities, physicalities, claims
    bool routed = false;
};

namespace {

void add_entities(laplace_source_reduce* r, const uint8_t* p, size_t n) {
    tuple_walk walk(p, n);
    const uint8_t* v[3];
    int32_t len[3];
    while (!walk.done()) {
        size_t start;
        const size_t span = walk.row(3, v, len, &start);
        ++r->stats.staged_entities;
        const key16 id = key_of(v[0], len[0], "entity id");
        auto [it, fresh] = r->entity_index.emplace(id, static_cast<uint32_t>(r->entities.size()));
        if (!fresh) continue;
        r->entities.push_back({id, r->arena.size(), static_cast<uint32_t>(span), 0, 0});
        r->arena.insert(r->arena.end(), p + start, p + start + span);
    }
}

void add_physicalities(laplace_source_reduce* r, const uint8_t* p, size_t n) {
    tuple_walk walk(p, n);
    const uint8_t* v[10];
    int32_t len[10];
    while (!walk.done()) {
        size_t start;
        const size_t span = walk.row(10, v, len, &start);
        ++r->stats.staged_physicalities;
        const key16 id = key_of(v[0], len[0], "physicality id");
        auto [it, fresh] = r->physicality_index.emplace(id, static_cast<uint32_t>(r->physicalities.size()));
        if (!fresh) continue;
        const key16 owner = key_of(v[1], len[1], "physicality entity id");
        r->placed.insert(owner);
        r->physicalities.push_back({id, r->arena.size(), static_cast<uint32_t>(span), 0, 0});
        r->arena.insert(r->arena.end(), p + start, p + start + span);
    }
}

int64_t int8_field(const uint8_t* v, int32_t len, const char* what) {
    if (len != 8) throw fail(std::string("stage ") + what + " is not an int8");
    return be64(v);
}

void add_claims(laplace_source_reduce* r, const uint8_t* p, size_t n) {
    tuple_walk walk(p, n);
    const uint8_t* v[14];
    int32_t len[14];
    while (!walk.done()) {
        size_t start;
        walk.row(14, v, len, &start);
        ++r->stats.staged_attestations;
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

        auto [it, fresh] = r->claim_index.emplace(c.id, static_cast<uint32_t>(r->claims.size()));
        if (fresh) {
            r->claims.push_back(c);
            continue;
        }
        // One claim identity, observed again: add the games and score, OR the
        // qualifiers, keep the latest observation's row, classify the totals.
        claim& prior = r->claims[it->second];
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
        ++r->stats.merged_attestations;
    }
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

int type_bit(laplace_source_reduce* r, const key16& type) {
    auto it = r->type_bits.find(type);
    if (it != r->type_bits.end()) return it->second;
    hash128_t tid;
    std::memcpy(&tid, type.b, 16);
    uint8_t bit = 0;
    float rank = 0;
    uint8_t band = 0;
    const int resolved = highway_table_relation_by_hash(&tid, &bit, &rank, &band) == 0 ? bit : -1;
    r->type_bits.emplace(type, resolved);
    return resolved;
}

void deposit_mask(laplace_source_reduce* r, const key16& entity, const key16& type) {
    const int bit = type_bit(r, type);
    if (bit < 0) {
        std::string pair(reinterpret_cast<const char*>(entity.b), 16);
        pair.append(reinterpret_cast<const char*>(type.b), 16);
        if (r->unresolved_seen.insert(pair).second) {
            hash128_t e, t;
            std::memcpy(&e, entity.b, 16);
            std::memcpy(&t, type.b, 16);
            r->unresolved.push_back(e);
            r->unresolved.push_back(t);
        }
        return;
    }
    auto [it, fresh] = r->masks.emplace(entity, laplace_mask256_t{});
    uint8_t* bytes = reinterpret_cast<uint8_t*>(&it->second);
    bytes[bit >> 3] |= static_cast<uint8_t>(1u << (bit & 7));
}

void route(laplace_source_reduce* r, uint32_t n) {
    r->lanes.assign(n, lane_state{});
    for (auto& lane : r->lanes) {
        lane.entities.reset();
        lane.physicalities.reset();
        lane.attestations.reset();
        lane.masks.reset();
    }
    // Rows in (partition, id) order: each lane's COPY walks one partition at a time.
    auto place = [n](std::vector<raw_row>& rows, uint32_t modulus) {
        for (auto& row : rows) row.lane = route_of(row.id, modulus, n, &row.partition);
        std::sort(rows.begin(), rows.end(), [](const raw_row& a, const raw_row& b) {
            return a.partition != b.partition ? a.partition < b.partition : a.id < b.id;
        });
    };
    place(r->entities, r->modulus[0]);
    place(r->physicalities, r->modulus[1]);

    // Highway masks: every claimed relation ORs its bit into its subject's and object's
    // mask. A claim that turns out to be a replay was deposited when it was first
    // admitted, and OR is idempotent, so the masks follow from the whole reduction and
    // ride on the entity rows instead of a separate pass.
    for (const claim& c : r->claims) {
        if (r->excluded_types.count(c.type)) continue;
        deposit_mask(r, c.subject, c.type);
        if (c.has_object) deposit_mask(r, c.object, c.type);
    }

    for (auto& row : r->entities) {
        // The entity row, whether a physicality of this reduction realizes it, and its mask.
        const bool placed = r->placed.count(row.id) != 0;
        auto& s = r->lanes[row.lane].entities;
        s.row(5);
        s.raw(r->arena.data() + row.offset + 2, row.length - 2);
        s.field_bool(placed);
        auto mask = r->masks.find(row.id);
        if (mask != r->masks.end()) {
            s.field_bytes(&mask->second, 32);
            r->masks.erase(mask);
        } else {
            s.field_null();
        }
        if (!placed) ++r->stats.unplaced_entities;
    }
    // Entities the claims name without staging them: their masks update stored rows.
    std::vector<std::pair<key16, laplace_mask256_t>> named(r->masks.begin(), r->masks.end());
    std::vector<uint32_t> named_partition(named.size());
    std::vector<uint32_t> named_order(named.size());
    for (uint32_t i = 0; i < named.size(); ++i) {
        named_order[i] = i;
        route_of(named[i].first, r->modulus[0], n, &named_partition[i]);
    }
    std::sort(named_order.begin(), named_order.end(), [&](uint32_t a, uint32_t b) {
        if (named_partition[a] != named_partition[b]) return named_partition[a] < named_partition[b];
        return named[a].first < named[b].first;
    });
    for (uint32_t i : named_order) {
        uint32_t partition;
        auto& s = r->lanes[route_of(named[i].first, r->modulus[0], n, &partition)].masks;
        s.row(2);
        s.field_bytes(named[i].first.b, 16);
        s.field_bytes(&named[i].second, 32);
    }
    r->stats.masked_entities = named.size();
    std::unordered_map<key16, laplace_mask256_t, key16_hash>().swap(r->masks);

    for (auto& row : r->physicalities) {
        auto& s = r->lanes[row.lane].physicalities;
        s.raw(r->arena.data() + row.offset, row.length);
        ++s.rows;
    }
    std::vector<uint32_t> partition(r->claims.size());
    std::vector<uint32_t> order(r->claims.size());
    for (uint32_t i = 0; i < order.size(); ++i) {
        order[i] = i;
        claim& c = r->claims[i];
        c.lane = route_of(c.subject, r->modulus[2], n, &partition[i]);
    }
    std::sort(order.begin(), order.end(), [r, &partition](uint32_t a, uint32_t b) {
        if (partition[a] != partition[b]) return partition[a] < partition[b];
        const claim& x = r->claims[a];
        const claim& y = r->claims[b];
        int c = std::memcmp(x.subject.b, y.subject.b, 16);
        return c != 0 ? c < 0 : x.id < y.id;
    });
    for (uint32_t i : order) {
        claim& c = r->claims[i];
        r->lanes[c.lane].claims.push_back(i);
        emit_claim(r->lanes[c.lane].attestations, c);
    }
    for (auto& lane : r->lanes) {
        lane.entities.close();
        lane.physicalities.close();
        lane.attestations.close();
        lane.masks.close();
    }
    // The rows now live in the lane streams.
    std::vector<uint8_t>().swap(r->arena);
    r->routed = true;
}

void admit(laplace_source_reduce* r, uint32_t lane, const uint8_t* p, size_t n) {
    lane_state& l = r->lanes[lane];
    if (p == nullptr && n == 0) {
        for (uint32_t i : l.claims) r->claims[i].admitted = true;
        return;
    }
    std::unordered_set<key16, key16_hash> novel;
    copy_reader reader(p, n);
    for (int fields; (fields = reader.next_row()) >= 0;) {
        if (fields != 1) throw fail("admitted-claim stream must carry one id column");
        const uint8_t* v = nullptr;
        const int32_t len = reader.field(&v);
        novel.insert(key_of(v, len, "admitted claim id"));
    }
    for (uint32_t i : l.claims) {
        claim& c = r->claims[i];
        c.admitted = novel.count(c.id) != 0;
    }
}

void build_cells(laplace_source_reduce* r, uint32_t lane) {
    lane_state& l = r->lanes[lane];
    l.cells.clear();
    l.folded_cells = 0;
    l.folded_games = 0;
    std::unordered_map<cell_key, uint32_t, cell_key_hash> index;
    for (uint32_t i : l.claims) {
        const claim& c = r->claims[i];
        if (!c.admitted || r->excluded_types.count(c.type)) continue;
        cell_key k{c.subject, c.type, c.has_object ? c.object : key16{}, c.has_object};
        auto [it, fresh] = index.emplace(k, static_cast<uint32_t>(l.cells.size()));
        if (fresh) {
            cell fresh_cell;
            fresh_cell.key = k;
            uint8_t buf[48];
            std::memcpy(buf, k.subject.b, 16);
            std::memcpy(buf + 16, k.type.b, 16);
            if (k.has_object) std::memcpy(buf + 32, k.object.b, 16);
            else std::memset(buf + 32, 0, 16);
            hash128_t h;
            hash128_blake3(buf, sizeof(buf), &h);
            std::memcpy(fresh_cell.id.b, &h, 16);
            l.cells.push_back(std::move(fresh_cell));
        }
        cell& target = l.cells[it->second];
        if (__builtin_add_overflow(target.games, c.games, &target.games))
            throw fail("cell games exceed int8");
        target.ts_pg_us = std::max(target.ts_pg_us, c.ts_pg_us);
        bool grouped = false;
        for (auto& g : target.groups)
            if (g.witness == c.source && g.opponent_rating == c.opponent_rating && g.phi == c.opponent_rd) {
                if (__builtin_add_overflow(g.games, c.games, &g.games)
                    || __builtin_add_overflow(g.sum, c.sum, &g.sum))
                    throw fail("rating-period group exceeds int8");
                grouped = true;
                break;
            }
        if (!grouped)
            target.groups.push_back({c.source, c.opponent_rating, c.opponent_rd, c.games, c.sum});
    }
    // Cells in consensus key order; each cell's periods in witness order and each
    // period's groups in (opponent rating, phi) order, independent of arrival order.
    std::sort(l.cells.begin(), l.cells.end(), [](const cell& a, const cell& b) {
        int c = std::memcmp(a.key.type.b, b.key.type.b, 16);
        if (c != 0) return c < 0;
        c = std::memcmp(a.id.b, b.id.b, 16);
        return c != 0 ? c < 0 : a.key.subject < b.key.subject;
    });
    for (auto& target : l.cells)
        std::sort(target.groups.begin(), target.groups.end(), [](const period_group& a, const period_group& b) {
            int c = std::memcmp(a.witness.b, b.witness.b, 16);
            if (c != 0) return c < 0;
            if (a.opponent_rating != b.opponent_rating) return a.opponent_rating < b.opponent_rating;
            return a.phi < b.phi;
        });
    l.cell_keys.reset();
    for (const auto& target : l.cells) {
        l.cell_keys.row(3);
        l.cell_keys.field_bytes(target.id.b, 16);
        l.cell_keys.field_bytes(target.key.subject.b, 16);
        l.cell_keys.field_bytes(target.key.type.b, 16);
    }
    l.cell_keys.close();
}

// One Glicko-2 rating period over grouped observations, exactly as the consensus
// fold applies an evidence delta: a single group through the uniform-period
// kernel, several through the grouped-period kernel.
void fold_period(laplace_source_reduce* r, glicko2_state_t& st, const period_group* groups, size_t n) {
    if (n == 1) {
        fold_memo_key key{{st.rating, st.rd, st.volatility, groups[0].opponent_rating,
                           groups[0].phi, groups[0].games, groups[0].sum}};
        auto it = r->memo.find(key);
        if (it != r->memo.end()) { st = it->second; return; }
        if (glicko2_fold_uniform_period(&st, groups[0].opponent_rating, groups[0].phi,
                groups[0].games, groups[0].sum, LAPLACE_GLICKO2_DEFAULT_TAU, 0) != 0)
            throw fail("native rating-period update failed");
        r->memo.emplace(key, st);
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

struct prior_state { int64_t rating, rd, volatility; };

void fold(laplace_source_reduce* r, uint32_t lane, const uint8_t* p, size_t n) {
    lane_state& l = r->lanes[lane];
    std::unordered_map<key16, prior_state, key16_hash> priors;
    copy_reader reader(p, n);
    for (int fields; (fields = reader.next_row()) >= 0;) {
        if (fields != 5) throw fail("prior standing stream must carry id, subject_id, rating, rd, volatility");
        const uint8_t* v = nullptr;
        int32_t len = reader.field(&v);
        const key16 id = key_of(v, len, "consensus id");
        reader.field(&v);  // subject: the cell id already binds it
        prior_state s;
        len = reader.field(&v); s.rating = int8_field(v, len, "prior rating");
        len = reader.field(&v); s.rd = int8_field(v, len, "prior rd");
        len = reader.field(&v); s.volatility = int8_field(v, len, "prior volatility");
        if (!priors.emplace(id, s).second) throw fail("prior standing stream repeats a cell");
    }
    l.consensus.reset();
    l.folded_cells = 0;
    l.folded_games = 0;
    l.standing.reset();
    for (const auto& target : l.cells) {
        glicko2_state_t st;
        auto prior = priors.find(target.id);
        const bool matched = prior != priors.end();
        if (matched) glicko2_init(&st, prior->second.rating, prior->second.rd, prior->second.volatility);
        else glicko2_init(&st, kNeutralMu, kInitialRd, kInitialVolatility);
        // One rating period per witness, witnesses in id order.
        size_t begin = 0;
        while (begin < target.groups.size()) {
            size_t end = begin + 1;
            while (end < target.groups.size() && target.groups[end].witness == target.groups[begin].witness) ++end;
            fold_period(r, st, target.groups.data() + begin, end - begin);
            begin = end;
        }
        // Novel cells are complete consensus rows; cells with prior standing carry the
        // folded state plus this source's games, which PostgreSQL adds to the stored count.
        copy_stream& out = matched ? l.standing : l.consensus;
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
        l.folded_games += static_cast<uint64_t>(target.games);
        ++l.folded_cells;
    }
    l.consensus.close();
    l.standing.close();
}

template <class F>
int guarded(laplace_source_reduce* r, F&& body) {
    if (!r) return -1;
    try {
        body();
        return 0;
    } catch (const std::bad_alloc&) {
        r->error = "source reduction exhausted memory";
        return -2;
    } catch (const std::exception& e) {
        r->error = e.what();
        return -1;
    }
}

void require_lane(const laplace_source_reduce* r, uint32_t lane) {
    if (!r->routed) throw fail("source reduction has not been routed to lanes");
    if (lane >= r->lanes.size()) throw fail("lane is out of range");
}

}  // namespace

extern "C" {

laplace_source_reduce_t* laplace_source_reduce_new(void) {
    return new (std::nothrow) laplace_source_reduce();
}

void laplace_source_reduce_free(laplace_source_reduce_t* reducer) { delete reducer; }

const char* laplace_source_reduce_error(const laplace_source_reduce_t* reducer) {
    return reducer ? reducer->error.c_str() : "no source reducer";
}

int laplace_source_reduce_add_stage(laplace_source_reduce_t* r, const intent_stage_t* stage) {
    return guarded(r, [&] {
        if (!stage) throw fail("no stage");
        if (r->routed) throw fail("source reduction is already routed");
        size_t n = 0;
        const uint8_t* p = intent_stage_tuple_ptr(stage, INTENT_STAGE_TABLE_ENTITIES, &n);
        if (p && n) add_entities(r, p, n);
        p = intent_stage_tuple_ptr(stage, INTENT_STAGE_TABLE_PHYSICALITIES, &n);
        if (p && n) add_physicalities(r, p, n);
        p = intent_stage_tuple_ptr(stage, INTENT_STAGE_TABLE_ATTESTATIONS, &n);
        if (p && n) add_claims(r, p, n);
    });
}

int laplace_source_reduce_exclude_fold_types(laplace_source_reduce_t* r, const hash128_t* type_ids, size_t count) {
    return guarded(r, [&] {
        if (count && !type_ids) throw fail("no excluded types");
        for (size_t i = 0; i < count; ++i) {
            key16 k;
            std::memcpy(k.b, &type_ids[i], 16);
            r->excluded_types.insert(k);
        }
    });
}

int laplace_source_reduce_partitions(laplace_source_reduce_t* r, uint32_t entity_modulus,
                                     uint32_t physicality_modulus, uint32_t claim_modulus) {
    return guarded(r, [&] {
        if (r->routed) throw fail("source reduction is already routed");
        r->modulus[0] = entity_modulus;
        r->modulus[1] = physicality_modulus;
        r->modulus[2] = claim_modulus;
    });
}

int laplace_source_reduce_route(laplace_source_reduce_t* r, uint32_t lanes) {
    return guarded(r, [&] {
        if (r->routed) throw fail("source reduction is already routed");
        if (lanes == 0) throw fail("at least one lane is required");
        route(r, lanes);
    });
}

void laplace_source_reduce_stats(const laplace_source_reduce_t* r, laplace_source_reduce_stats_t* out) {
    if (!out) return;
    if (!r) { std::memset(out, 0, sizeof(*out)); return; }
    *out = r->stats;
    out->entities = r->entities.size();
    out->physicalities = r->physicalities.size();
    out->attestations = r->claims.size();
    out->bytes = r->arena.size() + r->claims.size() * sizeof(claim);
    out->observations = 0;
    out->cells = 0;
    for (const auto& lane : r->lanes) {
        out->observations += lane.folded_games;
        out->cells += lane.folded_cells;
    }
}

const uint8_t* laplace_source_reduce_stream(laplace_source_reduce_t* r, laplace_source_reduce_stream_t stream,
                                            uint32_t lane, size_t* out_len, uint64_t* rows) {
    const uint8_t* result = nullptr;
    size_t length = 0;
    uint64_t count = 0;
    const int rc = guarded(r, [&] {
        require_lane(r, lane);
        lane_state& l = r->lanes[lane];
        copy_stream* s = nullptr;
        switch (stream) {
            case LAPLACE_SOURCE_REDUCE_ENTITIES: s = &l.entities; break;
            case LAPLACE_SOURCE_REDUCE_PHYSICALITIES: s = &l.physicalities; break;
            case LAPLACE_SOURCE_REDUCE_ATTESTATIONS: s = &l.attestations; break;
            case LAPLACE_SOURCE_REDUCE_CELL_KEYS: s = &l.cell_keys; break;
            case LAPLACE_SOURCE_REDUCE_CONSENSUS: s = &l.consensus; break;
            case LAPLACE_SOURCE_REDUCE_STANDING: s = &l.standing; break;
            case LAPLACE_SOURCE_REDUCE_MASKS: s = &l.masks; break;
            default: throw fail("unknown source-reduce stream");
        }
        if (!s->closed) throw fail("stream has not been built for this lane");
        result = s->bytes.data();
        length = s->bytes.size();
        count = s->rows;
    });
    if (out_len) *out_len = rc == 0 ? length : 0;
    if (rows) *rows = rc == 0 ? count : 0;
    return rc == 0 ? result : nullptr;
}

int laplace_source_reduce_admit(laplace_source_reduce_t* r, uint32_t lane, const uint8_t* novel, size_t len) {
    return guarded(r, [&] {
        require_lane(r, lane);
        if (len && !novel) throw fail("no admitted-claim stream");
        admit(r, lane, novel, len);
    });
}

int laplace_source_reduce_cells(laplace_source_reduce_t* r, uint32_t lane, uint64_t* cells) {
    return guarded(r, [&] {
        require_lane(r, lane);
        build_cells(r, lane);
        if (cells) *cells = r->lanes[lane].cells.size();
    });
}

int laplace_source_reduce_fold(laplace_source_reduce_t* r, uint32_t lane, const uint8_t* priors, size_t len) {
    return guarded(r, [&] {
        require_lane(r, lane);
        if (len && !priors) throw fail("no prior standing stream");
        fold(r, lane, priors, len);
    });
}

int laplace_source_reduce_masks(laplace_source_reduce_t* r, const hash128_t** unresolved_pairs,
                                size_t* unresolved_count) {
    return guarded(r, [&] {
        if (!r->routed) throw fail("source reduction has not been routed to lanes");
        if (unresolved_pairs) *unresolved_pairs = r->unresolved.empty() ? nullptr : r->unresolved.data();
        if (unresolved_count) *unresolved_count = r->unresolved.size() / 2;
    });
}

}  // extern "C"
