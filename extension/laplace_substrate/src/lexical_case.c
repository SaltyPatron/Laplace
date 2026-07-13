#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"

#include <string.h>

/*
 * word_case_variants(word) -> bytea[] : the orthographic case-variant word ids
 * of a word (itself + witnessed lower/upper/title surfaces minted back to word
 * ids). This is the native, batched replacement for the SQL trio
 * word_case_variant_ids -> word_case_map_surface (x3) -> grapheme_case_target,
 * which fanned a single lexical lookup into O(3 * graphemes) non-inlinable
 * consensus LIMIT-1 subqueries plus O(graphemes) render_text calls -- the RBAR
 * on the hot lexical_peers -> senses/define/bubble_up path.
 *
 * OUTPUT-IDENTICAL BY DELEGATION. Every semantic decision is still made by the
 * exact same function the SQL used:
 *   - case-map selection: one batched DISTINCT ON (subject, type) ... ORDER BY
 *     subject, type, eff_mu(rating, rd) DESC over v_consensus_unrefuted -- the
 *     same view, same eff_mu ordering, same "highest eff_mu wins, NULL/absent
 *     object => no mapping" semantics as grapheme_case_target's LIMIT 1;
 *   - grapheme rendering: laplace.render_text (depth 32), identical to the
 *     scalar render_text the SQL surface builder called;
 *   - word minting: laplace.word_id, the same content hash;
 *   - existence gate: laplace.entity_exists, identical predicate.
 * The C code only (a) walks the grapheme trajectory once and (b) reproduces
 * word_case_map_surface's string assembly verbatim (title => title map on the
 * first grapheme, lower on the rest; per-grapheme COALESCE(render(target),
 * render(child)) with run_length repetition; string_agg's skip-NULL semantics),
 * collapsing the fan-out to a fixed handful of batched SPI round trips.
 */

#define CM_LOWER 0
#define CM_UPPER 1
#define CM_TITLE 2
#define CM_SLOTS 3

typedef struct GraphemeItem
{
	char  key[16];				/* grapheme child id */
	int32 run;					/* run_length (>= 1 after clamping) */
} GraphemeItem;

/* Winning case-map object id per grapheme, per map slot (0 == none). */
typedef struct CaseMapEntry
{
	char  key[16];
	Datum obj[CM_SLOTS];
} CaseMapEntry;

/* render_text result for an id; text == NULL means it rendered to SQL NULL. */
typedef struct RenderEntry
{
	char  key[16];
	char *text;
} RenderEntry;

static Datum
make_bytea16(const char *raw)
{
	bytea *b = (bytea *) palloc(VARHDRSZ + 16);

	SET_VARSIZE(b, VARHDRSZ + 16);
	memcpy(VARDATA(b), raw, 16);
	return PointerGetDatum(b);
}

/* Copy a 16-byte entity id out of a bytea Datum; false if not 16 bytes. */
static bool
datum_key16(Datum d, char *out)
{
	bytea *b = DatumGetByteaPP(d);

	if (VARSIZE_ANY_EXHDR(b) != 16)
		return false;
	memcpy(out, VARDATA_ANY(b), 16);
	return true;
}

static char *
render_lookup(HTAB *r, const char *key16)
{
	bool         found;
	RenderEntry *e = (RenderEntry *) hash_search(r, key16, HASH_FIND, &found);

	return found ? e->text : NULL;
}

/*
 * The per-grapheme base string for a map slot, matching
 * COALESCE(render_text(grapheme_case_target(child, slot)), render_text(child)):
 * render(target) if a non-NULL-rendering target exists, else render(child).
 * Returns NULL only when render(child) itself is NULL (string_agg then skips
 * the whole grapheme).
 */
static char *
piece_base(HTAB *cm, HTAB *r, const char *gkey, int slot)
{
	bool          found;
	CaseMapEntry *ce = (CaseMapEntry *) hash_search(cm, gkey, HASH_FIND, &found);

	if (found && ce->obj[slot] != (Datum) 0)
	{
		char okey[16];

		if (datum_key16(ce->obj[slot], okey))
		{
			char *t = render_lookup(r, okey);

			if (t != NULL)
				return t;
		}
	}
	return render_lookup(r, gkey);
}

PG_FUNCTION_INFO_V1(pg_laplace_word_case_variants);

