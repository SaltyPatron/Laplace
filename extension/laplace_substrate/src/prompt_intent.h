#ifndef LAPLACE_PROMPT_INTENT_H
#define LAPLACE_PROMPT_INTENT_H

#include "postgres.h"

#include <math.h>

#include "nodes/bitmapset.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "laplace/core/hash128.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/relation_law.h"
#include "prompt_input.h"
#include "prompt_structure.h"
#include "trajectory_continuations.h"
#include "observation_read.h"
#include "consensus_scan.h"
#include "laplace/core/attestation_engine.h"
#include "query_evidence.h"
#include "relation_symmetry.h"
#include "spi_common.h"
#include "walk_score.h"

/* An identity bound to the current observation, with the ordinals of the
 * current occurrences it is reached from. Discourse identities never gain
 * origins here; they stay in their own response plane. */
typedef struct LaplacePromptIntentBinding
{
    hash128_t id;
    Bitmapset *origins;
} LaplacePromptIntentBinding;

typedef struct LaplacePromptOperand
{
    hash128_t id;
    hash128_t witness;
    Bitmapset *origins;
    int requirement; /* distinct-input ordinal, not an occurrence ordinal */
} LaplacePromptOperand;

/* One invocation compiled from witnessed CALLS and HAS_INPUT declarations on
 * the whole observation: the result relation, the declaring source and context,
 * and each input operand with the occurrence origins that bind it. */
typedef struct LaplacePromptRelationRead
{
    hash128_t result_relation, source, context, call_witness;
    hash128_t shape_id, exemplar_parse, current_parse;
    hash128_t applicability_witness, parse_witness, exemplar_parse_witness;
    LaplacePromptOperand *inputs;
    int input_count;
    Bitmapset *operand_origins;
} LaplacePromptRelationRead;

typedef enum LaplacePromptGeometryPlane
{
    LAPLACE_PROMPT_GEOMETRY_HILBERT = 1,
    LAPLACE_PROMPT_GEOMETRY_ANGULAR = 2,
    LAPLACE_PROMPT_GEOMETRY_FRECHET = 3
} LaplacePromptGeometryPlane;

/* Geometry is deterministic response state, not testimony.  Hilbert retains an
 * exact 128-bit scalar delta; angular/Frechet retain their native metric. */
typedef struct LaplacePromptGeometryCandidate
{
    hash128_t source;
    hash128_t id;
    uint32 plane;
    uint32 rank;
    uint8 hilbert_delta[16];
    double distance;
} LaplacePromptGeometryCandidate;

typedef struct LaplacePromptHilbertCandidate
{
    hash128_t id;
    uint8 delta[16];
} LaplacePromptHilbertCandidate;

typedef struct LaplacePromptIntent
{
    MemoryContext owner;
    const LaplacePromptInput *input;
    ArrayType *hard_relation_types;
    HTAB *bindings;
    hash128_t root;
    hash128_t active_context;
    LaplacePromptRelationRead *operations;
    int relation_count;
    int relation_capacity;
    bool ambiguous;
    bool explicit_invocation;
    bool budget_exhausted;
    LaplacePromptStructure *structure;
    /* Exact physicality responses are retained as their own typed plane. They
     * may ground prompt-relative bindings without being rewritten as testimony. */
    LaplaceStructuralCandidate *structural;
    int structural_count;
    ArrayType *structural_frontier;
    /* Placement/locality/shape remain a separate deterministic response plane.
     * They may discover responders, but never manufacture testimony or standing. */
    LaplacePromptGeometryCandidate *geometry;
    int geometry_count;
    int geometry_capacity;
    ArrayType *geometry_frontier;
    /* Prior discourse is an ordered response plane of exact admitted turn/content
     * identities.  It remains distinct from current observation occurrences and
     * from testimony even when equal canonical identities appear in both. */
    ArrayType *discourse;
} LaplacePromptIntent;

