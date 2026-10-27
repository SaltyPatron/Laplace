#include "laplace/core/hash128.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/text_identity.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "blake3.h"
#include "laplace/laplace.h"   /* Laplace-Native: lp_id_compose, the one implementation of a composition's ID */

_Static_assert(sizeof(hash128_t) == sizeof(lp_id), "a hash128_t is an lp_id: 16 bytes");

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
    /* Native owns every arity, including the one-child collapse. Public C ABI
     * callers must receive the same identity as the higher-level composer. */
    lp_id_compose((const lp_id*)children, n, (lp_id*)out);
}

typedef struct { hash128_run_reader_t next; void *context; } HashRuns;
static int hash_run(void *context, lp_id *child, uint64_t *run) {
    HashRuns *input = context;
    hash128_t id; size_t copies = 0;
    int result = input->next(input->context, &id, &copies);
    if (result == 0) { memcpy(child, &id, sizeof *child); *run = copies; }
    return result;
}
int hash128_merkle_runs(size_t child_count, hash128_run_reader_t reader,
                        void* context, hash128_t* out) {
    if (!reader || !out || child_count < 2) return -1;
    HashRuns input = {reader, context};
    lp_id id;
    if (!lp_id_compose_runs(child_count, hash_run, &input, &id)) return -1;
    memcpy(out, &id, sizeof *out);
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

int hash128_label_content_id(const char* label, size_t len, hash128_t* out) {
    return hash128_text((const uint8_t *)label, len, out);
}
