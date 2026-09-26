#ifndef LAPLACE_OBSERVATION_READ_H
#define LAPLACE_OBSERVATION_READ_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* One attestation row: attributable testimony about a typed triple, not the
 * folded consensus standing of that triple. */
typedef struct LaplaceObservation
{
    hash128_t id, subject, type, object, source, context;
    bool object_null, source_null, context_null;
    int16 outcome;
    int64 occurrences;
    /* The claim's governed qualifier bits (qualifier_law); zero when the
     * witness states none. */
    uint8 qualifiers[32];
} LaplaceObservation;

/* Whether the witness's qualifiers carry family/value ("derivation/calculation"). */
bool laplace_observation_qualified(const LaplaceObservation *row, const char *family, const char *value);

/* Set read of the attestation face for an operand id set, optionally limited
 * to sources and relation types. ordinal is the original 1-based operand
 * occurrence; duplicate operands share one probe but every occurrence is
 * visited. The callback borrows the row only for its duration. NULL filters
 * mean unrestricted; empty filters mean no witnesses.
 * Roles preserve the stored proposition: 1 = subject, 2 = object. An inbound
 * binding does not reverse an asymmetric assertion. A self-reference is
 * visited in both roles from one fetched witness. */
typedef void (*LaplaceObservationVisitor)(int ordinal, int16 role,
    const LaplaceObservation *observation, void *context);
void laplace_observation_read(ArrayType *operands, ArrayType *sources,
    ArrayType *types, int roles, LaplaceObservationVisitor visitor, void *context);

typedef struct LaplaceObservationCell
{
    hash128_t subject, type, object;
} LaplaceObservationCell;

/* Witnesses of exact (subject, type, object) cells, for reading the testimony
 * behind already-elected candidates. Every operand occurrence and both stored
 * endpoint roles are visited; duplicate cells share one read, and witnesses of
 * other cells at the same endpoints are never fetched. */
void laplace_observation_read_cells(ArrayType *operands, ArrayType *sources,
    const LaplaceObservationCell *cells, int cell_count,
    LaplaceObservationVisitor visitor, void *context);

#endif
