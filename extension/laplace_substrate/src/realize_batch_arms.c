#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "utils/acl.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/lsyscache.h"
#include "utils/rls.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "opaque_surface.h"
#include "consensus_scan.h"
#include "realize_batch_internal.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/entity_type_law.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/sql_catalog.h"

/* ---- prepared plans, one per arm, kept across calls ---- */

static SPIPlanPtr plan_has_name = NULL;
static SPIPlanPtr plan_synset_lemma = NULL;
static SPIPlanPtr plan_translation = NULL;
static SPIPlanPtr plan_defines = NULL;
static SPIPlanPtr plan_render = NULL;

/* Names: realize.name_candidates (sql_catalog.def), relation HAS_NAME. */

/* Lexicalization: an entity is realized by the words attested to express it
 * (word HAS_SENSE entity, or word HAS_SENSE sense IS_SENSE_OF entity),
 * requested language first, then strongest, then single-word tier before
 * multi-word phrases, then deterministic word id. */
static const char *Q_SYNSET_LEMMA =
    "WITH cand AS MATERIALIZED ("
    "  SELECT kb.key AS entity_id, kb.term AS word_id,"
    "         kb.in_lang AS lp,"
    "         consensus.eff_mu(kb.rating, kb.rd) AS mu"
    "  FROM converse.key_binding_evidence($1, NULL, false) kb"
    "  WHERE $2 IS NULL OR kb.language = $2 OR kb.language IS NULL"
    "), tiers AS MATERIALIZED ("
    "  SELECT public.blake3_send(e.id) AS word_id, e.tier"
    "  FROM public.laplace_entities(ARRAY("
    "      SELECT DISTINCT public.blake3_from_bytea(c.word_id) FROM cand c WHERE c.word_id IS NOT NULL"
    "  )) e"
    ") "
    "SELECT cand.entity_id, cand.word_id, cand.lp, cand.mu "
    "FROM cand "
    "LEFT JOIN tiers w ON w.word_id = cand.word_id "
    "ORDER BY cand.entity_id, cand.lp DESC, cand.mu DESC, COALESCE(w.tier, 32767) ASC, cand.word_id";

static const char *Q_TRANSLATION =
    "WITH edges AS MATERIALIZED ("
    "  SELECT e.subject_id, e.neighbour_id AS object_id, consensus.eff_mu(e.rating, e.rd) AS mu "
    "  FROM consensus.edges_raw_batch($1::bytea[], 'out', ARRAY[laplace.relation_type_id('IS_TRANSLATION_OF')], 16, false, 'eff_mu') e "
    "  WHERE e.neighbour_id IS NOT NULL"
    "), langs AS MATERIALIZED ("
    "  SELECT DISTINCT ON (v.word) v.word, v.lang "
    "  FROM converse.word_language_candidates_batch("
    "    CASE WHEN $2 IS NOT NULL THEN ARRAY(SELECT DISTINCT object_id FROM edges) ELSE '{}'::bytea[] END"
    "  ) v ORDER BY v.word, v.mu DESC, v.lang"
    ") "
    "SELECT e.subject_id, e.object_id, ($2 IS NOT NULL AND l.lang = $2) AS lp, e.mu "
    "FROM edges e LEFT JOIN langs l ON l.word = e.object_id "
    "ORDER BY e.subject_id, ($2 IS NOT NULL AND l.lang = $2) DESC, e.mu DESC, e.object_id";

static const char *Q_DEFINES =
    "SELECT e.subject_id, e.neighbour_id AS object_id,"
    "       consensus.eff_mu(e.rating, e.rd) AS mu "
    "FROM consensus.edges_raw_batch($1::bytea[], 'out', ARRAY[laplace.relation_type_id('HAS_DEFINITION')], 8, false, 'eff_mu') e "
    "WHERE e.neighbour_id IS NOT NULL "
    "ORDER BY e.subject_id, consensus.eff_mu(e.rating, e.rd) DESC, e.neighbour_id";   /* object_id CLOSES the order */

