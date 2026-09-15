#ifndef LAPLACE_PROMPT_INTENT_H
#define LAPLACE_PROMPT_INTENT_H

#include "postgres.h"
#include "nodes/bitmapset.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "prompt_input.h"
#include "observation_read.h"
#include "consensus_scan.h"
#include "laplace/core/attestation_engine.h"
#include "query_evidence.h"
#include "relation_symmetry.h"
#include "spi_common.h"
#include "walk_score.h"

/* Bindings carry exact current-prompt occurrence ordinals. History and emitted
 * identities can supply evidence, but cannot become new instruction cues. */
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
    int requirement; /* runtime input identity coordinate, distinct from surface ordinals */
} LaplacePromptOperand;

/* Explicit whole-observation invocation, with the actual source occurrence
 * retained. Naming knowledge alone never creates one of these records. */
typedef struct LaplacePromptRelationRead
{
    hash128_t result_relation, source, context, call_witness;
    LaplacePromptOperand *inputs;
    int input_count;
    Bitmapset *operand_origins;
} LaplacePromptRelationRead;

typedef struct LaplacePromptIntent
{
    MemoryContext owner;
    HTAB *bindings;
    hash128_t root;
    hash128_t active_context;
    LaplacePromptRelationRead *operations;
    int relation_count;
    int relation_capacity;
    bool ambiguous;
    bool explicit_invocation;
    bool budget_exhausted;
} LaplacePromptIntent;

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

/* Protocol relations describe an invocation; they are not result transitions.
 * Keep their channels in COUPLE but never use their endpoints as answer proof. */
static inline int
laplace_prompt_contract_relation(const hash128_t *relation)
{
    int member = 0;
    laplace_relation_in_family(relation, "CALLS", &member);
    if (member) return 1;
    laplace_relation_in_family(relation, "HAS_INPUT", &member);
    return member ? 2 : 0;
}

/* These are typed naming/interpretation relations, not surface-word rules.
 * Reverse access means finding what a surface names/evokes; it does not invert
 * the result operation (for example, HAS_PART remains directed). Definitions
 * and arbitrary semantic associations are deliberately not naming evidence. */