static inline void
laplace_prompt_intent_bind_discourse(LaplacePromptIntent *intent, ArrayType *discourse)
{
    MemoryContext previous;

    if (!intent)
        ereport(ERROR, (errmsg("prompt intent: discourse binding requires an intent")));
    previous = MemoryContextSwitchTo(intent->owner);
    if (!discourse)
    {
        intent->discourse = construct_empty_array(BYTEAOID);
        MemoryContextSwitchTo(previous);
        return;
    }
    if (ARR_NDIM(discourse) > 1 || ARR_ELEMTYPE(discourse) != BYTEAOID)
        ereport(ERROR, (errmsg("prompt intent: discourse must be a one-dimensional bytea array")));

    ArrayIterator iterator = array_create_iterator(discourse, 0, NULL);
    Datum value;
    bool isnull;
    while (array_iterate(iterator, &value, &isnull))
        if (!isnull && VARSIZE_ANY_EXHDR(DatumGetByteaPP(value)) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("prompt intent: discourse identities must be 16 bytes")));
    array_free_iterator(iterator);

    intent->discourse = DatumGetArrayTypePCopy(PointerGetDatum(discourse));
    MemoryContextSwitchTo(previous);
}

/* A hard relation scope differs from the optional output projection: NULL
 * permits all relations, while an explicitly empty array permits none. */
static inline bool
laplace_prompt_relation_allowed(const LaplacePromptIntent *intent, const hash128_t *relation)
{
    if (!intent->hard_relation_types) return true;
    bool allowed = false;
    ArrayIterator iterator = array_create_iterator(intent->hard_relation_types, 0, NULL);
    Datum value;
    bool isnull;
    while (array_iterate(iterator, &value, &isnull))
    {
        if (isnull) continue;
        hash128_t candidate = datum_to_hash128(value);
        if (hash128_eq(&candidate, relation)) { allowed = true; break; }
    }
    array_free_iterator(iterator);
    return allowed;
}

static inline const Bitmapset *
laplace_prompt_intent_origins(const LaplacePromptIntent *intent,
                              const hash128_t *id)
{
    LaplacePromptIntentBinding *binding;
    if (!intent || !intent->bindings) return NULL;
    binding = hash_search(intent->bindings, id, HASH_FIND, NULL);
    return binding ? binding->origins : NULL;
}

static inline const LaplacePromptOperand *
laplace_prompt_operation_input(const LaplacePromptRelationRead *operation,
                                const hash128_t *id)
{
    for (int i = 0; i < operation->input_count; ++i)
        if (hash128_eq(&operation->inputs[i].id, id))
            return &operation->inputs[i];
    return NULL;
}

static inline const LaplacePromptRelationRead *
laplace_prompt_intent_bound_operation(const LaplacePromptIntent *intent,
                                      const LaplaceQueryChannel *channel)
{
    if (intent)
        for (int i = 0; i < intent->relation_count; ++i)
        {
            const LaplacePromptRelationRead *operation = &intent->operations[i];
            if (hash128_eq(&operation->result_relation, &channel->relation) &&
                laplace_prompt_operation_input(operation, &channel->anchor))
                return operation;
        }
    return NULL;
}

static inline bool
laplace_prompt_same_inputs(const LaplacePromptRelationRead *a,
                           const LaplacePromptRelationRead *b)
{
    for (int i = 0; i < a->input_count; ++i)
        if (!laplace_prompt_operation_input(b, &a->inputs[i].id)) return false;
    for (int i = 0; i < b->input_count; ++i)
        if (!laplace_prompt_operation_input(a, &b->inputs[i].id)) return false;
    return bms_equal(a->operand_origins, b->operand_origins);
}

/* 1 for the CALLS family, 2 for HAS_INPUT, 0 otherwise. These relations declare
 * an invocation; their endpoints are not result transitions. */
static inline int
laplace_prompt_contract_relation(const hash128_t *relation)
{
    int member = 0;
    laplace_relation_in_family(relation, "CALLS", &member);
    if (member) return 1;
    laplace_relation_in_family(relation, "HAS_INPUT", &member);
    return member ? 2 : 0;
}

/* A binding channel is a positively standing naming or interpretation cell:
 * the HAS_NAME, HAS_SENSE, IS_LEMMA_OF or EVOKES_FRAME families, or the root
 * CORRESPONDS_TO relation. No other relation binds a candidate to the
 * observation's occurrences, and binding never inverts a result relation. */