Datum
pg_laplace_word_case_variants(PG_FUNCTION_ARGS)
{
	Datum         p_word = PG_GETARG_DATUM(0);
	char          pword_key[16];
	MemoryContext caller = CurrentMemoryContext;
	bool          need_finish = false;
	int           rc;

	GraphemeItem *graphemes = NULL;
	int           n_graphemes = 0;

	Datum        *subj = NULL;			/* distinct grapheme ids */
	int           n_subj = 0;

	Datum         type_datum[CM_SLOTS];
	char          type_key[CM_SLOTS][16];

	HASHCTL       ctl;
	HTAB         *cm;					/* grapheme -> CaseMapEntry */
	HTAB         *rmap;					/* id -> RenderEntry */

	Datum        *render_ids = NULL;
	int           n_render = 0;
	int           render_cap = 0;

	char         *surf[CM_SLOTS];
	char         *render_pword;

	Datum        *surv = NULL;			/* surviving surface texts */
	int           n_surv = 0;

	Datum        *vids = NULL;			/* distinct candidate word ids */
	char          vkey[64][16];			/* at most 3 surfaces -> few ids */
	int           n_vids = 0;

	char         *keep = NULL;			/* kept 16-byte ids (with p_word) */
	int           n_keep = 0;

	ArrayType    *result;

	if (!datum_key16(p_word, pword_key))
		ereport(ERROR, (errmsg("word_case_variants: word id must be 16 bytes")));

	if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
		elog(ERROR, "word_case_variants: SPI_connect failed");

	/* --- 1. Walk the grapheme trajectory once. --- */
	{
		Oid   at[1] = { BYTEAOID };
		Datum av[1] = { p_word };

		rc = SPI_execute_with_args(
			"SELECT ordinal, child_id, run_length "
			"FROM laplace.constituents($1) ORDER BY ordinal",
			1, at, av, NULL, true, 0);
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "word_case_variants: constituents failed: %s",
				 SPI_result_code_string(rc));

		n_graphemes = (int) SPI_processed;
		if (n_graphemes > 0)
		{
			graphemes = (GraphemeItem *) palloc0(sizeof(GraphemeItem) * n_graphemes);
			subj = (Datum *) palloc(sizeof(Datum) * n_graphemes);
			for (int i = 0; i < n_graphemes; i++)
			{
				HeapTuple tup = SPI_tuptable->vals[i];
				TupleDesc td = SPI_tuptable->tupdesc;
				bool      isnull;
				Datum     child = SPI_getbinval(tup, td, 2, &isnull);
				int32     run;

				if (isnull || !datum_key16(child, graphemes[i].key))
					ereport(ERROR, (errmsg("word_case_variants: bad grapheme id")));

				run = DatumGetInt32(SPI_getbinval(tup, td, 3, &isnull));
				graphemes[i].run = (isnull || run < 1) ? 1 : run;

				/* distinct grapheme ids for the batched case-map fetch */
				{
					bool dup = false;

					for (int j = 0; j < n_subj; j++)
						if (memcmp(graphemes[i].key,
								   VARDATA_ANY(DatumGetByteaPP(subj[j])), 16) == 0)
						{
							dup = true;
							break;
						}
					if (!dup)
						subj[n_subj++] = copy_bytea_datum(child);
				}
			}
		}
	}

	/* No constituents => no case surfaces => just the word itself. */
	if (n_graphemes == 0)
	{
		laplace_spi_finish(need_finish);
		{
			Datum one = make_bytea16(pword_key);

			result = construct_array(&one, 1, BYTEAOID, -1, false, TYPALIGN_INT);
		}
		PG_RETURN_ARRAYTYPE_P(result);
	}

	/* --- 2. Resolve the three case-map relation type ids. --- */
	{
		rc = SPI_execute(
			"SELECT laplace.relation_type_id('HAS_LOWERCASE_MAPPING'), "
			"       laplace.relation_type_id('HAS_UPPERCASE_MAPPING'), "
			"       laplace.relation_type_id('HAS_TITLECASE_MAPPING')",
			true, 1);
		if (rc != SPI_OK_SELECT || SPI_processed != 1)
			elog(ERROR, "word_case_variants: relation_type_id resolution failed");
		for (int s = 0; s < CM_SLOTS; s++)
		{
			bool  isnull;
			Datum d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc,
									s + 1, &isnull);

			if (isnull || !datum_key16(d, type_key[s]))
				elog(ERROR, "word_case_variants: null case-map relation type id");
			type_datum[s] = copy_bytea_datum(d);
		}
	}

	/* --- 3. ONE batched case-map fetch, eff_mu winner per (grapheme, map). --- */
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(CaseMapEntry);
	cm = hash_create("word_case_variants casemap", 64, &ctl, HASH_ELEM | HASH_BLOBS);

	{
		ArrayType *subj_arr = construct_array(subj, n_subj, BYTEAOID, -1, false,
											  TYPALIGN_INT);
		ArrayType *type_arr = construct_array(type_datum, CM_SLOTS, BYTEAOID, -1,
											  false, TYPALIGN_INT);
		Oid        at[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
		Datum      av[2] = { PointerGetDatum(subj_arr), PointerGetDatum(type_arr) };

		rc = SPI_execute_with_args(
			"SELECT DISTINCT ON (c.subject_id, c.type_id) "
			"       c.subject_id, c.type_id, c.object_id "
			"FROM laplace.v_consensus_unrefuted c "
			"WHERE c.subject_id = ANY($1) AND c.type_id = ANY($2) "
			"ORDER BY c.subject_id, c.type_id, laplace.eff_mu(c.rating, c.rd) DESC",
			2, at, av, NULL, true, 0);
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "word_case_variants: case-map fetch failed: %s",
				 SPI_result_code_string(rc));

		for (uint64 rr = 0; rr < SPI_processed; rr++)
		{
			HeapTuple tup = SPI_tuptable->vals[rr];
			TupleDesc td = SPI_tuptable->tupdesc;
			bool      isnull;
			Datum     subject = SPI_getbinval(tup, td, 1, &isnull);
			char      skey[16];
			char      tkey[16];
			Datum     tdat;
			int       slot = -1;

			if (isnull || !datum_key16(subject, skey))
				continue;
			tdat = SPI_getbinval(tup, td, 2, &isnull);
			if (isnull || !datum_key16(tdat, tkey))
				continue;
			for (int s = 0; s < CM_SLOTS; s++)
				if (memcmp(tkey, type_key[s], 16) == 0)
				{
					slot = s;
					break;
				}
			if (slot < 0)
				continue;

			{
				bool          found;
				CaseMapEntry *ce = (CaseMapEntry *) hash_search(cm, skey, HASH_ENTER,
															   &found);
				Datum         obj = SPI_getbinval(tup, td, 3, &isnull);

				if (!found)
					memset(ce->obj, 0, sizeof(ce->obj));
				/* NULL object => no mapping (grapheme_case_target => NULL). */
				ce->obj[slot] = isnull ? (Datum) 0 : copy_bytea_datum(obj);
			}
		}
	}

	/* --- 4. ONE batched render of every id any surface can need. --- */
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(RenderEntry);
	rmap = hash_create("word_case_variants render", 128, &ctl, HASH_ELEM | HASH_BLOBS);

	render_cap = n_graphemes * (CM_SLOTS + 1) + 1;
	render_ids = (Datum *) palloc(sizeof(Datum) * render_cap);

	/* p_word (for the IS DISTINCT FROM render_text(p_word) filter). */
	{
		bool         found;
		RenderEntry *e = (RenderEntry *) hash_search(rmap, pword_key, HASH_ENTER,
													 &found);

		if (!found)
		{
			e->text = NULL;
			render_ids[n_render++] = p_word;
		}
	}
	for (int i = 0; i < n_graphemes; i++)
	{
		bool          found;
		RenderEntry  *e = (RenderEntry *) hash_search(rmap, graphemes[i].key,
													  HASH_ENTER, &found);
		CaseMapEntry *ce;

		if (!found)
		{
			e->text = NULL;
			render_ids[n_render++] = make_bytea16(graphemes[i].key);
		}
		ce = (CaseMapEntry *) hash_search(cm, graphemes[i].key, HASH_FIND, &found);
		if (found)
			for (int s = 0; s < CM_SLOTS; s++)
			{
				char         okey[16];
				RenderEntry *oe;

				if (ce->obj[s] == (Datum) 0 || !datum_key16(ce->obj[s], okey))
					continue;
				oe = (RenderEntry *) hash_search(rmap, okey, HASH_ENTER, &found);
				if (!found)
				{
					oe->text = NULL;
					render_ids[n_render++] = copy_bytea_datum(ce->obj[s]);
				}
			}
	}

	{
		ArrayType *rid_arr = construct_array(render_ids, n_render, BYTEAOID, -1,
											 false, TYPALIGN_INT);
		Oid        at[1] = { BYTEAARRAYOID };
		Datum      av[1] = { PointerGetDatum(rid_arr) };

		rc = SPI_execute_with_args(
			"SELECT u.id, laplace.render_text(u.id) "
			"FROM unnest($1::bytea[]) AS u(id)",
			1, at, av, NULL, true, 0);
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "word_case_variants: render batch failed: %s",
				 SPI_result_code_string(rc));

		for (uint64 rr = 0; rr < SPI_processed; rr++)
		{
			HeapTuple tup = SPI_tuptable->vals[rr];
			TupleDesc td = SPI_tuptable->tupdesc;
			bool      isnull;
			Datum     idd = SPI_getbinval(tup, td, 1, &isnull);
			char      ikey[16];
			Datum     txt;
			bool      tnull;
			bool      found;
			RenderEntry *e;

			if (isnull || !datum_key16(idd, ikey))
				continue;
			e = (RenderEntry *) hash_search(rmap, ikey, HASH_FIND, &found);
			if (!found)
				continue;
			txt = SPI_getbinval(tup, td, 2, &tnull);
			e->text = tnull ? NULL : text_to_cstring(DatumGetTextPP(txt));
		}
	}

	/* --- 5. Assemble the three surfaces exactly as word_case_map_surface. --- */
	render_pword = render_lookup(rmap, pword_key);

	for (int m = 0; m < CM_SLOTS; m++)
	{
		StringInfoData s;
		bool           any = false;

		initStringInfo(&s);

		if (m == CM_TITLE)
		{
			for (int i = 0; i < n_graphemes; i++)
			{
				int run = graphemes[i].run;

				if (i == 0)
				{
					/* first grapheme: title once, then lower for the rest of
					 * its run -- COALESCE(render(title),render(child)) ||
					 * repeat(COALESCE(render(lower),render(child)), run-1).
					 * repeat() is STRICT, so the whole '||' expression is NULL
					 * (grapheme skipped) if EITHER operand is NULL, including
					 * repeat(lb, 0) when lb is NULL. Hence both tb and lb must
					 * be non-NULL for any run length. */
					char *tb = piece_base(cm, rmap, graphemes[i].key, CM_TITLE);
					char *lb = piece_base(cm, rmap, graphemes[i].key, CM_LOWER);

					if (tb == NULL || lb == NULL)
						continue;
					appendStringInfoString(&s, tb);
					for (int k = 1; k < run; k++)
						appendStringInfoString(&s, lb);
					any = true;
				}
				else
				{
					char *lb = piece_base(cm, rmap, graphemes[i].key, CM_LOWER);

					if (lb == NULL)
						continue;
					for (int k = 0; k < run; k++)
						appendStringInfoString(&s, lb);
					any = true;
				}
			}
		}
		else
		{
			for (int i = 0; i < n_graphemes; i++)
			{
				char *base = piece_base(cm, rmap, graphemes[i].key, m);
				int   run = graphemes[i].run;

				if (base == NULL)
					continue;	/* string_agg skips NULL elements */
				for (int k = 0; k < run; k++)
					appendStringInfoString(&s, base);
				any = true;
			}
		}

		/* string_agg over all-NULL / zero rows yields NULL. */
		surf[m] = any ? s.data : NULL;
	}

	/* --- 6. Filter surfaces, mint word ids, gate on existence. --- */
	surv = (Datum *) palloc(sizeof(Datum) * CM_SLOTS);
	for (int m = 0; m < CM_SLOTS; m++)
	{
		bool distinct;

		if (surf[m] == NULL)
			continue;
		/* surface IS DISTINCT FROM render_text(p_word) */
		distinct = (render_pword == NULL) ? true
										  : (strcmp(surf[m], render_pword) != 0);
		if (!distinct)
			continue;
		surv[n_surv++] = CStringGetTextDatum(surf[m]);
	}

	if (n_surv > 0)
	{
		ArrayType *surf_arr = construct_array(surv, n_surv, TEXTOID, -1, false,
											  TYPALIGN_INT);
		Oid        at[1] = { TEXTARRAYOID };
		Datum      av[1] = { PointerGetDatum(surf_arr) };

		rc = SPI_execute_with_args(
			"SELECT laplace.word_id(u.s) FROM unnest($1::text[]) AS u(s)",
			1, at, av, NULL, true, 0);
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "word_case_variants: word_id batch failed: %s",
				 SPI_result_code_string(rc));

		vids = (Datum *) palloc(sizeof(Datum) * SPI_processed);
		for (uint64 rr = 0; rr < SPI_processed; rr++)
		{
			bool  isnull;
			Datum wid = SPI_getbinval(SPI_tuptable->vals[rr],
									  SPI_tuptable->tupdesc, 1, &isnull);
			char  wkey[16];
			bool  dup = false;

			if (isnull || !datum_key16(wid, wkey))
				continue;			/* q.id IS NOT NULL */
			for (int j = 0; j < n_vids; j++)
				if (memcmp(wkey, vkey[j], 16) == 0)
				{
					dup = true;
					break;
				}
			if (dup)
				continue;
			memcpy(vkey[n_vids], wkey, 16);
			vids[n_vids++] = copy_bytea_datum(wid);
		}
	}

	/* Existence gate for the candidate ids that are not p_word itself. */
	keep = (char *) palloc(16 * (n_vids + 1));
	memcpy(keep + 16 * n_keep, pword_key, 16);	/* p_word always kept */
	n_keep++;

	if (n_vids > 0)
	{
		ArrayType *vid_arr = construct_array(vids, n_vids, BYTEAOID, -1, false,
											 TYPALIGN_INT);
		Oid        at[1] = { BYTEAARRAYOID };
		Datum      av[1] = { PointerGetDatum(vid_arr) };

		rc = SPI_execute_with_args(
			"SELECT u.id, laplace.entity_exists(u.id) "
			"FROM unnest($1::bytea[]) AS u(id)",
			1, at, av, NULL, true, 0);
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "word_case_variants: entity_exists batch failed: %s",
				 SPI_result_code_string(rc));

		for (uint64 rr = 0; rr < SPI_processed; rr++)
		{
			HeapTuple tup = SPI_tuptable->vals[rr];
			TupleDesc td = SPI_tuptable->tupdesc;
			bool      isnull;
			Datum     idd = SPI_getbinval(tup, td, 1, &isnull);
			char      ikey[16];
			bool      exists;

			if (isnull || !datum_key16(idd, ikey))
				continue;
			exists = DatumGetBool(SPI_getbinval(tup, td, 2, &isnull));
			/* (q.id = p_word OR entity_exists(q.id)) */
			if (memcmp(ikey, pword_key, 16) == 0 || (!isnull && exists))
			{
				bool dup = false;

				for (int j = 0; j < n_keep; j++)
					if (memcmp(keep + 16 * j, ikey, 16) == 0)
					{
						dup = true;
						break;
					}
				if (!dup)
				{
					memcpy(keep + 16 * n_keep, ikey, 16);
					n_keep++;
				}
			}
		}
	}

	/* --- 7. DISTINCT, sorted ascending (array_agg(DISTINCT id ORDER BY id)). --- */
	for (int a = 0; a < n_keep; a++)
		for (int b = a + 1; b < n_keep; b++)
			if (memcmp(keep + 16 * a, keep + 16 * b, 16) > 0)
			{
				char tmp[16];

				memcpy(tmp, keep + 16 * a, 16);
				memcpy(keep + 16 * a, keep + 16 * b, 16);
				memcpy(keep + 16 * b, tmp, 16);
			}

	/*
	 * Build the result in the caller's context while SPI is still connected
	 * (keep[] lives in the SPI context that SPI_finish will free).
	 */
	{
		MemoryContext old = MemoryContextSwitchTo(caller);
		Datum        *out = (Datum *) palloc(sizeof(Datum) * n_keep);

		for (int i = 0; i < n_keep; i++)
			out[i] = make_bytea16(keep + 16 * i);
		result = construct_array(out, n_keep, BYTEAOID, -1, false, TYPALIGN_INT);
		MemoryContextSwitchTo(old);
	}

	laplace_spi_finish(need_finish);

	PG_RETURN_ARRAYTYPE_P(result);
}
