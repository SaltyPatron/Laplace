#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/intent_stage.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The existing (entity,type) physicality address selects a placement. This
 * descriptor is ordinary canonical content describing one immutable body at
 * that address. It never changes the identity of the realized entity.
 *
 * Canonical references remain references: each ordinary/testimony carrier has
 * its actual constituent entity as a child. Factor vertices carry numeric
 * payload, so their packed id channel is not interpreted as an entity.
 *
 * The plan contains identity and ordered structure only. Materialization must
 * supply a pinned, verified physicality for every reference occurrence. A
 * current reference placement is not a historical child-curve witness. */
typedef enum {
    PHYSICALITY_DESCRIPTOR_SCHEMA,
    PHYSICALITY_DESCRIPTOR_COORDINATE,
    PHYSICALITY_DESCRIPTOR_HILBERT,
    PHYSICALITY_DESCRIPTOR_TRAJECTORY,
    PHYSICALITY_DESCRIPTOR_CARRIER,
    PHYSICALITY_DESCRIPTOR_FACTOR,
    PHYSICALITY_DESCRIPTOR_ABSENT,
    PHYSICALITY_DESCRIPTOR_PRESENT,
    PHYSICALITY_DESCRIPTOR_I16,
    PHYSICALITY_DESCRIPTOR_I32,
    PHYSICALITY_DESCRIPTOR_U16,
    PHYSICALITY_DESCRIPTOR_U64,
    PHYSICALITY_DESCRIPTOR_BINARY64,
    PHYSICALITY_DESCRIPTOR_TAG_COUNT
} physicality_descriptor_tag_t;

/* Values are the canonical content identities of decimal numbers 0..255,
 * supplied by the shared content/number owner, not a new byte atom alphabet.
 * Tags are canonical content identities of the declared schema vocabulary.
 * The caller retains the vocabulary's source/floor receipt. */
typedef struct {
    hash128_t tags[PHYSICALITY_DESCRIPTOR_TAG_COUNT];
    hash128_t byte_numbers[256];
} physicality_descriptor_basis_t;

/* Reject an aliased vocabulary before exact bit fields can lose information.
 * This checks the provider's shape, not its external floor/source authenticity. */
int physicality_descriptor_basis_is_valid(const physicality_descriptor_basis_t* basis);

typedef struct {
    hash128_t entity_id;
    int16_t type;
    double coord[4];
    hilbert128_t hilbert_index;
    const double* trajectory_xyzm;
    size_t trajectory_vertices;
    int32_t n_constituents;
    int alignment_residual_is_null;
    double alignment_residual;
    int source_dim_is_null;
    int32_t source_dim;
} physicality_descriptor_input_t;

typedef struct {
    /* Maximum live owned plan payload, including old plus replacement arrays
     * during growth. Excludes borrowed input/vocabulary and allocator
     * bookkeeping; this is not a process RSS measurement. */
    size_t maximum_plan_bytes;
} physicality_descriptor_limits_t;

typedef struct {
    hash128_t id;
    size_t first_child;
    size_t child_count;
} physicality_descriptor_node_t;

typedef enum {
    PHYSICALITY_DESCRIPTOR_REALIZED_ENTITY = 1,
    PHYSICALITY_DESCRIPTOR_CARRIER_ENTITY = 2
} physicality_descriptor_reference_kind_t;

typedef struct {
    hash128_t entity_id;
    size_t input_index;
    /* SIZE_MAX for the realized entity; otherwise exact stored vertex index.
     * Every occurrence survives even when ids or canonical subtrees repeat. */
    size_t vertex_index;
    physicality_descriptor_reference_kind_t kind;
} physicality_descriptor_reference_t;

typedef struct physicality_descriptor_plan physicality_descriptor_plan_t;

/* Exact descriptor manifests retain their ordinary entity identities while
 * their storage coordinates use the declared literal-identifier projection.
 * They are not the referenced entities' selected Content geometry. */
enum { PHYSICALITY_DESCRIPTOR_RETENTION_TYPE = 9 };

