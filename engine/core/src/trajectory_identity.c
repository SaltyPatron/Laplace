#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"

#include <stddef.h>

/* Stream one stored RLE vertex at a time into hash128_merkle_runs. The hash
 * layer owns the Merkle domain bytes; this layer owns only trajectory decoding,
 * so there is still one executable definition of content identity. */
typedef struct {
    const double* xyzm;
    size_t points;
    size_t index;
} trajectory_merkle_reader_t;

static int trajectory_next_run(void* context, hash128_t* child, size_t* run) {
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
    return hash128_merkle_runs(count, trajectory_next_run, &reader, out_id);
}
