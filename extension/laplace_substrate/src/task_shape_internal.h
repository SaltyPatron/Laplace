#ifndef LAPLACE_SUBSTRATE_TASK_SHAPE_INTERNAL_H
#define LAPLACE_SUBSTRATE_TASK_SHAPE_INTERNAL_H

#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "miscadmin.h"
#include "parser/parse_func.h"
#include "lib/stringinfo.h"
#include "utils/lsyscache.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/task_shape.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"
#include "content_membership_read.h"
#include "content_trajectory_read.h"
#include "prompt_intent.h"
#include "prompt_structure.h"
#include "task_shape.h"
#include "trajectory_wkb.h"

typedef struct ShapeStructure
{
    hash128_t id;
    hash128_t *flat;
    size_t length;
    laplace_ud_parse_t parse;
    laplace_task_shape_view_t shape;
    bool is_parse, is_shape;
    MemoryContextCallback cleanup;
} ShapeStructure;

typedef struct ShapeCell
{
    LaplaceObservationCell key;
    bool positive;
} ShapeCell;

typedef struct ShapeEntityType
{
    hash128_t id, type;
} ShapeEntityType;

typedef struct ShapeRead
{
    LaplacePromptIntent *intent;
    MemoryContext owner;
    HTAB *structures, *witnesses, *cells, *entity_types;
    hash128_t example_type, call_type, input_type, parse_type;
    Oid as_binary;
    int fanout;
    bool failed, vertex_only;
} ShapeRead;

typedef struct ShapeConstituents
{
    hash128_t *ids;
    uint64_t *flags;
    size_t count;
} ShapeConstituents;

typedef struct SlotChoices
{
    hash128_t *ids;
    int count;
    int origin;
} SlotChoices;

HTAB *shape_table(const char *name, Size key, Size entry, MemoryContext owner);
void shape_receive_structure(Datum physicality, Datum entity, Datum geometry, void *opaque);
void shape_receive_witness(int ordinal, int16 role, const LaplaceObservation *row, void *opaque);
void shape_receive_standing(const LaplaceConsensusRow *row, void *opaque);
bool shape_positive(const ShapeRead *read, const LaplaceObservation *row,
                    const LaplaceObservation *rows, int count);
const LaplaceObservation *shape_parse_witness(const ShapeRead *read, hash128_t parse,
                                              const LaplaceObservation *rows, int count);
const LaplaceObservation *shape_current_witness(const LaplacePromptParse *parse);
ArrayType *shape_structure_ids(ShapeRead *read, bool parses);
void shape_read_entity_types(ShapeRead *read);

#endif