typedef enum {
    PHYSICALITY_DESCRIPTOR_OK = 0,
    PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER = 1,
    PHYSICALITY_DESCRIPTOR_INVALID = -1,
    PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED = -2,
    PHYSICALITY_DESCRIPTOR_INVALID_BODY = -3,
    PHYSICALITY_DESCRIPTOR_IDENTITY_CONFLICT = -4,
    PHYSICALITY_DESCRIPTOR_MISSING_FLOOR = -5,
    PHYSICALITY_DESCRIPTOR_MISSING_REFERENCE = -6,
    PHYSICALITY_DESCRIPTOR_CANCELLED = -7
} physicality_descriptor_status_t;

/* Optional, caller-owned cancellation for synchronous native operations. The
 * callback runs only on the calling thread, must return normally without
 * throwing/longjmp, and must remain nonzero once cancellation is requested.
 * Core owners unwind normally and return CANCELLED with no published output.
 * Checkpoints occur between rows/nodes/closure steps; individual allocator,
 * hash and geometry calls are not forcibly interruptible. NULL preserves the
 * existing operation. No callback or context is retained by a returned object. */
typedef struct {
    int (*requested)(void* context);
    void* context;
} physicality_descriptor_cancel_t;

static inline int physicality_descriptor_cancel_requested(
    const physicality_descriptor_cancel_t* cancellation) {
    return cancellation != NULL && cancellation->requested != NULL &&
        cancellation->requested(cancellation->context) != 0;
}

physicality_descriptor_status_t physicality_descriptor_plan_build_cancelable(
    const physicality_descriptor_input_t* inputs, size_t input_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* limits,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_plan_t** out_plan);

physicality_descriptor_status_t physicality_descriptor_plan_build(
    const physicality_descriptor_input_t* inputs, size_t input_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* limits,
    physicality_descriptor_plan_t** out_plan);
void physicality_descriptor_plan_free(physicality_descriptor_plan_t* plan);

size_t physicality_descriptor_plan_bytes(const physicality_descriptor_plan_t* plan);
/* Conservative high-water payload reservation, including old plus requested
 * replacement arrays even when realloc grows in place. Not allocator RSS;
 * plan_bytes reports retained payload only. */
size_t physicality_descriptor_plan_peak_bytes(const physicality_descriptor_plan_t* plan);
const physicality_descriptor_node_t* physicality_descriptor_plan_nodes(
    const physicality_descriptor_plan_t* plan, size_t* count);
const hash128_t* physicality_descriptor_plan_children(
    const physicality_descriptor_plan_t* plan, size_t* count);
const hash128_t* physicality_descriptor_plan_roots(
    const physicality_descriptor_plan_t* plan, size_t* count);
const physicality_descriptor_reference_t* physicality_descriptor_plan_references(
    const physicality_descriptor_plan_t* plan, size_t* count);

/* Shared producer capture boundary: read the actual native stage tuples before
 * placement-address dedup. All distinct bodies and observation occurrences
 * survive. This does not deposit rows or imply that referenced physicalities
 * have been hydrated. The caller supplies a finite admitted source-stage set;
 * generated view stages belong to their explicit derivation and are not fed
 * back as new source observations. */
typedef struct physicality_descriptor_capture physicality_descriptor_capture_t;

typedef struct {
    hash128_t placement_id;
    size_t source_stage_index;
    size_t source_row_index;
    int64_t observed_at_unix_us;
} physicality_descriptor_observation_t;

/* Plan-free bounded transport of native stage rows. Shares the capture decoder:
 * exact COPY framing, scalar/EWKB layouts and placement IDs are checked; all
 * occurrence bodies and timestamps are retained. This does not construct or
 * validate a descriptor plan, deposit rows, or establish source evidence. The
 * returned capture_plan is NULL. Use capture_stages for descriptor admission. */
physicality_descriptor_status_t physicality_descriptor_capture_stage_rows(
    const intent_stage_t* const* stages, size_t stage_count,
    size_t maximum_capture_bytes, physicality_descriptor_capture_t** out_capture);

physicality_descriptor_status_t physicality_descriptor_capture_stages(
    const intent_stage_t* const* stages, size_t stage_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_capture_bytes,
    physicality_descriptor_capture_t** out_capture);
physicality_descriptor_status_t physicality_descriptor_capture_stages_cancelable(
    const intent_stage_t* const* stages, size_t stage_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_capture_bytes,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_capture_t** out_capture);
