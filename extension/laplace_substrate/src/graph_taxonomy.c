#include "postgres.h"

#include <limits.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/math4d.h"
#include "laplace/core/relation_law.h"
#include "bubble_up_native.h"
#include "edges_raw_internal.h"
#include "graph_taxonomy_internal.h"
#include "perfcache_native.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_hypernyms);
PG_FUNCTION_INFO_V1(pg_laplace_isa_path);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_taxonomy_edges_batch);
PG_FUNCTION_INFO_V1(pg_laplace_synset_gloss_batch);

int
tax_find(const hash128_t *ids, int n, const hash128_t *key)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&ids[i], key))
            return i;
    return -1;
}

typedef struct TaxSubjOrd
{
    hash128_t id;
    int64     ord;
} TaxSubjOrd;

typedef struct TaxEdgeOutRow
{
    int64     ord;
    hash128_t object_id;
    hash128_t type_id;
    int64     rating;
    int64     rd;
    bool      relation_null;
    hash128_t relation_id;
} TaxEdgeOutRow;

typedef struct TaxSubjBucket
{
    hash128_t key;
    int       head;
    int       tail;
} TaxSubjBucket;

static hash128_t
tax_resolve_canon_type(const hash128_t *t)
{
    const laplace_relation_def_t *def = NULL;
    hash128_t                     canon;

    if (laplace_relation_lookup(t, &def) == 0 && def != NULL &&
        def->canonical != NULL &&
        laplace_relation_type_id(def->canonical, &canon) >= 0)
        return canon;
    if (laplace_relation_metadata_lookup_c(t, &canon, NULL, NULL, NULL))
        return canon;
    return *t;
}