static const char *Q_RENDER =
    "SELECT realize.render_text_batch($1)";

static SPIPlanPtr ensure_plan(SPIPlanPtr *slot, const char *sql,
                              int nargs, const Oid *argtypes);

void
realize_ensure_name_plans(void)
{
    Oid two[2] = { BYTEAARRAYOID, BYTEAOID };
    Oid one[1] = { BYTEAARRAYOID };

    ensure_plan(&plan_has_name, laplace_sql_query_text("realize.name_candidates"), 2, two);
    ensure_plan(&plan_synset_lemma, Q_SYNSET_LEMMA, 2, two);
    ensure_plan(&plan_render, Q_RENDER, 1, one);
}

void
realize_ensure_all_plans(void)
{
    Oid two[2] = { BYTEAARRAYOID, BYTEAOID };
    Oid one[1] = { BYTEAARRAYOID };

    realize_ensure_name_plans();
    ensure_plan(&plan_translation, Q_TRANSLATION, 2, two);
    ensure_plan(&plan_defines, Q_DEFINES, 1, one);
}

SPIPlanPtr
realize_plan_has_name(void)
{
    return plan_has_name;
}

SPIPlanPtr
realize_plan_synset_lemma(void)
{
    return plan_synset_lemma;
}

SPIPlanPtr
realize_plan_translation(void)
{
    return plan_translation;
}

SPIPlanPtr
realize_plan_defines(void)
{
    return plan_defines;
}