static inline bool
laplace_prompt_binding_channel(const LaplaceQueryChannel *channel)
{
    static const char *families[] = {
        "HAS_NAME", "HAS_NAME_ALIAS", "HAS_SENSE", "IS_LEMMA_OF", "EVOKES_FRAME"
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

static inline LaplacePromptIntent
laplace_prompt_intent_begin(const LaplacePromptInput *input, MemoryContext owner)
{
    LaplacePromptIntent result = {0};
    Datum *values, *nodes;
    bool *nulls, *node_nulls;
    int count, node_count;
    MemoryContext previous = MemoryContextSwitchTo(owner);
    result.owner = owner;
    result.root = input->root;
    result.bindings = laplace_prompt_binding_table("prompt witnessed bindings", owner);
    deconstruct_array(input->context, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    deconstruct_array(input->nodes, INT4OID, 4, true, TYPALIGN_INT,
                      &nodes, &node_nulls, &node_count);
    if (count != node_count)
        elog(ERROR, "prompt intent: occurrence projections disagree");
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
    }
    if (count > 0) { pfree(values); pfree(nulls); }
    if (node_count > 0) { pfree(nodes); pfree(node_nulls); }
    MemoryContextSwitchTo(previous);
    return result;
}

static inline bool
laplace_prompt_result_channel(const LaplaceQueryChannel *channel)
{
    const laplace_relation_def_t *def = NULL;
    if (channel->outbound) return true;
    return laplace_relation_lookup(&channel->relation, &def) == 0 &&
           def != NULL && def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC;
}

typedef struct LaplacePromptContractCell
{
    LaplaceObservationCell cell;
    bool positive;
} LaplacePromptContractCell;

typedef struct LaplacePromptContractRead
{
    LaplacePromptIntent *intent;
    const hash128_t *active_context;
    HTAB *witnesses;
    HTAB *cells;
    int fanout;
} LaplacePromptContractRead;

static void
laplace_prompt_contract_observation(int ordinal, int16 role,
                                    const LaplaceObservation *row, void *context)
{
    LaplacePromptContractRead *read = context;
    LaplaceObservation *stored;
    MemoryContext previous;
    bool found;
    (void) ordinal;
    if (role != 1 || row->object_null || row->source_null || row->context_null ||
        row->occurrences <= 0 || !hash128_eq(&row->subject, &read->intent->root) ||
        !hash128_eq(&row->context, read->active_context))
        return;
    previous = MemoryContextSwitchTo(read->intent->owner);
    if (!hash_search(read->witnesses, &row->id, HASH_FIND, NULL) &&
        hash_get_num_entries(read->witnesses) >= read->fanout)
    {
        /* Continue the complete set read, but never compile the retained
         * prefix as a smaller contract when its declared envelope is exceeded. */
        read->intent->budget_exhausted = true;
        MemoryContextSwitchTo(previous);
        return;
    }
    stored = hash_search(read->witnesses, &row->id, HASH_ENTER, NULL);
    *stored = *row;
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    LaplacePromptContractCell *cell = hash_search(read->cells, &key, HASH_ENTER, &found);
    if (!found) cell->positive = false;
    MemoryContextSwitchTo(previous);
}

static void
laplace_prompt_contract_standing(const LaplaceConsensusRow *row, void *context)
{
    LaplacePromptContractRead *read = context;
    LaplaceObservationCell key;
    LaplacePromptContractCell *cell;
    if (row->object_is_null) return;
    key = (LaplaceObservationCell) {row->subject, row->type, row->object};
    cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    if (cell)
        cell->positive = laplace_walk_edge_weight(row->rating, row->rd) > 0.0;
}

static inline bool
laplace_prompt_contract_positive(const LaplacePromptContractRead *read,
                                 const LaplaceObservation *row)
{
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    LaplacePromptContractCell *cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    return cell && cell->positive;
}

static inline bool
laplace_prompt_same_scope(const LaplaceObservation *a, const LaplaceObservation *b)
{
    return hash128_eq(&a->source, &b->source) &&
           hash128_eq(&a->context, &b->context);
}

static inline bool
laplace_prompt_contract_refuted(const LaplaceObservation *row,
                                const LaplaceObservation *rows, int count)
{
    for (int i = 0; i < count; ++i)
        if (rows[i].outcome == LAPLACE_ATTESTATION_OUTCOME_REFUTE &&
            laplace_prompt_same_scope(row, &rows[i]) &&
            hash128_eq(&row->type, &rows[i].type) &&
            hash128_eq(&row->object, &rows[i].object))
            return true;
    return false;
}

static int
laplace_prompt_witness_compare(const void *left, const void *right)
{
    const LaplaceObservation *a = left, *b = right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

/* The caller declares the active invocation context. A source witnesses
 * R CALLS O and R HAS_INPUT I in that exact context, with a common source.
 * R is the current whole observation; I has exact current occurrence provenance
 * through witnessed naming/sense bindings. The result anchor must be exactly I.
 *
 * Read ALL typed declarations before checking standing. Missing/negative
 * standing for a declared input invalidates the contract; top-K cannot silently
 * erase it. The one native set read retains witnesses, followed by one set
 * consensus probe. Exceeding fanout never authorizes a retained prefix.
 *
 * This executes explicit program data. It does not infer a speech act from an
 * alias, a frame, the availability of an answer, or a historical text match. */
static inline void
laplace_prompt_intent_compile(LaplacePromptIntent *intent,
                              const hash128_t *active_context, int fanout)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    HASHCTL ctl = {0};
    HASH_SEQ_STATUS sequence;
    LaplaceObservation *entry, *rows;
    int row_count = 0;
    Datum root;
    ArrayType *roots, *types, *objects;
    ArrayBuildState *type_ids = NULL, *object_ids = NULL;
    LaplacePromptContractRead read = {
        .intent = intent, .active_context = active_context, .fanout = fanout};

    intent->explicit_invocation = true;
    intent->active_context = *active_context;
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplaceObservation);
    ctl.hcxt = intent->owner;
    read.witnesses = hash_create("prompt invocation witnesses", Max(Min(fanout, 128), 1), &ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.keysize = sizeof(LaplaceObservationCell);
    ctl.entrysize = sizeof(LaplacePromptContractCell);
    read.cells = hash_create("prompt invocation cells", Max(Min(fanout, 128), 1), &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (size_t i = 0; i < laplace_relation_table_count; ++i)
        if (laplace_prompt_contract_relation(&laplace_relation_table[i].type_id))
        {
            Datum id = hash128_to_datum(&laplace_relation_table[i].type_id);
            type_ids = accumArrayResult(type_ids, id, false, BYTEAOID, intent->owner);
            pfree(DatumGetPointer(id));
        }
    types = type_ids ? DatumGetArrayTypeP(makeArrayResult(type_ids, intent->owner)) :
        construct_empty_array(BYTEAOID);
    root = hash128_to_datum(&intent->root);
    roots = construct_array(&root, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    laplace_observation_read(roots, NULL, types, 1,
                             laplace_prompt_contract_observation, &read);
    if (intent->budget_exhausted)
    {
        hash_destroy(read.witnesses);
        hash_destroy(read.cells);
        pfree(roots); pfree(types); pfree(DatumGetPointer(root));
        MemoryContextSwitchTo(previous);
        return;
    }
    if (hash_get_num_entries(read.witnesses) > INT_MAX ||
        hash_get_num_entries(read.witnesses) > MaxAllocSize / sizeof(*rows))
        elog(ERROR, "prompt contract: witness set exceeds allocation capacity");
    row_count = (int) hash_get_num_entries(read.witnesses);
    rows = palloc(sizeof(*rows) * Max(row_count, 1));
    hash_seq_init(&sequence, read.witnesses);
    for (int i = 0; (entry = hash_seq_search(&sequence)) != NULL; ++i)
    {
        Datum id = hash128_to_datum(&entry->object);
        rows[i] = *entry;
        object_ids = accumArrayResult(object_ids, id, false, BYTEAOID, intent->owner);
        pfree(DatumGetPointer(id));
    }
    hash_destroy(read.witnesses);
    objects = object_ids ? DatumGetArrayTypeP(makeArrayResult(object_ids, intent->owner)) :
        construct_empty_array(BYTEAOID);
    laplace_consensus_scan(roots, objects, types, laplace_prompt_contract_standing, &read, NULL);
    pfree(roots); pfree(types); pfree(objects); pfree(DatumGetPointer(root));
    qsort(rows, row_count, sizeof(*rows), laplace_prompt_witness_compare);

    for (int i = 0; i < row_count; ++i)
    {
        const LaplaceObservation *call = &rows[i];
        LaplacePromptRelationRead operation = {0};
        bool valid = true;
        if (laplace_prompt_contract_relation(&call->type) != 1 ||
            call->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM ||
            !laplace_prompt_contract_positive(&read, call) ||
            laplace_prompt_contract_refuted(call, rows, row_count))
            continue;
        operation.result_relation = call->object;
        operation.source = call->source;
        operation.context = call->context;
        operation.call_witness = call->id;
        operation.inputs = palloc0(sizeof(*operation.inputs) * Max(row_count, 1));
        for (int j = 0; j < row_count; ++j)
        {
            const LaplaceObservation *input = &rows[j];
            LaplacePromptIntentBinding *binding;
            LaplacePromptOperand *operand;
            if (!laplace_prompt_same_scope(call, input) ||
                laplace_prompt_contract_relation(&input->type) != 2)
                continue;
            binding = hash_search(intent->bindings, &input->object, HASH_FIND, NULL);
            if (!binding || input->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM ||
                !laplace_prompt_contract_positive(&read, input) ||
                laplace_prompt_contract_refuted(input, rows, row_count))
            {
                valid = false;
                break;
            }
            operand = &operation.inputs[operation.input_count++];
            operand->id = input->object;
            operand->witness = input->id;
            operand->origins = bms_copy(binding->origins);
            operation.operand_origins = bms_add_members(operation.operand_origins,
                                                        binding->origins);
        }
        if (!valid || operation.input_count == 0)
        {
            for (int j = 0; j < operation.input_count; ++j)
                bms_free(operation.inputs[j].origins);
            pfree(operation.inputs);
            bms_free(operation.operand_origins);
            continue;
        }
        if (intent->relation_count == intent->relation_capacity)
        {
            int64 capacity = intent->relation_capacity ?
                (int64) intent->relation_capacity * 2 : 8;
            if (capacity > INT_MAX ||
                (Size) capacity > MaxAllocSize / sizeof(LaplacePromptRelationRead))
                elog(ERROR, "prompt contract: operation set exceeds allocation capacity");
            intent->operations = intent->operations ?
                repalloc(intent->operations, sizeof(LaplacePromptRelationRead) * capacity) :
                palloc(sizeof(LaplacePromptRelationRead) * capacity);
            intent->relation_capacity = (int) capacity;
        }
        intent->operations[intent->relation_count++] = operation;
    }
    for (int i = 0; i < intent->relation_count; ++i)
        for (int j = i + 1; j < intent->relation_count; ++j)
            if (!hash128_eq(&intent->operations[i].result_relation, &intent->operations[j].result_relation) ||
                !laplace_prompt_same_inputs(&intent->operations[i], &intent->operations[j]))
                intent->ambiguous = true;
    hash_destroy(read.cells);
    pfree(rows);
    MemoryContextSwitchTo(previous);
}

/* Advance exactly ONE witnessed naming hop. Stage additions before merging so
 * iteration order cannot secretly turn one coupling round into many hops.
 * Return changed identities for one set-sized frontier extension. All input
 * channels belong to the unmasked active field, and all alternatives survive. */
static inline ArrayType *
laplace_prompt_intent_couple(LaplacePromptIntent *intent,
                             const LaplaceQueryChannel *channels, int count)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    HTAB *pending = laplace_prompt_binding_table("prompt pending bindings", intent->owner);
    HASH_SEQ_STATUS sequence;
    LaplacePromptIntentBinding *entry;
    ArrayBuildState *changed = NULL;
    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &channels[i];
        const Bitmapset *anchor;
        bool found;
        if (!laplace_prompt_binding_channel(channel)) continue;
        anchor = laplace_prompt_intent_origins(intent, &channel->anchor);
        if (!anchor) continue;
        entry = hash_search(pending, &channel->candidate, HASH_ENTER, &found);
        if (!found) entry->origins = NULL;
        entry->origins = bms_add_members(entry->origins, anchor);
    }
    hash_seq_init(&sequence, pending);
    while ((entry = hash_seq_search(&sequence)) != NULL)
    {
        bool found;
        LaplacePromptIntentBinding *target =
            hash_search(intent->bindings, &entry->id, HASH_ENTER, &found);
        if (!found) target->origins = NULL;
        if (!bms_is_subset(entry->origins, target->origins))
        {
            Datum id = hash128_to_datum(&entry->id);
            target->origins = bms_add_members(target->origins, entry->origins);
            changed = accumArrayResult(changed, id, false, BYTEAOID, intent->owner);
            pfree(DatumGetPointer(id));
        }
        bms_free(entry->origins);
    }
    hash_destroy(pending);
    ArrayType *result = changed ? DatumGetArrayTypeP(makeArrayResult(changed, intent->owner)) :
        construct_empty_array(BYTEAOID);
    MemoryContextSwitchTo(previous);
    return result;
}

#endif
