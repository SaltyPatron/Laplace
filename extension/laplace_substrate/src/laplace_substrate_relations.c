#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/hsearch.h"
#include "access/htup_details.h"
#include "access/xact.h"
#include "catalog/pg_type.h"
#include "miscadmin.h"
#include "nodes/execnodes.h"
#include "storage/proc.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/qualifier_law.h"
#include "consensus_scan.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_relation_type_resolve);

Datum
pg_relation_type_resolve(PG_FUNCTION_ARGS)
{
    text*   surface_txt = PG_GETARG_TEXT_PP(0);
    char*   surface     = text_to_cstring(surface_txt);
    hash128_t type_id;
    int     rc = laplace_relation_resolve_surface(surface, &type_id, NULL, NULL, NULL, NULL);
    pfree(surface);
    if (rc < 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(hash128_to_datum(&type_id));
}

PG_FUNCTION_INFO_V1(pg_relation_type_in_family);

Datum
pg_relation_type_in_family(PG_FUNCTION_ARGS)
{
    bytea*  type_ba = PG_GETARG_BYTEA_PP(0);
    text*   fam_txt = PG_GETARG_TEXT_PP(1);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    char*   family  = text_to_cstring(fam_txt);
    int     in_family = 0;
    int     rc = laplace_relation_in_family(&type_id, family, &in_family);
    pfree(family);
    if (rc != 0)
        PG_RETURN_BOOL(false);
    PG_RETURN_BOOL(in_family != 0);
}

PG_FUNCTION_INFO_V1(pg_relation_rank);

Datum
pg_relation_rank(PG_FUNCTION_ARGS)
{
    bytea*    type_ba = PG_GETARG_BYTEA_PP(0);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    const laplace_relation_def_t* def = NULL;
    int       rc = laplace_relation_lookup(&type_id, &def);
    if (rc != 0 || def == NULL)
        PG_RETURN_NULL();
    PG_RETURN_FLOAT8(def->rank);
}

/*
 * Symmetry of a relation type, read from the compiled relation manifest.
 *
 * A symmetric assertion folds into one consensus cell oriented to
 * subject = min(subject, object) (laplace_attestation_orient), so a read that
 * probes subject_id alone sees such a pair only from its lesser-hashed end;
 * readers use this to probe both ends instead of reporting absence.
 *
 * NULL for a type the manifest does not carry (dynamic DEP_/FEAT_/EDEP_
 * families). Callers coalesce it to false so unknown symmetry never widens a
 * read into the reverse direction.
 */
PG_FUNCTION_INFO_V1(pg_relation_is_symmetric);

Datum
pg_relation_is_symmetric(PG_FUNCTION_ARGS)
{
    bytea*    type_ba = PG_GETARG_BYTEA_PP(0);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    const laplace_relation_def_t* def = NULL;
    int       rc = laplace_relation_lookup(&type_id, &def);
    if (rc != 0 || def == NULL)
        PG_RETURN_NULL();
    PG_RETURN_BOOL(def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC);
}

/* Per-backend memo for rank resolution of a type the manifest does not carry.
 * Such a type inherits the rank of its nearest manifest ancestor along the
 * strongest IS_A consensus chain; the memo keeps that chain walk off every row
 * of a ranked read. Resolution depends on the manifest and IS_A standing, so
 * resolved hits stay cached per backend while negative misses are scoped to
 * the current (lxid, cid) command so subsequent mutations re-probe cleanly. */
typedef struct RankMemoEntry
{
    hash128_t          key;
    double             rank;
    bool               has_rank;
    LocalTransactionId lxid;
    CommandId          cid;
} RankMemoEntry;

typedef struct RankResolveBest
{
    hash128_t           object;
    laplace_standing128 mu;
    bool                has;
} RankResolveBest;

static HTAB     *rank_resolve_memo = NULL;
static hash128_t rank_is_a_id;
static bool      rank_is_a_ready = false;

static void
rank_resolve_consume(const LaplaceConsensusRow *row, void *ctx)
{
    RankResolveBest    *best = (RankResolveBest *) ctx;
    laplace_standing128 mu;

    if (row->object_is_null)
        return;
    mu = (laplace_standing128) row->rating - 2 * (laplace_standing128) row->rd;
    if (!best->has || mu > best->mu ||
        (mu == best->mu && memcmp(&row->object, &best->object, sizeof(hash128_t)) < 0))
    {
        best->object = row->object;
        best->mu     = mu;
        best->has    = true;
    }
}

bool
laplace_relation_rank_resolve_c(hash128_t orig_id, double *out_rank)
{
    hash128_t                      cur_id = orig_id;
    const laplace_relation_def_t  *def = NULL;
    RankMemoEntry                 *memo;
    bool                           memo_found;
    LocalTransactionId             cur_lxid;
    CommandId                      cur_cid;
    ArrayType                     *isa_arr;
    double                         resolved_rank = 0.0;
    bool                           found = false;
    HTAB                          *visited;
    HASHCTL                        vctl;

    if (laplace_relation_lookup(&cur_id, &def) == 0 && def != NULL)
    {
        if (out_rank != NULL)
            *out_rank = def->rank;
        return true;
    }

    cur_lxid = (MyProc != NULL) ? MyProc->vxid.lxid : InvalidLocalTransactionId;
    cur_cid  = GetCurrentCommandId(false);

    if (rank_resolve_memo == NULL)
    {
        HASHCTL ctl;

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize   = sizeof(hash128_t);
        ctl.entrysize = sizeof(RankMemoEntry);
        ctl.hcxt      = TopMemoryContext;
        rank_resolve_memo = hash_create("relation_rank_resolved memo", 64,
                                        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    memo = (RankMemoEntry *) hash_search(rank_resolve_memo, &orig_id,
                                         HASH_FIND, &memo_found);
    if (memo_found)
    {
        if (memo->has_rank)
        {
            if (out_rank != NULL)
                *out_rank = memo->rank;
            return true;
        }
        if (cur_lxid != InvalidLocalTransactionId &&
            memo->lxid == cur_lxid &&
            memo->cid == cur_cid)
            return false;
    }

    if (!rank_is_a_ready)
    {
        if (laplace_relation_resolve_surface("IS_A", &rank_is_a_id, NULL, NULL, NULL, NULL) < 0)
            return false;
        rank_is_a_ready = true;
    }

    isa_arr = hash128_array_from_ids(&rank_is_a_id, 1);
    memset(&vctl, 0, sizeof(vctl));
    vctl.keysize   = sizeof(hash128_t);
    vctl.entrysize = sizeof(hash128_t);
    vctl.hcxt      = CurrentMemoryContext;
    visited = hash_create("relation_rank_resolved visited", 8,
                          &vctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    while (!found)
    {
        bool            seen;
        ArrayType      *subj_arr;
        RankResolveBest best;

        CHECK_FOR_INTERRUPTS();
        (void) hash_search(visited, &cur_id, HASH_ENTER, &seen);
        if (seen)
            break;

        best.has = false;
        best.mu  = 0;
        memset(&best.object, 0, sizeof(best.object));

        subj_arr = hash128_array_from_ids(&cur_id, 1);
        laplace_consensus_candidates_stored(subj_arr, NULL, isa_arr,
                                            rank_resolve_consume, &best);
        pfree(subj_arr);

        if (!best.has)
            break;

        {
            const laplace_relation_def_t *pdef = NULL;
            if (laplace_relation_lookup(&best.object, &pdef) == 0 && pdef != NULL)
            {
                resolved_rank = pdef->rank;
                found         = true;
            }
            else
            {
                cur_id = best.object;
            }
        }
    }

    hash_destroy(visited);
    pfree(isa_arr);

    memo = (RankMemoEntry *) hash_search(rank_resolve_memo, &orig_id,
                                         HASH_ENTER, &memo_found);
    memo->rank     = resolved_rank;
    memo->has_rank = found;
    memo->lxid     = cur_lxid;
    memo->cid      = cur_cid;

    if (found && out_rank != NULL)
        *out_rank = resolved_rank;
    return found;
}

PG_FUNCTION_INFO_V1(pg_relation_rank_resolved);

Datum
pg_relation_rank_resolved(PG_FUNCTION_ARGS)
{
    bytea    *type_ba  = PG_GETARG_BYTEA_PP(0);
    hash128_t orig_id  = datum_to_hash128(PointerGetDatum(type_ba));
    double    out_rank = 0.0;

    if (laplace_relation_rank_resolve_c(orig_id, &out_rank))
        PG_RETURN_FLOAT8(out_rank);
    PG_RETURN_NULL();
}

PG_FUNCTION_INFO_V1(pg_relation_canonical);

Datum
pg_relation_canonical(PG_FUNCTION_ARGS)
{
    bytea*    type_ba = PG_GETARG_BYTEA_PP(0);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    const laplace_relation_def_t* def = NULL;
    int       rc = laplace_relation_lookup(&type_id, &def);
    if (rc != 0 || def == NULL || def->canonical == NULL)
        PG_RETURN_NULL();
    PG_RETURN_TEXT_P(cstring_to_text(def->canonical));
}

PG_FUNCTION_INFO_V1(pg_laplace_qualifier_bit);

Datum
pg_laplace_qualifier_bit(PG_FUNCTION_ARGS)
{
    char *family = text_to_cstring(PG_GETARG_TEXT_PP(0));
    char *value  = text_to_cstring(PG_GETARG_TEXT_PP(1));
    int   bit    = laplace_qualifier_bit(family, value);

    if (bit < 0)
        ereport(ERROR,
                (errcode(ERRCODE_RAISE_EXCEPTION),
                 errmsg("undeclared claim qualifier %s/%s", family, value),
                 errhint("engine/manifest/qualifiers.toml")));
    pfree(family);
    pfree(value);
    PG_RETURN_INT32(bit);
}

PG_FUNCTION_INFO_V1(pg_laplace_has_qualifier);

Datum
pg_laplace_has_qualifier(PG_FUNCTION_ARGS)
{
    bytea         *mask;
    char          *family;
    char          *value;
    int            bit;
    Size           len;
    const uint8_t *bytes;
    bool           has;

    if (PG_ARGISNULL(1) || PG_ARGISNULL(2))
        PG_RETURN_NULL();
    family = text_to_cstring(PG_GETARG_TEXT_PP(0 + 1));
    value  = text_to_cstring(PG_GETARG_TEXT_PP(2));
    bit    = laplace_qualifier_bit(family, value);
    if (bit < 0)
        ereport(ERROR,
                (errcode(ERRCODE_RAISE_EXCEPTION),
                 errmsg("undeclared claim qualifier %s/%s", family, value),
                 errhint("engine/manifest/qualifiers.toml")));
    pfree(family);
    pfree(value);
    if (PG_ARGISNULL(0))
        PG_RETURN_BOOL(false);
    mask  = PG_GETARG_BYTEA_PP(0);
    len   = VARSIZE_ANY_EXHDR(mask);
    if ((Size) (bit >> 3) >= len)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("index %d out of valid range, 0..%d",
                        bit, (int) (len * 8) - 1)));
    bytes = (const uint8_t *) VARDATA_ANY(mask);
    has   = ((bytes[bit >> 3] >> (bit & 7)) & 1) != 0;
    PG_RETURN_BOOL(has);
}