static SPIPlanPtr
ensure_plan(SPIPlanPtr *slot, const char *sql, int nargs, const Oid *argtypes)
{
    if (*slot == NULL)
    {
        SPIPlanPtr plan = SPI_prepare_cursor(sql, nargs, (Oid *) argtypes, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "realize_batch: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "realize_batch: SPI_keepplan failed");
        *slot = plan;
    }
    return *slot;
}

/* Every arm's ORDER BY closes on an id, so the first row per input id is a
 * property of the data, not of the plan the input array's size selects. */

void
realize_id_key(IdKey *key, Datum bytea_datum, const char *what)
{
    bytea *b = DatumGetByteaPP(bytea_datum);

    if (VARSIZE_ANY_EXHDR(b) != 16)
        ereport(ERROR,
                (errmsg("realize_batch: %s id must be 16 bytes (got %zu)",
                        what, (size_t) VARSIZE_ANY_EXHDR(b))));
    memcpy(key->bytes, VARDATA_ANY(b), 16);
}

HTAB *
realize_make_id_htab(const char *name, Size entrysize, long nelem)
{
    HASHCTL hctl;

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize = sizeof(IdKey);
    hctl.entrysize = entrysize;
    return hash_create(name, nelem, &hctl, HASH_ELEM | HASH_BLOBS);
}

void
realize_render_union_add(HTAB *render_ids, Datum **union_ids, int32 *n, int32 *cap,
                         Datum cand)
{
    IdKey        key;
    bool         found;
    RenderEntry *e;

    realize_id_key(&key, cand, "candidate");
    e = (RenderEntry *) hash_search(render_ids, &key, HASH_ENTER, &found);
    if (found)
        return;
    if (*n == *cap)
    {
        *cap *= 2;
        *union_ids = (Datum *) repalloc(*union_ids, sizeof(Datum) * *cap);
    }
    e->slot = *n;
    (*union_ids)[(*n)++] = cand;
}

typedef struct ArmFrontier
{
    HTAB *owners;
    bool incoming;
} ArmFrontier;

static void
arm_frontier_visit(const LaplaceConsensusRow *row, void *context)
{
    ArmFrontier *frontier = context;
    if (!row->object_is_null && !laplace_glicko2_refuted(row->rating, row->rd))
        hash_search(frontier->owners,
            frontier->incoming ? &row->object : &row->subject, HASH_ENTER, NULL);
}

/* Prune empty semantic branches using the shared native indexed set reader.
 * The subsequent ranked SQL keeps its language/tier/tie rules, but only sees
 * owners with a possible unrefuted candidate. In particular, a missing name
 * or lexicalization must not plan a multi-partition join just to return zero.
 * This probe uses the same MVCC snapshot, ACL/RLS checks and refutation law as
 * the actual arm; it does not infer absence from labels or planner statistics. */
static ArrayType *
arm_frontier(SPIPlanPtr plan, ArrayType *input)
{
    Oid relation = get_relname_relid("consensus", get_namespace_oid("laplace", false));
    /* Views/column grants and RLS retain their SQL authorization semantics.
     * The native optimization is used only when a direct set read is allowed. */
    if (!OidIsValid(relation) ||
        pg_class_aclcheck(relation, GetUserId(), ACL_SELECT) != ACLCHECK_OK ||
        check_enable_rls(relation, InvalidOid, true) == RLS_ENABLED)
        return input;
    const char *names[2];
    int count = 1;
    bool incoming = false;
    /* Q_SYNSET_LEMMA also resolves Native 3-part [word, lang, key] tuple bindings,
     * which are not 3-part HAS_SENSE/IS_SENSE_OF claims. */
    if (plan == plan_synset_lemma) return input;
    else if (plan == plan_has_name) names[0] = "HAS_NAME";
    else if (plan == plan_translation) names[0] = "IS_TRANSLATION_OF";
    else return input;
    Datum type_ids[2];
    for (int i = 0; i < count; ++i)
    {
        hash128_t type;
        if (laplace_relation_type_id(names[i], &type) != 0) return input;
        type_ids[i] = hash128_to_datum(&type);
    }
    ArrayType *types = construct_array(type_ids, count, BYTEAOID, -1, false, TYPALIGN_INT);
    ArmFrontier frontier = {realize_make_id_htab("realization candidate frontier", sizeof(IdKey), 64), incoming};
    laplace_consensus_scan(incoming ? NULL : input, incoming ? input : NULL,
        types, arm_frontier_visit, &frontier, NULL);
    ArrayBuildState *selected = NULL;
    HASH_SEQ_STATUS scan;
    IdKey *id;
    hash_seq_init(&scan, frontier.owners);
    while ((id = hash_seq_search(&scan)) != NULL)
    {
        hash128_t owner;
        memcpy(&owner, id, sizeof(owner));
        selected = accumArrayResult(selected, hash128_to_datum(&owner), false,
            BYTEAOID, CurrentMemoryContext);
    }
    ArrayType *result = selected ? DatumGetArrayTypeP(makeArrayResult(selected, CurrentMemoryContext))
        : construct_empty_array(BYTEAOID);
    hash_destroy(frontier.owners);
    return result;
}

/* Run one candidate arm and decode it into ArmData + the render union. */
void
realize_run_arm(SPIPlanPtr plan, Datum ids_arr, Datum lang, bool lang_null,
                ArmData *arm, HTAB *render_ids, Datum **union_ids,
                int32 *un, int32 *ucap, const char *what)
{
    Datum args[2];
    char  nulls[3] = "  ";
    int   rc;

    ArrayType *selected = arm_frontier(plan, DatumGetArrayTypeP(ids_arr));
    arm->by_id = realize_make_id_htab(what, sizeof(ArmEntry), 256);
    arm->cands = NULL;
    arm->n = arm->cap = 0;
    if (ArrayGetNItems(ARR_NDIM(selected), ARR_DIMS(selected)) == 0) return;
    args[0] = PointerGetDatum(selected);
    args[1] = lang;
    if (lang_null)
        nulls[1] = 'n';

    rc = SPI_execute_plan(plan, args, nulls, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "realize_batch: %s arm failed: %s",
             what, SPI_result_code_string(rc));

    arm->cap = Max(64, (int32) SPI_processed);
    arm->cands = (Datum *) palloc(sizeof(Datum) * arm->cap);
    arm->n = 0;

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple  tup = SPI_tuptable->vals[r];
        TupleDesc  td = SPI_tuptable->tupdesc;
        bool       isnull;
        Datum      in_id = SPI_getbinval(tup, td, 1, &isnull);
        Datum      cand;
        IdKey      key;
        bool       found;
        ArmEntry  *e;

        if (isnull)
            continue;
        cand = SPI_getbinval(tup, td, 2, &isnull);
        if (isnull)
            continue;
        cand = copy_bytea_datum(cand);

        realize_id_key(&key, in_id, what);
        e = (ArmEntry *) hash_search(arm->by_id, &key, HASH_ENTER, &found);
        if (!found)
        {
            e->start = arm->n;
            e->count = 0;
        }
        /* rows arrive grouped by input id (ORDER BY input id first), so the
         * run stays contiguous; count extends it. */
        if (arm->n == arm->cap)
        {
            arm->cap *= 2;
            arm->cands = (Datum *) repalloc(arm->cands, sizeof(Datum) * arm->cap);
        }
        arm->cands[arm->n++] = cand;
        e->count++;

        realize_render_union_add(render_ids, union_ids, un, ucap, cand);
    }
    SPI_freetuptable(SPI_tuptable);
}