static inline bool
laplace_prompt_binding_channel(const LaplaceQueryChannel *channel)
{
    static const char *families[] = {
        "HAS_NAME", "HAS_SENSE", "IS_LEMMA_OF", "EVOKES_FRAME"
    };
    int member = 0;
    if (!(laplace_walk_edge_weight(channel->rating, channel->rd) > 0.0))
        return false;
    for (size_t i = 0; i < sizeof(families) / sizeof(families[0]); ++i)
    {
        laplace_relation_in_family(&channel->relation, families[i], &member);
        if (member) return true;
    }
    /* Only the governed root correspondence operation names an equivalent
     * concept. Its argument-role descendants preserve a different type. */
    laplace_relation_in_family(&channel->relation, "CORRESPONDS_TO", &member);
    if (member)
    {
        const laplace_relation_def_t *def = NULL;
        return laplace_relation_lookup(&channel->relation, &def) == 0 &&
               def != NULL && def->parent_idx < 0;
    }
    return false;
}

static inline HTAB *
laplace_prompt_binding_table(const char *name, MemoryContext owner)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplacePromptIntentBinding);
    ctl.hcxt = owner;
    return hash_create(name, 128, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

typedef struct LaplacePromptStructuralKey
{
    hash128_t source;
    hash128_t target;
    uint32 relation;
} LaplacePromptStructuralKey;

typedef struct LaplacePromptStructuralIndex
{
    LaplacePromptStructuralKey key;
    int index;
} LaplacePromptStructuralIndex;

static inline LaplacePromptStructuralKey
laplace_prompt_structural_key(const hash128_t *source, const hash128_t *target,
                              uint32 relation)
{
    LaplacePromptStructuralKey key;
    MemSet(&key, 0, sizeof(key));
    key.source = *source;
    key.target = *target;
    key.relation = relation;
    return key;
}

#include "prompt_intent_geometry.h"

/* Merge the ordered trajectory continuations of the observation's occurrences
 * into the structural response as CONTINUATION crossings from the root, summing
 * occurrence counts per continuation. They remain trajectory facts, never
 * testimony. */
static inline int
laplace_prompt_merge_continuations(LaplacePromptIntent *intent,
                                   LaplaceTrajectoryScope *trajectory_scope)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    int continuation_count = 0;
    LaplaceContinuation *continuations = laplace_trajectory_continuations_scoped(
        intent->input->context, false, trajectory_scope, &continuation_count);
    if (continuation_count <= 0)
    {
        if (continuations) pfree(continuations);
        MemoryContextSwitchTo(previous);
        return 0;
    }
    if (continuation_count > INT_MAX - intent->structural_count ||
        (Size)(intent->structural_count + continuation_count) >
            MaxAllocSize / sizeof(LaplaceStructuralCandidate))
        ereport(ERROR,
                (errmsg("prompt intent: ordered continuation response exceeds allocation capacity")));

    HASHCTL ctl = {0};
    ctl.keysize = sizeof(LaplacePromptStructuralKey);
    ctl.entrysize = sizeof(LaplacePromptStructuralIndex);
    ctl.hcxt = intent->owner;
    HTAB *index = hash_create("prompt structural response index",
                              Max(intent->structural_count + continuation_count, 16),
                              &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    intent->structural = intent->structural
        ? repalloc(intent->structural,
                   sizeof(*intent->structural) *
                   (Size)(intent->structural_count + continuation_count))
        : palloc(sizeof(*intent->structural) * (Size) continuation_count);

    for (int i = 0; i < intent->structural_count; ++i)
    {
        LaplacePromptStructuralKey key = laplace_prompt_structural_key(
            &intent->structural[i].source, &intent->structural[i].id,
            intent->structural[i].relation_mask);
        bool found;
        LaplacePromptStructuralIndex *slot =
            hash_search(index, &key, HASH_ENTER, &found);
        if (found)
            elog(ERROR, "prompt intent: duplicate structural route escaped native folding");
        slot->index = i;
    }

    for (int i = 0; i < continuation_count; ++i)
    {
        const LaplaceContinuation *continuation = &continuations[i];
        LaplacePromptStructuralKey key = laplace_prompt_structural_key(
            &intent->root, &continuation->id, LAPLACE_STRUCTURAL_CONTINUATION);
        bool found;
        LaplacePromptStructuralIndex *slot =
            hash_search(index, &key, HASH_ENTER, &found);
        LaplaceStructuralCandidate *target;
        if (found)
            target = &intent->structural[slot->index];
        else
        {
            slot->index = intent->structural_count++;
            target = &intent->structural[slot->index];
            *target = (LaplaceStructuralCandidate) {
                .source = intent->root,
                .id = continuation->id,
                .relation_mask = LAPLACE_STRUCTURAL_CONTINUATION,
                .nearest_gap = 1};
        }
        if (target->relation_mask != LAPLACE_STRUCTURAL_CONTINUATION)
            elog(ERROR, "prompt intent: ordered continuation merged into another structural route");
        if (continuation->occurrences < 0 ||
            target->occurrences > PG_INT64_MAX - continuation->occurrences)
            ereport(ERROR,
                    (errmsg("prompt intent: ordered continuation occurrence count overflow")));
        target->occurrences += continuation->occurrences;
        if (target->nearest_gap > 1) target->nearest_gap = 1;
    }

    hash_destroy(index);
    pfree(continuations);
    MemoryContextSwitchTo(previous);
    return continuation_count;
}

static inline LaplacePromptIntent
laplace_prompt_intent_begin(const LaplacePromptInput *input, MemoryContext owner,
                            ArrayType *relation_types, int fanout,
                            LaplaceTrajectoryScope *trajectory_scope)
{
    LaplacePromptIntent result = {0};
    Datum *values, *nodes;
    bool *nulls, *node_nulls;
    int count, node_count;
    MemoryContext previous = MemoryContextSwitchTo(owner);
    result.owner = owner;
    result.input = input;
    result.hard_relation_types = relation_types;
    result.root = input->root;
    result.bindings = laplace_prompt_binding_table("prompt witnessed bindings", owner);
    result.structure = laplace_prompt_structure_couple(input, relation_types, fanout, owner);
    deconstruct_array(input->context, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    deconstruct_array(input->nodes, INT4OID, 4, true, TYPALIGN_INT,
                      &nodes, &node_nulls, &node_count);
    if (count != node_count)
        elog(ERROR, "prompt intent: occurrence projections disagree");

    /* The whole admitted observation is an exact source too. Give its root the
     * complete current occurrence provenance before any structural response is
     * admitted, rather than reconstructing that ancestry after ORIENT. */
    bool root_found;
    LaplacePromptIntentBinding *root_binding =
        hash_search(result.bindings, &input->root, HASH_ENTER, &root_found);
    if (!root_found) root_binding->origins = NULL;

    for (int i = 0; i < count; ++i)
    {
        LaplacePromptIntentBinding *entry;
        hash128_t id;
        bool found;
        int node;
        if (nulls[i] || node_nulls[i]) continue;
        node = DatumGetInt32(nodes[i]);
        if (node < 0 || (size_t) node >= tier_tree_node_count(input->tree))
            elog(ERROR, "prompt intent: occurrence outside admitted tree");
        id = datum_to_hash128(values[i]);
        entry = hash_search(result.bindings, &id, HASH_ENTER, &found);
        if (!found) entry->origins = NULL;
        entry->origins = bms_add_member(entry->origins, i);
        root_binding->origins = bms_add_member(root_binding->origins, i);
    }

    /* Physicality couples before ORIENT. One trajectory scope yields container,
     * constituent, predecessor, successor and co-occurrence crossings from the
     * root and every current occurrence, plus ordered continuation. Crossings
     * stay typed in `structural`; identities they first reach join the bindings
     * with their sources' origins and form the structural frontier. No
     * crossing becomes testimony. */
    result.structural_frontier = construct_empty_array(BYTEAOID);
    if (trajectory_scope)
    {
        Datum root_value = hash128_to_datum(&input->root);
        Datum *source_values = palloc(sizeof(Datum) * Max(count + 1, 1));
        int source_count = 0;
        source_values[source_count++] = root_value;
        for (int i = 0; i < count; ++i)
            if (!nulls[i]) source_values[source_count++] = values[i];
        ArrayType *sources = construct_array(source_values, source_count,
                                             BYTEAOID, -1, false, TYPALIGN_INT);
        pfree(source_values);
        pfree(DatumGetPointer(root_value));

        const uint32 structural_mask = LAPLACE_STRUCTURAL_CONTAINER |
                                       LAPLACE_STRUCTURAL_CONSTITUENT |
                                       LAPLACE_STRUCTURAL_PREDECESSOR |
                                       LAPLACE_STRUCTURAL_SUCCESSOR |
                                       LAPLACE_STRUCTURAL_COOCCUR;
        result.structural = laplace_trajectory_structural_candidates(
            trajectory_scope, sources, structural_mask, &result.structural_count);
        pfree(sources);
        (void) laplace_prompt_merge_continuations(&result, trajectory_scope);

        /* fanout is a per-source/per-plane envelope. Five structural crossing
         * planes respond per active source, plus one ordered-continuation plane
         * for the exact whole observation. Read the complete native responses;
         * if the envelope cannot contain them, mark orientation resource-bounded
         * instead of compiling a retained prefix as if it were the whole state. */
        uint64 structural_limit = 0;
        if (fanout > 0)
        {
            const uint64 maximum = ~(uint64) 0;
            uint64 planes = (uint64) source_count * 5u + 1u;
            structural_limit = maximum;
            if (planes != 0 && (uint64) fanout <= maximum / planes)
                structural_limit = (uint64) fanout * planes;
        }
        if ((uint64) result.structural_count > structural_limit)
            result.budget_exhausted = true;
        else if (result.structural_count > 0)
        {
            HTAB *pending = laplace_prompt_binding_table(
                "prompt physicality pending bindings", owner);
            for (int i = 0; i < result.structural_count; ++i)
            {
                const LaplaceStructuralCandidate *crossing = &result.structural[i];
                LaplacePromptIntentBinding *source =
                    hash_search(result.bindings, &crossing->source, HASH_FIND, NULL);
                if (!source || !source->origins) continue;
                bool found;
                LaplacePromptIntentBinding *target =
                    hash_search(pending, &crossing->id, HASH_ENTER, &found);
                if (!found) target->origins = NULL;
                target->origins = bms_add_members(target->origins, source->origins);
            }

            HASH_SEQ_STATUS sequence;
            LaplacePromptIntentBinding *entry;
            ArrayBuildState *frontier = NULL;
            hash_seq_init(&sequence, pending);
            while ((entry = hash_seq_search(&sequence)) != NULL)
            {
                bool found;
                LaplacePromptIntentBinding *target =
                    hash_search(result.bindings, &entry->id, HASH_ENTER, &found);
                if (!found)
                {
                    target->origins = NULL;
                    Datum id = hash128_to_datum(&entry->id);
                    frontier = accumArrayResult(frontier, id, false, BYTEAOID, owner);
                    pfree(DatumGetPointer(id));
                }
                target->origins = bms_add_members(target->origins, entry->origins);
                bms_free(entry->origins);
            }
            hash_destroy(pending);
            if (frontier)
            {
                pfree(result.structural_frontier);
                result.structural_frontier =
                    DatumGetArrayTypeP(makeArrayResult(frontier, owner));
            }
        }
    }

    /* Geometry also responds before ORIENT, but after exact physicality routes
     * have retained their own frontier.  This ordering prevents one plane from
     * suppressing the other when both reach the same canonical identity. */
    laplace_prompt_geometry_couple(&result, fanout);

    if (count > 0) { pfree(values); pfree(nulls); }
    if (node_count > 0) { pfree(nodes); pfree(node_nulls); }
    MemoryContextSwitchTo(previous);
    return result;
}

#include "prompt_intent_contract.h"

#endif