void physicality_descriptor_capture_free(physicality_descriptor_capture_t* capture);
/* Exclusive-owner lifetime operation after capture_stages has fully validated
 * every input. Frees only the plan and returns its exact retained payload bytes.
 * Inputs, decoded trajectories and observations remain valid until capture_free;
 * all borrowed plan pointers become invalid and capture_plan returns NULL.
 * Null captures and repeated calls return zero. The retained byte count shrinks;
 * the historical capture peak is unchanged. This does not refund validation
 * work or reduce the peak needed to construct the original capture. */
size_t physicality_descriptor_capture_release_plan(physicality_descriptor_capture_t* capture);
/* Total retained capture + any still-owned plan allocation; maximum_capture_bytes caps
 * this combined allocation. Borrowed stages and allocator bookkeeping excluded. */
size_t physicality_descriptor_capture_bytes(const physicality_descriptor_capture_t* capture);
/* Peak capture plus owned plan payload, including transient plan growth;
 * borrowed stages and allocator bookkeeping are excluded. */
size_t physicality_descriptor_capture_peak_bytes(const physicality_descriptor_capture_t* capture);
const physicality_descriptor_plan_t* physicality_descriptor_capture_plan(
    const physicality_descriptor_capture_t* capture);
const physicality_descriptor_input_t* physicality_descriptor_capture_inputs(
    const physicality_descriptor_capture_t* capture, size_t* count);
const physicality_descriptor_observation_t* physicality_descriptor_capture_observations(
    const physicality_descriptor_capture_t* capture, size_t* count);

/* A bulk reader supplies hydrated ordinary composition records. This native
 * decoder checks every supplied identity and each typed field, then reconstructs
 * exact bodies. A GIN containment hit on schema + E is only a candidate until
 * this validation succeeds. Missing child records fail; they are never inferred
 * from current coordinates or replaced by opaque id bytes. */
typedef struct physicality_descriptor_readback physicality_descriptor_readback_t;
/* Shape/authenticity filter for indexed containment candidates. A successful
 * root still requires complete typed readback before it is an admitted body. */
int physicality_descriptor_readback_root_entity(
    const physicality_descriptor_node_t* node, const hash128_t* children,
    size_t child_count, const physicality_descriptor_basis_t* basis,
    hash128_t* out_entity);

/* The same typed decoder in discovery mode. NEEDS_PROVIDER returns only sorted
 * unique missing typed-node identities: no body buffer is executable. Realized
 * E, carrier E, tags and exact numeric vocabulary are leaves. The caller may
 * hydrate that entire frontier and retry under its cumulative work grant.
 * OK performs the existing full decoder and planner verification. The content
 * hash-operand grant is checked against decoded Content manifests before that
 * replan can expand RLE runs; it is independent of the byte grant. */
physicality_descriptor_status_t physicality_descriptor_readback_prepare(
    const physicality_descriptor_node_t* nodes, size_t node_count,
    const hash128_t* children, size_t child_count,
    const hash128_t* roots, size_t root_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_readback_bytes,
    size_t maximum_content_hash_operands,
    physicality_descriptor_readback_t** out_readback);
const hash128_t* physicality_descriptor_readback_missing(
    const physicality_descriptor_readback_t* readback, size_t* count);
size_t physicality_descriptor_readback_content_hash_operands(
    const physicality_descriptor_readback_t* readback);
physicality_descriptor_status_t physicality_descriptor_readback_build(
    const physicality_descriptor_node_t* nodes, size_t node_count,
    const hash128_t* children, size_t child_count,
    const hash128_t* roots, size_t root_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_readback_bytes,
    physicality_descriptor_readback_t** out_readback);
void physicality_descriptor_readback_free(physicality_descriptor_readback_t* readback);
/* Retained decoded inputs, and peak decoder + temporary verification plan.
 * maximum_readback_bytes caps the peak; allocator bookkeeping is excluded. */
size_t physicality_descriptor_readback_bytes(const physicality_descriptor_readback_t* readback);
size_t physicality_descriptor_readback_peak_bytes(const physicality_descriptor_readback_t* readback);
const physicality_descriptor_input_t* physicality_descriptor_readback_inputs(
    const physicality_descriptor_readback_t* readback, size_t* count);

#ifdef __cplusplus
}
#endif