/* First candidate in [start, start+count) whose render is NON-EMPTY. */
const char *
realize_first_nonempty(const ArmData *arm, const IdKey *key,
                       char **rendered, HTAB *render_ids)
{
    ArmEntry *e = (ArmEntry *) hash_search(arm->by_id, key, HASH_FIND, NULL);

    if (e == NULL)
        return NULL;
    for (int32 i = e->start; i < e->start + e->count; i++)
    {
        IdKey        ck;
        RenderEntry *re;

        realize_id_key(&ck, arm->cands[i], "render lookup");
        re = (RenderEntry *) hash_search(render_ids, &ck, HASH_FIND, NULL);
        if (re != NULL && rendered[re->slot] != NULL && rendered[re->slot][0] != '\0')
            return rendered[re->slot];
    }
    return NULL;
}

bool
realize_validate_id_array(ArrayType *arr, const char *operation)
{
    if (ARR_NDIM(arr) == 0)
        return false;
    if (ARR_NDIM(arr) != 1)
        ereport(ERROR,
                (errmsg("%s: ids must be 1-dimensional", operation)));
    if (ARR_ELEMTYPE(arr) != BYTEAOID)
        ereport(ERROR,
                (errmsg("%s: element type must be bytea", operation)));
    return true;
}

char **
realize_render_union(Datum *union_ids, int32 un, const char *operation)
{
    char **rendered = (char **) palloc0(sizeof(char *) * Max(un, 1));

    if (un > 0)
    {
        ArrayType *ids_arr = construct_array(union_ids, un, BYTEAOID, -1,
                                             false, TYPALIGN_INT);
        Datum      args[1] = { PointerGetDatum(ids_arr) };
        int        rc = SPI_execute_plan(plan_render, args, NULL, true, 1);
        bool       isnull;
        Datum      arr_datum;

        if (rc != SPI_OK_SELECT || SPI_processed != 1)
            elog(ERROR, "%s: render batch failed: %s", operation,
                 SPI_result_code_string(rc));
        arr_datum = SPI_getbinval(SPI_tuptable->vals[0],
                                  SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
        {
            ArrayType *ra = DatumGetArrayTypeP(arr_datum);
            Datum     *relems;
            bool      *rnulls;
            int        rn;

            deconstruct_array(ra, TEXTOID, -1, false, TYPALIGN_INT,
                              &relems, &rnulls, &rn);
            if (rn != un)
                elog(ERROR, "%s: render batch returned %d of %d",
                     operation, rn, un);
            for (int i = 0; i < un; i++)
                if (!rnulls[i])
                    rendered[i] = text_to_cstring(DatumGetTextPP(relems[i]));
        }
        SPI_freetuptable(SPI_tuptable);
    }
    return rendered;
}
