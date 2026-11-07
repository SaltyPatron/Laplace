#ifndef LAPLACE_SUBSTRATE_REALIZE_BATCH_INTERNAL_H
#define LAPLACE_SUBSTRATE_REALIZE_BATCH_INTERNAL_H

#include "postgres.h"
#include "executor/spi.h"
#include "utils/array.h"
#include "utils/hsearch.h"

typedef struct IdKey
{
    char bytes[16];
} IdKey;

typedef struct ArmEntry
{
    IdKey key;
    int32 start;
    int32 count;
} ArmEntry;

typedef struct ArmData
{
    HTAB  *by_id;               /* IdKey -> ArmEntry */
    Datum *cands;               /* candidate bytea datums, arrival order */
    int32  n;
    int32  cap;
} ArmData;

typedef struct RenderEntry
{
    IdKey key;
    int32 slot;
} RenderEntry;

void realize_ensure_name_plans(void);
void realize_ensure_all_plans(void);
SPIPlanPtr realize_plan_has_name(void);
SPIPlanPtr realize_plan_synset_lemma(void);
SPIPlanPtr realize_plan_translation(void);
SPIPlanPtr realize_plan_defines(void);

void realize_id_key(IdKey *key, Datum bytea_datum, const char *what);
HTAB *realize_make_id_htab(const char *name, Size entrysize, long nelem);
void realize_render_union_add(HTAB *render_ids, Datum **union_ids, int32 *n, int32 *cap, Datum cand);
void realize_run_arm(SPIPlanPtr plan, Datum ids_arr, Datum lang, bool lang_null,
                     ArmData *arm, HTAB *render_ids, Datum **union_ids,
                     int32 *un, int32 *ucap, const char *what);
const char *realize_first_nonempty(const ArmData *arm, const IdKey *key,
                                   char **rendered, HTAB *render_ids);
bool realize_validate_id_array(ArrayType *arr, const char *operation);
char **realize_render_union(Datum *union_ids, int32 un, const char *operation);
Datum realize_scalar_from_one_elem_batch(ArrayType *arr);

#endif
