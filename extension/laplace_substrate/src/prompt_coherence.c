/*
 * Joint sense election for one observation. The candidate senses of every
 * resolved token are scored against each other through the consensus cells
 * they share:
 *
 *   coherence   rank-weighted effective mu of cells joining a candidate to a
 *               candidate of a different token, read in both directions;
 *   total_mass  the same weight over every cell incident to the candidate,
 *               the denominator that makes coherence a scale-free share;
 *   rel_mass    weight of the candidate's cells in a relation type that a
 *               different token names.
 *
 * A token names a relation type when its content id is the id of the longest
 * segment of the type's canonical name, of a fixed alias of that segment, or
 * of a lemma it is IS_LEMMA_OF-linked to. Each token emits its one elected
 * sense with the keys the election used. SQL fetches sets in index range
 * reads; C folds them with O(1) hash probes. No floors, caps or top-k; ties
 * end on the sense id.
 */

#include "prompt_coherence_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_prompt_coherence);

static SPIPlanPtr pc_cand_plan = NULL;
static SPIPlanPtr pc_single_mass_plan = NULL;
static SPIPlanPtr pc_lemma_plan = NULL;
static SPIPlanPtr pc_rel_plan = NULL;

Datum
pg_laplace_prompt_coherence(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt;
    MemoryContext  work, old;
    HASHCTL        ctl;
    HTAB          *syn_h, *tok_h, *type_h, *peer_h, *namer_h;
    PcCand        *cands = NULL;
    int            n_cand = 0, cap_cand = 0;
    Datum         *syn_datums = NULL;
    int            n_syn = 0;
    ArrayType     *syn_arr;
    bool           spi_top = false;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt = PG_GETARG_TEXT_PP(0);

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "prompt_coherence: SPI_connect failed");

    work = AllocSetContextCreate(CurrentMemoryContext, "prompt_coherence",
                                 ALLOCSET_DEFAULT_SIZES);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PcSynEntry);
    ctl.hcxt = work;
    syn_h = hash_create("pc syn", 512, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PcTokEntry);
    ctl.hcxt = work;
    tok_h = hash_create("pc tok", 64, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PcTypeEntry);
    ctl.hcxt = work;
    type_h = hash_create("pc type", 256, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(PcPeerKey);
    ctl.entrysize = sizeof(PcPeerEntry);
    ctl.hcxt = work;
    peer_h = hash_create("pc peers", 512, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(int32);
    ctl.entrysize = sizeof(PcOrdEntry);
    ctl.hcxt = work;
    namer_h = hash_create("pc namers", 64, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* One complete candidate set; the row arena does not own retained candidates. */
    {
        Oid types[] = {TEXTOID};
        Datum args[] = {PointerGetDatum(prompt)};
        PcCandidateRead read = {syn_h, tok_h, cands, n_cand, cap_cand, work};
        laplace_spi_stream(ensure_pc_plan(&pc_cand_plan,
            laplace_sql_query_text("prompt.coherence_candidates"), 1, types, "pc_cand"),
            1, types, args, NULL, pc_receive_candidate, &read);
        cands = read.cands; n_cand = read.count; cap_cand = read.capacity;
    }

    if (n_cand == 0)
    {
        MemoryContextDelete(work);
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    {
        HASH_SEQ_STATUS seq;
        PcSynEntry     *se;
        long            count = hash_get_num_entries(syn_h);

        if (count <= 0 || (uint64) count > (uint64) INT_MAX ||
            (Size) count > MaxAllocSize / sizeof(Datum))
            ereport(ERROR,
                    (errmsg("prompt_coherence: unique candidate set exceeds PostgreSQL array capacity")));
        old = MemoryContextSwitchTo(work);
        syn_datums = (Datum *) palloc(sizeof(Datum) * count);
        hash_seq_init(&seq, syn_h);
        while ((se = (PcSynEntry *) hash_seq_search(&seq)) != NULL)
        {
            bytea *cp = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(cp, VARHDRSZ + 16);
            memcpy(VARDATA(cp), se->key, 16);
            syn_datums[n_syn++] = PointerGetDatum(cp);
        }
        syn_arr = construct_array(syn_datums, n_syn, BYTEAOID, -1, false, TYPALIGN_INT);
        MemoryContextSwitchTo(old);
    }

    /* ---- single-token fast path: coherence and relation naming are zero when
     * only one token resolved; compute total_mass via set aggregation in SQL
     * instead of streaming every incident edge into C. ---- */
    {
        bool  multi_tok = false;
        int32 first_ord = cands[0].ord;

        for (int i = 1; i < n_cand; i++)
        {
            if (cands[i].ord != first_ord)
            {
                multi_tok = true;
                break;
            }
        }

        if (!multi_tok)
        {
            Oid   argtypes[1] = { BYTEAARRAYOID };
            Datum args[1] = { PointerGetDatum(syn_arr) };
            int   rc = SPI_execute_plan(
                ensure_pc_plan(
                    &pc_single_mass_plan,
                    "SELECT m.id, sum(m.mass)::float8 FROM ("
                    "  SELECT c.subject_id AS id, "
                    "         sum(COALESCE(consensus.relation_rank(c.type_id), 0.0) "
                    "             * (consensus.eff_mu(c.rating, c.rd)::float8)) AS mass "
                    "  FROM laplace.consensus c WHERE c.subject_id = ANY($1) GROUP BY c.subject_id "
                    "  UNION ALL "
                    "  SELECT c.object_id AS id, "
                    "         sum(COALESCE(consensus.relation_rank(c.type_id), 0.0) "
                    "             * (consensus.eff_mu(c.rating, c.rd)::float8)) AS mass "
                    "  FROM laplace.consensus c WHERE c.object_id = ANY($1) GROUP BY c.object_id "
                    "  UNION ALL "
                    "  SELECT e.subject_id AS id, "
                    "         sum(COALESCE(consensus.relation_rank(e.type_id), 0.0) "
                    "             * (consensus.eff_mu(e.rating, e.rd)::float8)) AS mass "
                    "  FROM consensus.edges_raw_batch($1::bytea[], 'both', NULL, 128, true, 'edge_rank') e "
                    "  WHERE e.type_id IS NOT NULL "
                    "  GROUP BY e.subject_id"
                    ") m GROUP BY m.id",
                    1, argtypes, "pc_single_mass"),
                args, NULL, true, 0);

            if (rc == SPI_OK_SELECT)
            {
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple   tup = SPI_tuptable->vals[r];
                    TupleDesc   td = SPI_tuptable->tupdesc;
                    bool        isnull;
                    bytea      *id = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
                    double      mass;
                    PcSynEntry *me;

                    if (isnull || VARSIZE_ANY_EXHDR(id) != 16)
                        continue;
                    mass = DatumGetFloat8(SPI_getbinval(tup, td, 2, &isnull));
                    if (isnull)
                        continue;
                    me = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(id), HASH_FIND, NULL);
                    if (me == NULL)
                        continue;
                    for (int i = 0; i < me->n_idx; i++)
                        cands[me->idx[i]].total_mass += mass;
                }
                SPI_freetuptable(SPI_tuptable);
            }
            goto emit_winners;
        }
    }

    /* ---- coherence: two indexed range reads, O(1) probe per edge ---- */
    pc_scan_edges(syn_h, type_h, peer_h, cands, syn_arr, true);
    pc_scan_edges(syn_h, type_h, peer_h, cands, syn_arr, false);

    /* ---- which relation types does a token name? A name's concept segment
     * becomes a content id through the substrate's content hash; a token names
     * the type by id equality, or as the object of an IS_LEMMA_OF cell whose
     * subject is that id. ---- */
    {
        PcTypeEntry     *te;
        Datum           *nw_datums = NULL;
        int              n_nw = 0;
        PcTypeEntry    **nw_owner = NULL;
        size_t           nw_capacity = laplace_relation_table_count;

        if (nw_capacity > (size_t) INT_MAX ||
            nw_capacity > (size_t) (MaxAllocSize / sizeof(Datum)) ||
            nw_capacity > (size_t) (MaxAllocSize / sizeof(PcTypeEntry *)))
            ereport(ERROR,
                    (errmsg("prompt_coherence: relation manifest exceeds PostgreSQL array capacity")));
        old = MemoryContextSwitchTo(work);
        nw_datums = (Datum *) palloc(sizeof(Datum) * Max(nw_capacity, (size_t) 1));
        nw_owner = (PcTypeEntry **) palloc(sizeof(PcTypeEntry *) * Max(nw_capacity, (size_t) 1));
        MemoryContextSwitchTo(old);

        /* Iterate the whole manifest, not only the types the candidates carry:
         * naming selects which relation to traverse, so it cannot depend on that
         * relation already appearing among the candidates' cells. The manifest
         * is bounded, so this is a fixed cost per call. Each manifest type is
         * entered into type_h for the rel_mass pass. */
        for (size_t ri = 0; ri < laplace_relation_table_count; ri++)
        {
            const laplace_relation_def_t *def = &laplace_relation_table[ri];
            const char *name, *last;
            size_t      len;
            hash128_t   wid;
            hash128_t   type_id;
            PcTokEntry *tk;
            bool        found;

            name = def->canonical;
            if (name == NULL)
                continue;
            tk = NULL;

            /* Canonical ids come from the native law cache; the static table's
             * type_id field is not populated. */
            if (laplace_relation_type_id(name, &type_id) < 0)
                continue;
            te = (PcTypeEntry *) hash_search(type_h, &type_id, HASH_ENTER, &found);
            if (!found)
            {
                te->namer_ords = NULL;
                te->n_namers = 0;
                te->cap_namers = 0;
                te->named = false;
            }

            /* A canonical name's concept is its longest underscore-delimited
             * segment; ties keep the earlier segment. */
            {
                const char *seg = name;
                const char *best_seg = NULL;
                size_t      best_len = 0;

                for (;;)
                {
                    const char *us = strchr(seg, '_');
                    size_t      slen = us ? (size_t) (us - seg) : strlen(seg);

                    if (slen > best_len)
                    {
                        best_seg = seg;
                        best_len = slen;
                    }
                    if (us == NULL)
                        break;
                    seg = us + 1;
                }
                if (best_seg == NULL || best_len < 2)
                    continue;           /* under two characters names nothing */
                last = best_seg;
                len = best_len;
            }

            {
                char *lower = pnstrdup(last, len);

                for (size_t i = 0; i < len; i++)
                    lower[i] = pg_ascii_tolower(lower[i]);
                if (laplace_content_root_id((const uint8_t *) lower, len, &wid) != 0)
                {
                    pfree(lower);
                    continue;
                }
                /* With no token carrying the segment's id, a fixed alias of the
                 * segment is tried instead. */
                {
                    static const struct { const char *from; const char *to; } aliases[] = {
                        {"antonym", "opposite"},
                        {"definition", "define"},
                        {NULL, NULL}
                    };
                    hash128_t alias_wid;
                    int       ai;

                    tk = (PcTokEntry *) hash_search(tok_h, &wid, HASH_FIND, NULL);
                    if (tk == NULL)
                    {
                        for (ai = 0; aliases[ai].from != NULL; ai++)
                        {
                            if (strcmp(lower, aliases[ai].from) != 0)
                                continue;
                            if (laplace_content_root_id(
                                    (const uint8_t *) aliases[ai].to,
                                    strlen(aliases[ai].to), &alias_wid) == 0)
                                tk = (PcTokEntry *) hash_search(
                                    tok_h, &alias_wid, HASH_FIND, NULL);
                            break;
                        }
                    }
                }
                pfree(lower);
            }

            if (tk != NULL)
            {
                pc_type_add_namers(te, tk, namer_h, work);
                continue;
            }
            /* No direct hit: queue the name id for the lemma probe. */
            {
                bytea *b;

                if ((size_t) n_nw >= nw_capacity)
                    ereport(ERROR,
                            (errmsg("prompt_coherence: relation-name cardinality exceeded its exact manifest bound")));
                old = MemoryContextSwitchTo(work);
                b = (bytea *) palloc(VARHDRSZ + 16);
                SET_VARSIZE(b, VARHDRSZ + 16);
                memcpy(VARDATA(b), &wid, 16);
                MemoryContextSwitchTo(old);
                nw_datums[n_nw] = PointerGetDatum(b);
                nw_owner[n_nw] = te;
                n_nw++;
            }
        }

        if (n_nw > 0)
        {
            Oid        argtypes[1] = { BYTEAARRAYOID };
            Datum      args[1];
            ArrayType *nw_arr;
            int        rc;

            old = MemoryContextSwitchTo(work);
            nw_arr = construct_array(nw_datums, n_nw, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            args[0] = PointerGetDatum(nw_arr);
            rc = SPI_execute_plan(
                ensure_pc_plan(
                    &pc_lemma_plan,
                    "SELECT l.subject_id, l.object_id FROM laplace.consensus l "
                    "WHERE l.subject_id = ANY($1) "
                    "  AND l.type_id = laplace.relation_type_id('IS_LEMMA_OF')",
                    1, argtypes, "pc_lemma"),
                args, NULL, true, 0);
            if (rc == SPI_OK_SELECT)
            {
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td = SPI_tuptable->tupdesc;
                    bool      isnull;
                    bytea    *lemma, *form;
                    PcTokEntry *tk;

                    lemma = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
                    if (isnull || VARSIZE_ANY_EXHDR(lemma) != 16) continue;
                    form = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
                    if (isnull || VARSIZE_ANY_EXHDR(form) != 16) continue;

                    tk = (PcTokEntry *) hash_search(tok_h, VARDATA_ANY(form), HASH_FIND, NULL);
                    if (tk == NULL)
                        continue;
                    for (int i = 0; i < n_nw; i++)
                    {
                        bytea *nb = DatumGetByteaPP(nw_datums[i]);

                        if (memcmp(VARDATA_ANY(nb), VARDATA_ANY(lemma), 16) == 0)
                        {
                            pc_type_add_namers(nw_owner[i], tk, namer_h, work);
                        }
                    }
                }
                SPI_freetuptable(SPI_tuptable);
            }
        }
    }

    /* ---- rel_mass: one more indexed read, restricted to the named types. The
     * (subject_id, type_id) index serves it directly. A token never scores
     * itself: the namer must be a DIFFERENT ord. ---- */
    {
        HASH_SEQ_STATUS seq;
        PcTypeEntry    *te;
        Datum          *td_arr = NULL;
        int             n_td = 0;
        long            type_count = hash_get_num_entries(type_h);

        if (type_count < 0 || (uint64) type_count > (uint64) INT_MAX ||
            (Size) type_count > MaxAllocSize / sizeof(Datum))
            ereport(ERROR,
                    (errmsg("prompt_coherence: relation type set exceeds PostgreSQL array capacity")));
        old = MemoryContextSwitchTo(work);
        td_arr = (Datum *) palloc(sizeof(Datum) * Max(type_count, 1));
        MemoryContextSwitchTo(old);

        hash_seq_init(&seq, type_h);
        while ((te = (PcTypeEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (!te->named)
                continue;
            old = MemoryContextSwitchTo(work);
            {
                bytea *b = (bytea *) palloc(VARHDRSZ + 16);

                SET_VARSIZE(b, VARHDRSZ + 16);
                memcpy(VARDATA(b), te->key, 16);
                td_arr[n_td++] = PointerGetDatum(b);
            }
            MemoryContextSwitchTo(old);
        }

        if (n_td > 0)
        {
            Oid        argtypes[3] = { BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID };
            Datum      args[3];
            ArrayType *type_arr;

            old = MemoryContextSwitchTo(work);
            type_arr = construct_array(td_arr, n_td, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            args[0] = PointerGetDatum(syn_arr);
            args[1] = PointerGetDatum(type_arr);
            args[2] = PointerGetDatum(laplace_symmetric_relation_types());
            PcEdgeRead read = {syn_h, type_h, NULL, cands};
            laplace_spi_stream(ensure_pc_plan(&pc_rel_plan,
                laplace_sql_query_text("prompt.coherence_relation_mass"), 3, argtypes, "pc_rel"),
                3, argtypes, args, NULL, pc_receive_relation_mass, &read);
        }
    }

emit_winners:
    /* ---- emit one row per token: its elected sense ---- */
    {
        for (int i = 0; i < n_cand; i++)
        {
            PcCand *best = &cands[i];
            bool    is_best = true;

            for (int j = 0; j < n_cand; j++)
            {
                PcCand *o = &cands[j];

                if (j == i || o->ord != best->ord)
                    continue;
                /* Election order: coherence share, raw rel_mass, language
                 * agreement, witness count, denote_mu, total_mass, lower sense
                 * id. The share, not raw coherence, so a high-degree candidate
                 * does not win on edge count alone. Joint evidence from this
                 * observation precedes language agreement, which precedes every
                 * global quantity; an unattested language beats a disagreeing
                 * one and loses to an agreeing one. */
                double o_share    = o->total_mass    > 0.0 ? o->coherence    / o->total_mass    : 0.0;
                double best_share = best->total_mass > 0.0 ? best->coherence / best->total_mass : 0.0;

                if (o_share > best_share
                    || (o_share == best_share && o->rel_mass > best->rel_mass)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree > best->lang_agree)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses > best->witnesses)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses == best->witnesses
                        && o->denote_mu > best->denote_mu)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses == best->witnesses
                        && o->denote_mu == best->denote_mu
                        && o->total_mass > best->total_mass)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses == best->witnesses
                        && o->denote_mu == best->denote_mu
                        && o->total_mass == best->total_mass
                        && memcmp(o->syn, best->syn, 16) < 0))
                {
                    is_best = false;
                    break;
                }
            }
            if (!is_best)
                continue;

            {
                Datum values[12];
                bool  nulls[12];
                bytea *tokb = (bytea *) palloc(VARHDRSZ + 16);
                bytea *synb = (bytea *) palloc(VARHDRSZ + 16);

                SET_VARSIZE(tokb, VARHDRSZ + 16);
                memcpy(VARDATA(tokb), best->tok, 16);
                SET_VARSIZE(synb, VARHDRSZ + 16);
                memcpy(VARDATA(synb), best->syn, 16);

                MemSet(nulls, 0, sizeof(nulls));
                values[0] = Int32GetDatum(best->ord);
                values[1] = PointerGetDatum(tokb);
                values[2] = PointerGetDatum(synb);
                values[3] = Float8GetDatum(best->coherence);
                values[4] = DirectFunctionCall1(float8_numeric,
                                                Float8GetDatum(best->denote_mu));
                values[5] = Int64GetDatum(best->peer_count);
                if (best->has_rel_type)
                {
                    bytea *rt = (bytea *) palloc(VARHDRSZ + 16);

                    SET_VARSIZE(rt, VARHDRSZ + 16);
                    memcpy(VARDATA(rt), best->rel_type, 16);
                    values[6] = PointerGetDatum(rt);
                }
                else
                    nulls[6] = true;
                /* rel_mass as a share of the candidate's total mass, scale-free
                 * like specificity; raw mass is share * total_mass. */
                values[7] = Float8GetDatum(
                    best->total_mass > 0.0 ? best->rel_mass / best->total_mass : 0.0);
                /* Specificity: the share of the candidate's own mass that reaches
                 * the rest of the observation, or -1 when its token names a
                 * relation type. A namer is the operator of the observation, not
                 * its subject: it stays orderable but sorts below every
                 * non-namer, including one with no signal. */
                {
                    double share = best->total_mass > 0.0
                                   ? best->coherence / best->total_mass
                                   : 0.0;
                    bool   is_namer = hash_search(
                        namer_h, &best->ord, HASH_FIND, NULL) != NULL;

                    values[8] = Float8GetDatum(
                        is_namer ? -1.0 : share);
                }
                /* The denominator, exposed raw. */
                values[9] = Float8GetDatum(best->total_mass);
                /* Witness count, a key of the election. */
                values[10] = Int64GetDatum(best->witnesses);
                /* +1 agrees / 0 unattested / -1 disagrees. 0 on every token means
                 * no language evidence, not agreement. */
                values[11] = Int32GetDatum(best->lang_agree);

                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            }
        }
    }

    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