static TaxEdgeOutRow *
tax_edges_batch_c(const TaxSubjOrd *subj_items, int n_subj,
                  const hash128_t *types, int n_types,
                  int *out_count)
{
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, prev;
    HTAB               *rel_reg;
    HTAB               *allowed;
    HTAB               *by_subj;
    HASHCTL             ctl;
    hash128_t          *subj_ids;
    ArrayType          *subj_b3;
    ArrayType          *types_b3;
    LaplaceEdgeRawRow  *raw;
    int                 n_raw = 0;
    int                *next;
    hash128_t          *kept_canon;
    int                 n_keep = 0;
    TaxEdgeOutRow      *out = NULL;
    int                 n_out = 0;

    *out_count = 0;
    if (subj_items == NULL || n_subj <= 0 || types == NULL || n_types <= 0)
        return NULL;

    if (!laplace_highway_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("consensus_taxonomy_edges_batch requires the highway perfcache")));

    work = AllocSetContextCreate(caller, "tax_edges_batch", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = work;
    rel_reg = hash_create("tax rel registry", 256, &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int bit = 0; bit < 256; bit++)
    {
        const char                   *canonical = NULL;
        float                         rank = 0.0f;
        uint8_t                       band = 0;
        hash128_t                     tid;
        const laplace_relation_def_t *def = NULL;
        bool                          found = false;

        if (highway_table_relation_by_bit((uint8_t) bit, &canonical, &rank, &band) != 0)
            continue;
        if (laplace_relation_type_id(canonical, &tid) < 0 ||
            laplace_relation_lookup(&tid, &def) != 0 || def == NULL)
            continue;
        (void) hash_search(rel_reg, &tid, HASH_ENTER, &found);
    }

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = work;
    allowed = hash_create("tax allowed types", Max(n_types, 16), &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = 0; i < n_types; i++)
    {
        bool found = false;
        (void) hash_search(allowed, &types[i], HASH_ENTER, &found);
    }

    subj_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_subj);
    for (int i = 0; i < n_subj; i++)
        subj_ids[i] = subj_items[i].id;

    subj_b3 = hash128_array_from_ids(subj_ids, n_subj);
    types_b3 = hash128_array_from_ids(types, n_types);
    raw = laplace_edges_raw_batch_c(subj_b3, "out", types_b3, true, 64, false, "eff_mu", &n_raw);
    pfree(subj_b3);
    pfree(types_b3);

    if (raw != NULL && n_raw > 0)
    {
        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(hash128_t);
        ctl.entrysize = sizeof(TaxSubjBucket);
        ctl.hcxt = work;
        by_subj = hash_create("tax by_subj", Max(n_subj, 16), &ctl,
                              HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        next = (int *) palloc(sizeof(int) * (Size) n_raw);
        kept_canon = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_raw);

        for (int i = 0; i < n_raw; i++)
        {
            const LaplaceEdgeRawRow *e = &raw[i];
            hash128_t                ct;
            bool                     bfound = false;
            TaxSubjBucket           *bkt;

            next[i] = -1;
            if (e->neighbour_null)
                continue;
            if (hash_search(rel_reg, &e->neighbour_id, HASH_FIND, NULL) != NULL)
                continue;
            ct = tax_resolve_canon_type(&e->type_id);
            if (hash_search(allowed, &ct, HASH_FIND, NULL) == NULL)
                continue;

            kept_canon[i] = ct;
            bkt = (TaxSubjBucket *) hash_search(by_subj, &e->subject_id, HASH_ENTER, &bfound);
            if (!bfound)
            {
                bkt->head = i;
                bkt->tail = i;
            }
            else
            {
                next[bkt->tail] = i;
                bkt->tail = i;
            }
            n_keep++;
        }

        if (n_keep > 0)
        {
            int cap = 0;
            for (int s = 0; s < n_subj; s++)
            {
                TaxSubjBucket *bkt = (TaxSubjBucket *) hash_search(by_subj, &subj_items[s].id, HASH_FIND, NULL);
                for (int idx = bkt ? bkt->head : -1; idx >= 0; idx = next[idx])
                    cap++;
            }
            if (cap > 0)
            {
                out = (TaxEdgeOutRow *) MemoryContextAlloc(caller, sizeof(TaxEdgeOutRow) * (Size) cap);
                for (int s = 0; s < n_subj; s++)
                {
                    TaxSubjBucket *bkt = (TaxSubjBucket *) hash_search(by_subj, &subj_items[s].id, HASH_FIND, NULL);
                    for (int idx = bkt ? bkt->head : -1; idx >= 0; idx = next[idx])
                    {
                        const LaplaceEdgeRawRow *e = &raw[idx];
                        out[n_out++] = (TaxEdgeOutRow) {
                            .ord = subj_items[s].ord,
                            .object_id = e->neighbour_id,
                            .type_id = kept_canon[idx],
                            .rating = e->rating,
                            .rd = e->rd,
                            .relation_null = e->relation_null,
                            .relation_id = e->relation_id
                        };
                    }
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    *out_count = n_out;
    return out;
}

Datum
pg_laplace_consensus_taxonomy_edges_batch(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType     *p_subjects;
    ArrayType     *p_types;
    Datum         *s_d = NULL, *t_d = NULL;
    bool          *s_n = NULL, *t_n = NULL;
    int            ns_raw = 0, nt_raw = 0;
    TaxSubjOrd    *subj_items;
    int            n_subj = 0;
    hash128_t     *type_ids;
    int            n_types = 0;
    TaxEdgeOutRow *rows;
    int            n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;

    p_subjects = PG_GETARG_ARRAYTYPE_P(0);
    p_types = PG_GETARG_ARRAYTYPE_P(1);

    deconstruct_array(p_subjects, BYTEAOID, -1, false, TYPALIGN_INT, &s_d, &s_n, &ns_raw);
    deconstruct_array(p_types, BYTEAOID, -1, false, TYPALIGN_INT, &t_d, &t_n, &nt_raw);

    if (ns_raw == 0 || nt_raw == 0)
    {
        pfree(s_d);
        pfree(s_n);
        pfree(t_d);
        pfree(t_n);
        return (Datum) 0;
    }

    subj_items = (TaxSubjOrd *) palloc(sizeof(TaxSubjOrd) * (Size) ns_raw);
    for (int i = 0; i < ns_raw; i++)
    {
        if (!s_n[i])
        {
            subj_items[n_subj++] = (TaxSubjOrd) {
                .id = datum_to_hash128(s_d[i]),
                .ord = (int64) (i + 1)
            };
        }
    }

    type_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) nt_raw);
    for (int i = 0; i < nt_raw; i++)
    {
        if (!t_n[i])
            type_ids[n_types++] = datum_to_hash128(t_d[i]);
    }

    pfree(s_d);
    pfree(s_n);
    pfree(t_d);
    pfree(t_n);

    if (n_subj == 0 || n_types == 0)
    {
        pfree(subj_items);
        pfree(type_ids);
        return (Datum) 0;
    }

    rows = tax_edges_batch_c(subj_items, n_subj, type_ids, n_types, &n_rows);
    pfree(subj_items);
    pfree(type_ids);

    for (int i = 0; i < n_rows; i++)
    {
        Datum vals[6];
        bool  nulls[6] = { false, false, false, false, false, false };

        vals[0] = Int64GetDatum(rows[i].ord);
        vals[1] = hash128_to_datum(&rows[i].object_id);
        vals[2] = hash128_to_datum(&rows[i].type_id);
        vals[3] = Int64GetDatum(rows[i].rating);
        vals[4] = Int64GetDatum(rows[i].rd);
        if (rows[i].relation_null)
        {
            vals[5] = (Datum) 0;
            nulls[5] = true;
        }
        else
        {
            vals[5] = hash128_to_datum(&rows[i].relation_id);
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
    }

    if (rows != NULL)
        pfree(rows);
    return (Datum) 0;
}

typedef struct TaxBestDefEntry
{
    hash128_t key;
    int64     mu_fp;
    hash128_t def_id;
} TaxBestDefEntry;

typedef struct TaxDefLabelEntry
{
    hash128_t key;
    Datum     label;
    bool      is_null;
} TaxDefLabelEntry;

static ArrayType *
tax_synset_gloss_batch_c(ArrayType *p_synsets)
{
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    Datum         *in_d = NULL;
    bool          *in_n = NULL;
    int            n_in = 0;
    hash128_t     *in_ids;
    bool          *in_valid;
    hash128_t     *uniq_syns;
    int            n_uniq = 0;
    hash128_t     *def_types;
    int            n_def_types = 0;
    HTAB          *best_defs = NULL;
    HTAB          *def_labels = NULL;
    HASHCTL        ctl;
    Datum         *out_vals;
    bool          *out_nulls;
    int            dims[1];
    int            lbs[1];
    ArrayType     *res;

    if (p_synsets == NULL)
        return construct_empty_array(TEXTOID);

    deconstruct_array(p_synsets, BYTEAOID, -1, false, TYPALIGN_INT, &in_d, &in_n, &n_in);
    if (n_in == 0)
    {
        pfree(in_d);
        pfree(in_n);
        return construct_empty_array(TEXTOID);
    }

    work = AllocSetContextCreate(caller, "tax_synset_gloss_batch", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    in_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_in);
    in_valid = (bool *) palloc0(sizeof(bool) * (Size) n_in);
    uniq_syns = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_in);

    {
        HTAB *seen;
        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(hash128_t);
        ctl.entrysize = sizeof(hash128_t);
        ctl.hcxt = work;
        seen = hash_create("tax gloss uniq syns", Max(n_in, 16), &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

        for (int i = 0; i < n_in; i++)
        {
            if (!in_n[i])
            {
                bool found = false;
                in_ids[i] = datum_to_hash128(in_d[i]);
                in_valid[i] = true;
                (void) hash_search(seen, &in_ids[i], HASH_ENTER, &found);
                if (!found)
                    uniq_syns[n_uniq++] = in_ids[i];
            }
        }
    }

    if (n_uniq > 0)
    {
        size_t max_dt = laplace_relation_table_count > 0 ? laplace_relation_table_count : 1;
        def_types = (hash128_t *) palloc(sizeof(hash128_t) * max_dt);
        for (size_t i = 0; i < laplace_relation_table_count; i++)
        {
            hash128_t tid;
            int       in_fam = 0;

            if (laplace_relation_table[i].canonical == NULL)
                continue;
            if (laplace_relation_type_id(laplace_relation_table[i].canonical, &tid) < 0)
                continue;
            if (laplace_relation_in_family(&tid, "HAS_DEFINITION", &in_fam) == 0 && in_fam != 0)
                def_types[n_def_types++] = tid;
        }

        if (n_def_types > 0)
        {
            ArrayType         *subj_b3 = hash128_array_from_ids(uniq_syns, n_uniq);
            ArrayType         *dt_b3 = hash128_array_from_ids(def_types, n_def_types);
            int                n_edges = 0;
            LaplaceEdgeRawRow *edges = laplace_edges_raw_batch_c(
                subj_b3, "out", dt_b3, true, 8, false, "eff_mu", &n_edges);
            pfree(subj_b3);
            pfree(dt_b3);

            if (edges != NULL && n_edges > 0)
            {
                hash128_t *uniq_defs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_edges);
                int        n_udefs = 0;
                HTAB      *dseen;
                HASH_SEQ_STATUS seq;
                TaxBestDefEntry *be;

                MemSet(&ctl, 0, sizeof(ctl));
                ctl.keysize = sizeof(hash128_t);
                ctl.entrysize = sizeof(TaxBestDefEntry);
                ctl.hcxt = work;
                best_defs = hash_create("tax gloss best defs", Max(n_uniq, 16), &ctl,
                                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

                for (int i = 0; i < n_edges; i++)
                {
                    const LaplaceEdgeRawRow *e = &edges[i];
                    int64                    mu_fp;
                    bool                     found = false;
                    TaxBestDefEntry         *ent;

                    if (e->neighbour_null)
                        continue;
                    mu_fp = laplace_effective_mu_fp(e->rating, e->rd);
                    ent = (TaxBestDefEntry *) hash_search(best_defs, &e->subject_id, HASH_ENTER, &found);
                    if (!found || mu_fp > ent->mu_fp ||
                        (mu_fp == ent->mu_fp && memcmp(&e->neighbour_id, &ent->def_id, sizeof(hash128_t)) < 0))
                    {
                        ent->mu_fp = mu_fp;
                        ent->def_id = e->neighbour_id;
                    }
                }

                MemSet(&ctl, 0, sizeof(ctl));
                ctl.keysize = sizeof(hash128_t);
                ctl.entrysize = sizeof(hash128_t);
                ctl.hcxt = work;
                dseen = hash_create("tax gloss uniq defs", Max(n_uniq, 16), &ctl,
                                    HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                hash_seq_init(&seq, best_defs);
                while ((be = (TaxBestDefEntry *) hash_seq_search(&seq)) != NULL)
                {
                    bool df = false;
                    (void) hash_search(dseen, &be->def_id, HASH_ENTER, &df);
                    if (!df)
                        uniq_defs[n_udefs++] = be->def_id;
                }

                if (n_udefs > 0)
                {
                    Datum     *def_datums = (Datum *) palloc(sizeof(Datum) * (Size) n_udefs);
                    ArrayType *def_arr;
                    Datum      lbl_arr_d;

                    for (int i = 0; i < n_udefs; i++)
                        def_datums[i] = hash128_to_datum(&uniq_defs[i]);
                    def_arr = construct_array(def_datums, n_udefs, BYTEAOID, -1, false, TYPALIGN_INT);
                    lbl_arr_d = spi_invoke_public_label("laplace_label_array", BYTEAARRAYOID,
                                                        PointerGetDatum(def_arr), true);
                    if (lbl_arr_d != (Datum) 0)
                    {
                        Datum *ld = NULL;
                        bool  *ln = NULL;
                        int    nl = 0;

                        deconstruct_array(DatumGetArrayTypeP(lbl_arr_d), TEXTOID,
                                          -1, false, TYPALIGN_INT, &ld, &ln, &nl);
                        MemSet(&ctl, 0, sizeof(ctl));
                        ctl.keysize = sizeof(hash128_t);
                        ctl.entrysize = sizeof(TaxDefLabelEntry);
                        ctl.hcxt = work;
                        def_labels = hash_create("tax gloss def labels", Max(n_udefs, 16), &ctl,
                                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                        for (int i = 0; i < n_udefs && i < nl; i++)
                        {
                            bool              lf = false;
                            TaxDefLabelEntry *le = (TaxDefLabelEntry *)
                                hash_search(def_labels, &uniq_defs[i], HASH_ENTER, &lf);
                            le->is_null = ln[i];
                            le->label = ln[i] ? (Datum) 0 : ld[i];
                        }
                    }
                }
            }
        }
    }

    out_vals = (Datum *) palloc0(sizeof(Datum) * (Size) n_in);
    out_nulls = (bool *) palloc(sizeof(bool) * (Size) n_in);
    for (int i = 0; i < n_in; i++)
    {
        out_nulls[i] = true;
        if (!in_valid[i] || best_defs == NULL || def_labels == NULL)
            continue;
        {
            TaxBestDefEntry *be = (TaxBestDefEntry *) hash_search(best_defs, &in_ids[i], HASH_FIND, NULL);
            if (be != NULL)
            {
                TaxDefLabelEntry *le = (TaxDefLabelEntry *) hash_search(def_labels, &be->def_id, HASH_FIND, NULL);
                if (le != NULL && !le->is_null)
                {
                    out_vals[i] = datumCopy(le->label, false, -1);
                    out_nulls[i] = false;
                }
            }
        }
    }

    MemoryContextSwitchTo(caller);
    dims[0] = n_in;
    lbs[0] = 1;
    res = construct_md_array(out_vals, out_nulls, 1, dims, lbs, TEXTOID, -1, false, TYPALIGN_INT);

    MemoryContextDelete(work);
    pfree(in_d);
    pfree(in_n);
    return res;
}

Datum
pg_laplace_synset_gloss_batch(PG_FUNCTION_ARGS)
{
    ArrayType *p_synsets;

    if (PG_ARGISNULL(0))
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));
    p_synsets = PG_GETARG_ARRAYTYPE_P(0);
    PG_RETURN_ARRAYTYPE_P(tax_synset_gloss_batch_c(p_synsets));
}

int
tax_idx_find(HTAB *map, const hash128_t *h)
{
    bool         found;
    TaxIdxEntry *e = (TaxIdxEntry *) hash_search(map, h, HASH_FIND, &found);

    return found ? e->idx : -1;
}

void
tax_idx_add(HTAB *map, const hash128_t *h, int idx)
{
    bool         found;
    TaxIdxEntry *e = (TaxIdxEntry *) hash_search(map, h, HASH_ENTER, &found);

    e->idx = idx;
}

static Datum
spi_top_synset(Datum word)
{
    hash128_t         w = datum_to_hash128(word);
    int               n_bu = 0;
    LaplaceBubbleRow *rows = laplace_bubble_up_batch_c(&w, 1, NULL, 0, true, 1, false, &n_bu);
    Datum             res = (Datum) 0;

    if (n_bu > 0 && rows != NULL)
        res = hash128_to_datum(&rows[0].synset_id);
    if (rows != NULL)
        pfree(rows);
    return res;
}

static bool
in_ancestor_chain(TaxNode *nodes, int cur, const hash128_t *target)
{
    while (cur >= 0)
    {
        if (hash128_eq(&nodes[cur].id, target))
            return true;
        cur = nodes[cur].parent;
    }
    return false;
}

int
tax_bfs_up_weighted(const hash128_t *seeds,
                    const int64_t *seed_mu,
                    const bool *seed_mu_valid,
                    int seed_n, int max_depth,
                    const hash128_t *up_types, int up_type_n,
                    TaxNode **nodes_out)
{
    int    tail = 0;
    int    n = 0;
    int    node_cap = TAX_WALK_INITIAL;
    int    queue_cap = TAX_WALK_INITIAL;
    TaxNode *nodes = (TaxNode *) palloc(sizeof(TaxNode) * node_cap);
    int   *queue = (int *) palloc(sizeof(int) * queue_cap);
    HTAB  *idmap;
    HASHCTL ctl;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(TaxIdxEntry);
    idmap = hash_create("tax_bfs_up idmap", TAX_WALK_INITIAL, &ctl,
                        HASH_ELEM | HASH_BLOBS);

    for (int s = 0; s < seed_n; s++)
    {
        if (n >= node_cap)
        {
            node_cap *= 2;
            nodes = (TaxNode *) repalloc(nodes, sizeof(TaxNode) * node_cap);
        }
        if (tail >= queue_cap)
        {
            queue_cap *= 2;
            queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
        }
        {
            int existing = tax_idx_find(idmap, &seeds[s]);
            bool valid = seed_mu_valid != NULL && seed_mu_valid[s];

            if (existing >= 0)
            {
                if (valid && (!nodes[existing].path_mu_valid ||
                              seed_mu[s] > nodes[existing].path_mu))
                {
                    nodes[existing].path_mu = seed_mu[s];
                    nodes[existing].path_mu_valid = true;
                }
                continue;
            }
        }
        nodes[n].id = seeds[s];
        nodes[n].depth = 0;
        nodes[n].parent = -1;
        nodes[n].via_type = (hash128_t) { 0, 0 };
        nodes[n].rating = 0;
        nodes[n].rd = 0;
        nodes[n].path_mu = seed_mu != NULL ? seed_mu[s] : 0;
        nodes[n].path_mu_valid = seed_mu_valid != NULL && seed_mu_valid[s];
        tax_idx_add(idmap, &nodes[n].id, n);
        queue[tail++] = n++;
    }

    /* Level-synchronous BFS: one set read per depth level, rows in frontier
     * order. A node already reached at a shallower depth is kept; at equal
     * depth the wider bottleneck mu takes the parent; a deeper node is
     * re-parented to the shallower path and re-queued. */
    {
        int level_begin = 0;
        int level_end = tail;

        for (int depth = 0; depth < max_depth && level_begin < level_end; depth++)
        {
            int            frontier_n = level_end - level_begin;
            int            walk_depth = depth + 1;
            TaxSubjOrd    *frontier_items = (TaxSubjOrd *) palloc(sizeof(TaxSubjOrd) * (Size) frontier_n);
            TaxEdgeOutRow *edges;
            int            n_edges = 0;

            for (int i = 0; i < frontier_n; i++)
            {
                frontier_items[i] = (TaxSubjOrd) {
                    .id = nodes[queue[level_begin + i]].id,
                    .ord = (int64) (i + 1)
                };
            }

            edges = tax_edges_batch_c(frontier_items, frontier_n, up_types, up_type_n, &n_edges);
            pfree(frontier_items);

            for (int r = 0; r < n_edges; r++)
            {
                const TaxEdgeOutRow *er = &edges[r];
                int64                ord = er->ord;
                int                  cur;
                hash128_t            obj_h = er->object_id;
                int                  pi;
                hash128_t            via_type = er->relation_null ? er->type_id : er->relation_id;
                int64                rating = er->rating;
                int64                rd = er->rd;
                int64                edge_mu;
                int64                candidate_mu;
                bool                 candidate_valid = true;

                if (ord < 1 || ord > frontier_n)
                    continue;
                cur = queue[level_begin + (int) ord - 1];

                if (in_ancestor_chain(nodes, cur, &obj_h))
                    continue;

                edge_mu = eff_mu_display_fp(rating, rd);
                candidate_mu = nodes[cur].path_mu_valid
                    ? Min(nodes[cur].path_mu, edge_mu)
                    : edge_mu;

                pi = tax_idx_find(idmap, &obj_h);
                if (pi >= 0)
                {
                    if (nodes[pi].depth < walk_depth)
                        continue;
                    if (nodes[pi].depth == walk_depth)
                    {
                        if (!candidate_valid ||
                            (nodes[pi].path_mu_valid &&
                             candidate_mu <= nodes[pi].path_mu))
                            continue;
                        nodes[pi].parent = cur;
                        nodes[pi].via_type = via_type;
                        nodes[pi].rating = rating;
                        nodes[pi].rd = rd;
                        nodes[pi].path_mu = candidate_mu;
                        nodes[pi].path_mu_valid = true;
                        continue;
                    }
                    nodes[pi].depth = walk_depth;
                    nodes[pi].parent = cur;
                    nodes[pi].via_type = via_type;
                    nodes[pi].rating = rating;
                    nodes[pi].rd = rd;
                    nodes[pi].path_mu = candidate_mu;
                    nodes[pi].path_mu_valid = candidate_valid;
                    if (tail >= queue_cap)
                    {
                        queue_cap *= 2;
                        queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
                    }
                    queue[tail++] = pi;
                    continue;
                }

                if (n >= node_cap)
                {
                    node_cap *= 2;
                    nodes = (TaxNode *) repalloc(nodes, sizeof(TaxNode) * node_cap);
                }
                if (tail >= queue_cap)
                {
                    queue_cap *= 2;
                    queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
                }

                nodes[n].id = obj_h;
                nodes[n].depth = walk_depth;
                nodes[n].parent = cur;
                nodes[n].via_type = via_type;
                nodes[n].rating = rating;
                nodes[n].rd = rd;
                nodes[n].path_mu = candidate_mu;
                nodes[n].path_mu_valid = candidate_valid;
                tax_idx_add(idmap, &nodes[n].id, n);
                queue[tail++] = n++;
            }

            if (edges != NULL)
                pfree(edges);
            level_begin = level_end;
            level_end = tail;
        }
    }

    hash_destroy(idmap);
    pfree(queue);
    *nodes_out = nodes;
    return n;
}

int
tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
           const hash128_t *up_types, int up_type_n,
           TaxNode **nodes_out)
{
    return tax_bfs_up_weighted(seeds, NULL, NULL, seed_n, max_depth,
                               up_types, up_type_n, nodes_out);
}

void
reconstruct_path(TaxNode *nodes, int idx, Datum **path, Datum **types, Datum *path_mu)
{
    int depth = nodes[idx].depth;
    int n = depth + 1;
    int i = idx;
    Datum mu = nodes[idx].path_mu_valid
        ? fp_display_numeric(nodes[idx].path_mu)
        : (Datum) 0;

    *path = (Datum *) palloc(sizeof(Datum) * n);
    *types = (Datum *) palloc(sizeof(Datum) * depth);

    for (int slot = depth; slot >= 0; slot--)
    {
        (*path)[slot] = hash128_to_datum(&nodes[i].id);
        if (slot > 0)
        {
            (*types)[slot - 1] = hash128_to_datum(&nodes[i].via_type);
            i = nodes[i].parent;
        }
    }
    *path_mu = mu;
}

void
tax_fetch_relation_set(const char *name, hash128_t **types_out, int *n_out)
{
    const hash128_t *slice = NULL;
    size_t           n = 0;
    hash128_t       *types;

    if (laplace_relation_set_ids(name, &slice, &n) != 0)
        elog(ERROR, "relate_path_raw: relation set %s is unavailable", name);
    types = (hash128_t *) palloc(sizeof(hash128_t) * (n > 0 ? n : 1));
    if (n > 0)
        memcpy(types, slice, sizeof(hash128_t) * n);
    *types_out = types;
    *n_out = (int) n;
}

Datum
pg_laplace_hypernyms(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          word;
    int32          max_depth;
    Datum          start;
    TaxNode       *nodes;
    int            n_nodes;
    Datum          lang;
    hash128_t     *up_types;
    int            n_up;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("hypernyms: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    max_depth = PG_ARGISNULL(1) ? 8 : PG_GETARG_INT32(1);
    if (max_depth < 0)
        ereport(ERROR, (errmsg("hypernyms: p_depth must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);

    start = spi_top_synset(word);
    if (start == (Datum) 0)
        return (Datum) 0;

    lang = spi_word_language(word);
    tax_fetch_relation_set("PATH_UPWARD", &up_types, &n_up);

    {
        hash128_t seed = datum_to_hash128(start);
        n_nodes = tax_bfs_up(&seed, 1, max_depth, up_types, n_up, &nodes);
    }

    /* REALIZE the emitted ancestors as one set: labels through
     * spi_realize_batch, glosses through one native batch lookup. */
    {
        int    n_emit = 0;
        int   *emit_idx = (int *) palloc(sizeof(int) * (n_nodes > 0 ? n_nodes : 1));
        Datum *emit_ids = (Datum *) palloc(sizeof(Datum) * (n_nodes > 0 ? n_nodes : 1));
        Datum *labels = NULL;
        bool  *label_nulls = NULL;
        Datum *glosses = NULL;
        bool  *gloss_nulls = NULL;
        int    n_labels = 0, n_glosses = 0;

        for (int i = 0; i < n_nodes; i++)
        {
            if (nodes[i].depth == 0)
                continue;
            emit_idx[n_emit] = i;
            emit_ids[n_emit] = hash128_to_datum(&nodes[i].id);
            n_emit++;
        }

        if (n_emit > 0)
        {
            ArrayType *ids_arr = construct_array(emit_ids, n_emit, BYTEAOID,
                                                 -1, false, TYPALIGN_INT);
            Datum      label_arr;
            ArrayType *gloss_arr;

            label_arr = spi_realize_batch(PointerGetDatum(ids_arr), lang);
            if (label_arr != (Datum) 0)
                deconstruct_array(DatumGetArrayTypePCopy(label_arr), TEXTOID,
                                  -1, false, TYPALIGN_INT,
                                  &labels, &label_nulls, &n_labels);

            gloss_arr = tax_synset_gloss_batch_c(ids_arr);
            if (gloss_arr != NULL)
                deconstruct_array(gloss_arr, TEXTOID,
                                  -1, false, TYPALIGN_INT,
                                  &glosses, &gloss_nulls, &n_glosses);
        }

        for (int e = 0; e < n_emit; e++)
        {
            Datum values[3];
            bool  nulls[3] = { false, false, false };
            int   i = emit_idx[e];

            values[0] = Int32GetDatum(nodes[i].depth);
            if (labels != NULL && e < n_labels && !label_nulls[e])
                values[1] = labels[e];
            else
                nulls[1] = true;
            if (glosses != NULL && e < n_glosses && !gloss_nulls[e])
                values[2] = glosses[e];
            else
                nulls[2] = true;

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    return (Datum) 0;
}

Datum
pg_laplace_isa_path(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          x, y;
    int32          max_depth;
    TaxNode       *nodes = NULL;
    int            n_nodes = 0;
    hash128_t     *up_types;
    int            n_up;
    hash128_t     *targets;
    int            n_targets = 0;
    int            best = -1;
    int            best_len = INT_MAX;
    Datum          best_mu = (Datum) 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("isa_path: endpoints must not be NULL")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 8 : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    tax_fetch_relation_set("ISA_UPWARD", &up_types, &n_up);

    {
        Datum             *synsets;
        int                ns;
        hash128_t          y_id = datum_to_hash128(y);
        hash128_t          trans_id;
        ArrayType         *y_b3;
        ArrayType         *t_b3;
        LaplaceEdgeRawRow *tr = NULL;
        int                n_tr = 0;

        spi_fetch_synset_ids(y, &synsets, &ns);
        if (laplace_relation_type_id("IS_TRANSLATION_OF", &trans_id) < 0)
            elog(ERROR, "isa_path: relation IS_TRANSLATION_OF unavailable");
        y_b3 = hash128_array_from_ids(&y_id, 1);
        t_b3 = hash128_array_from_ids(&trans_id, 1);
        tr = laplace_edges_raw_batch_c(y_b3, "in", t_b3, false, 0, false, "eff_mu", &n_tr);
        pfree(y_b3);
        pfree(t_b3);

        if ((uint64) n_tr + (uint64) ns + 2 > (uint64) INT_MAX ||
            (uint64) n_tr + (uint64) ns + 2 >
                (uint64) (MaxAllocSize / sizeof(hash128_t)))
            ereport(ERROR,
                    (errmsg("isa_path: target frontier exceeds PostgreSQL allocation capacity")));

        targets = (hash128_t *) palloc(
            sizeof(hash128_t) * ((Size) ns + (Size) n_tr + 2));
        targets[n_targets++] = datum_to_hash128(x);
        for (int i = 0; i < ns; i++)
            targets[n_targets++] = datum_to_hash128(synsets[i]);
        targets[n_targets++] = y_id;
        for (int r = 0; r < n_tr; r++)
        {
            if (!tr[r].neighbour_null)
                targets[n_targets++] = tr[r].neighbour_id;
        }
        if (tr != NULL)
            pfree(tr);
    }

    {
        hash128_t *starts;
        int        n_starts = 0;
        Datum     *synsets;
        int        ns;

        spi_fetch_synset_ids(x, &synsets, &ns);
        starts = (hash128_t *) palloc(sizeof(hash128_t) * ((Size) ns + 1));
        starts[n_starts++] = datum_to_hash128(x);
        for (int i = 0; i < ns; i++)
            starts[n_starts++] = datum_to_hash128(synsets[i]);
        n_nodes = tax_bfs_up(starts, n_starts, max_depth, up_types, n_up, &nodes);
        pfree(starts);
    }

    for (int i = 0; i < n_nodes; i++)
    {
        int path_len;
        Datum path_mu;

        if (nodes[i].depth <= 0)
            continue;
        if (tax_find(targets, n_targets, &nodes[i].id) < 0)
            continue;

        path_len = nodes[i].depth + 1;
        {
            Datum *path, *types;
            reconstruct_path(nodes, i, &path, &types, &path_mu);
            if (path_len < best_len ||
                (path_len == best_len && best_mu != (Datum) 0 && path_mu != (Datum) 0 &&
                 DatumGetInt32(DirectFunctionCall2(numeric_cmp, path_mu, best_mu)) > 0))
            {
                best = i;
                best_len = path_len;
                best_mu = path_mu;
            }
            pfree(path);
            pfree(types);
        }
    }

    if (best >= 0)
    {
        Datum  values[3];
        bool   nulls[3] = { false, false, false };
        Datum *path, *types;

        reconstruct_path(nodes, best, &path, &types, &best_mu);
        values[0] = PointerGetDatum(construct_array(path, best_len, BYTEAOID,
                                                  -1, false, TYPALIGN_INT));
        values[1] = PointerGetDatum(construct_array(types, best_len - 1, BYTEAOID,
                                                    -1, false, TYPALIGN_INT));
        values[2] = best_mu;
        if (best_mu == (Datum) 0)
            nulls[2] = true;
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        pfree(path);
        pfree(types);
    }

    return (Datum) 0;
}
