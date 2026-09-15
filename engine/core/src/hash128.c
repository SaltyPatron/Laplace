#include "laplace/core/hash128.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"

#include <string.h>

#include "blake3.h"

static const uint8_t MERKLE_DOMAIN = 0x01;

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out) {
    blake3_hasher h;
    blake3_hasher_init(&h);
    if (data && len > 0) {
        blake3_hasher_update(&h, data, len);
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out) {
    /*
     * CONTENT-ADDRESSING LAW: same content = same hash. The id is a function
     * of the child-id sequence and nothing else — no tier, no ordinal, no
     * container. Tier is a FLOOR, not identity: entities.tier records the
     * lowest form of the content; hash_composer collapses single-child nodes to
     * the child id for exactly this reason.
     */
    (void)tier;
    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));
    if (children && n > 0) {
        blake3_hasher_update(&h, children, n * sizeof(hash128_t));
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

int hash128_merkle_runs(size_t child_count, hash128_run_reader_t reader,
                        void* context, hash128_t* out) {
    if (!reader || !out || child_count < 2) return -1;

    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));

    size_t seen = 0;
    while (seen < child_count) {
        hash128_t child;
        size_t run = 0;
        int rc = reader(context, &child, &run);
        if (rc != 0 || run == 0 || run > child_count - seen) return -1;

        hash128_t block[64];
        for (size_t i = 0; i < 64; ++i) block[i] = child;
        size_t remaining = run;
        while (remaining > 0) {
            size_t take = remaining < 64 ? remaining : 64;
            blake3_hasher_update(&h, block, take * sizeof(hash128_t));
            remaining -= take;
        }
        seen += run;
    }

    hash128_t extra;
    size_t extra_run = 0;
    if (reader(context, &extra, &extra_run) != 1) return -1;

    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
    return 0;
}

typedef struct {
    const double* xyzm;
    size_t points;
    size_t index;
} trajectory_merkle_reader_t;

static int trajectory_next_merkle_run(void* context, hash128_t* child, size_t* run) {
    trajectory_merkle_reader_t* reader = (trajectory_merkle_reader_t*)context;
    if (!reader || !child || !run) return -1;
    if (reader->index >= reader->points) return 1;

    mantissa_payload_t payload;
    mantissa_unpack(&reader->xyzm[reader->index * 4], &payload);
    reader->index++;
    *child = payload.entity_id;
    *run = payload.run_length ? (size_t)payload.run_length : (size_t)1;
    return 0;
}

int trajectory_content_identity(const double* trajectory_xyzm,
                                size_t n_points,
                                hash128_t* out_id,
                                size_t* out_count) {
    if (!out_id || !out_count || !trajectory_xyzm || n_points == 0) return -1;

    size_t count = 0;
    if (trajectory_constituent_count(trajectory_xyzm, n_points, &count) != 0
        || count == 0) return -1;
    *out_count = count;

    if (count == 1) {
        if (n_points != 1) return -1;
        mantissa_payload_t payload;
        mantissa_unpack(trajectory_xyzm, &payload);
        *out_id = payload.entity_id;
        return 0;
    }

    trajectory_merkle_reader_t reader = {
        .xyzm = trajectory_xyzm,
        .points = n_points,
        .index = 0,
    };
    return hash128_merkle_runs(count, trajectory_next_merkle_run, &reader, out_id);
}

int hash128_compare(const hash128_t* a, const hash128_t* b) {
    return memcmp(a, b, sizeof(hash128_t));
}

int hash128_equals(const hash128_t* a, const hash128_t* b) {
    return memcmp(a, b, sizeof(hash128_t)) == 0;
}

void hash128_zero(hash128_t* out) {
    memset(out, 0, sizeof(*out));
}
