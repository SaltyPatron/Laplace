#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "nodes/makefuncs.h"
#include "parser/parse_func.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"

#include "content_trajectory_read.h"
#include "lexical_case_internal.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/qualifier_law.h"
#include "laplace/core/sql_catalog.h"

#include <string.h>

static SPIPlanPtr case_mappings_plan = NULL;
static SPIPlanPtr scalar_constituents_plan = NULL;
static SPIPlanPtr scalar_render_plan = NULL;
static SPIPlanPtr scalar_word_id_plan = NULL;
static SPIPlanPtr scalar_entity_exists_plan = NULL;
static Oid        lexical_st_asbinary_oid = InvalidOid;

SPIPlanPtr
ensure_lexical_plan(SPIPlanPtr *slot, const char *sql, int nargs, Oid *argtypes, const char *label)
{
	if (*slot == NULL)
	{
		SPIPlanPtr p = SPI_prepare_cursor(sql, nargs, argtypes, CURSOR_OPT_PARALLEL_OK);

		if (p == NULL)
			elog(ERROR, "%s: SPI_prepare failed: %s", label, SPI_result_code_string(SPI_result));
		if (SPI_keepplan(p) != 0)
			elog(ERROR, "%s: SPI_keepplan failed", label);
		*slot = p;
	}
	return *slot;
}

Oid
ensure_lexical_asbinary_oid(void)
{
	if (!OidIsValid(lexical_st_asbinary_oid))
	{
		Oid phys_rel = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
		Oid geom_type = get_atttype(phys_rel, get_attnum(phys_rel, "trajectory"));

		lexical_st_asbinary_oid = LookupFuncName(
			list_make2(makeString("public"), makeString("st_asbinary")),
			1, &geom_type, false);
	}
	return lexical_st_asbinary_oid;
}

/*
 * Case peers of a word, read from its trajectory. Each constituent grapheme's
 * unrefuted HAS_CASE_MAPPING cells, selected by the mapping/lower, upper and
 * title qualifiers of their attestations, give the highest effective-mu target
 * per slot. The lower, upper and title surfaces are assembled from the rendered
 * targets (the grapheme's own rendering where no target renders) with run-length
 * repetition, minted back to content ids, and kept only when that entity already
 * exists. The word is always its own peer. Each stage is one set statement per
 * call, not one per grapheme.
 */

Datum
make_bytea16(const char *raw)
{
	bytea *b = (bytea *) palloc(VARHDRSZ + 16);

	SET_VARSIZE(b, VARHDRSZ + 16);
	memcpy(VARDATA(b), raw, 16);
	return PointerGetDatum(b);
}

/* Copy a 16-byte entity id out of a bytea Datum; false if not 16 bytes. */
bool
datum_key16(Datum d, char *out)
{
	bytea *b = DatumGetByteaPP(d);

	if (VARSIZE_ANY_EXHDR(b) != 16)
		return false;
	memcpy(out, VARDATA_ANY(b), 16);
	return true;
}

char *
render_lookup(HTAB *r, const char *key16)
{
	bool         found;
	RenderEntry *e = (RenderEntry *) hash_search(r, key16, HASH_FIND, &found);

	return found ? e->text : NULL;
}
/*
 * A grapheme's case mappings are HAS_CASE_MAPPING cells; which case a target is
 * (mapping/lower, upper, title) is the qualifier of the attestation behind the
 * cell. Fills cm with the effective-mu winner per (grapheme, slot); a slot with
 * no witnessed target stays 0.
 */
void
fetch_case_mappings(HTAB *cm, Datum *subjects, int n_subjects, const char *caller)
{
	static const char *cm_values[CM_SLOTS] = { "lower", "upper", "title" };
	Datum      bits[CM_SLOTS];
	ArrayType *subject_array;
	ArrayType *bit_array;
	Oid        at[2] = { BYTEAARRAYOID, INT4ARRAYOID };
	Datum      av[2];
	int        rc;

	for (int s = 0; s < CM_SLOTS; s++)
	{
		int bit = laplace_qualifier_bit("mapping", cm_values[s]);

		if (bit < 0)
			elog(ERROR, "%s: undeclared qualifier mapping/%s", caller, cm_values[s]);
		bits[s] = Int32GetDatum(bit);
	}
	subject_array = construct_array(subjects, n_subjects, BYTEAOID, -1, false, TYPALIGN_INT);
	bit_array = construct_array(bits, CM_SLOTS, INT4OID, 4, true, TYPALIGN_INT);
	av[0] = PointerGetDatum(subject_array);
	av[1] = PointerGetDatum(bit_array);

	rc = SPI_execute_plan(ensure_lexical_plan(&case_mappings_plan,
											  laplace_sql_query_text("lexical.case_mappings"),
											  2, at, caller),
						  av, NULL, true, 0);
	if (rc != SPI_OK_SELECT)
		elog(ERROR, "%s: case-map fetch failed: %s", caller, SPI_result_code_string(rc));

	for (uint64 rr = 0; rr < SPI_processed; rr++)
	{
		HeapTuple     tup = SPI_tuptable->vals[rr];
		TupleDesc     td = SPI_tuptable->tupdesc;
		bool          isnull;
		char          skey[16];
		Datum         subject = SPI_getbinval(tup, td, 1, &isnull);
		Datum         slot_datum;
		int           slot;
		bool          found;
		CaseMapEntry *entry;
		Datum         obj;

		if (isnull || !datum_key16(subject, skey))
			continue;
		slot_datum = SPI_getbinval(tup, td, 2, &isnull);
		if (isnull)
			continue;
		slot = DatumGetInt32(slot_datum) - 1;
		if (slot < 0 || slot >= CM_SLOTS)
			continue;
		entry = (CaseMapEntry *) hash_search(cm, skey, HASH_ENTER, &found);
		obj = SPI_getbinval(tup, td, 3, &isnull);
		if (!found)
			memset(entry->obj, 0, sizeof(entry->obj));
		entry->obj[slot] = isnull ? (Datum) 0 : copy_bytea_datum(obj);
	}
}


/*
 * The per-grapheme base string for a map slot: the rendering of its mapped
 * target when that renders, else the grapheme's own rendering. NULL only when
 * the grapheme itself does not render; the surface then skips that grapheme.
 */
char *
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

/* Lower and upper map every grapheme through their own slot; title maps the
 * first grapheme's first repetition through title and everything else through
 * lower. Each slot yields a distinct surface, NULL when nothing rendered. */
void
assemble_case_surfaces(GraphemeItem *graphemes, int n_graphemes,
					   HTAB *cm, HTAB *rmap, char *surf[CM_SLOTS])
{
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
					continue;
				for (int k = 0; k < run; k++)
					appendStringInfoString(&s, base);
				any = true;
			}
		}
		surf[m] = any ? s.data : NULL;
	}
}

void
batch_word_add_grapheme(BatchWord *word, const char *key, int32 run)
{
	if (word->n_graphemes == word->grapheme_cap)
	{
		word->grapheme_cap = word->grapheme_cap == 0 ? 8 : word->grapheme_cap * 2;
		word->graphemes = word->graphemes == NULL
			? (GraphemeItem *) palloc(sizeof(GraphemeItem) * word->grapheme_cap)
			: (GraphemeItem *) repalloc(word->graphemes,
									 sizeof(GraphemeItem) * word->grapheme_cap);
	}
	memcpy(word->graphemes[word->n_graphemes].key, key, 16);
	word->graphemes[word->n_graphemes].run = run < 1 ? 1 : run;
	word->n_graphemes++;
}

void
batch_word_add_variant(BatchWord *word, const char *key)
{
	for (int i = 0; i < word->n_variants; i++)
		if (memcmp(word->variants + (Size) i * 16, key, 16) == 0)
			return;

	if (word->n_variants == word->variant_cap)
	{
		word->variant_cap = word->variant_cap == 0 ? 4 : word->variant_cap * 2;
		word->variants = word->variants == NULL
			? (char *) palloc((Size) word->variant_cap * 16)
			: (char *) repalloc(word->variants, (Size) word->variant_cap * 16);
	}
	memcpy(word->variants + (Size) word->n_variants * 16, key, 16);
	word->n_variants++;
}

int
id16_cmp(const void *a, const void *b)
{
	return memcmp(a, b, 16);
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

		rc = SPI_execute_plan(
			ensure_lexical_plan(
				&scalar_constituents_plan,
				"SELECT c.ordinal, public.blake3_send(c.child), c.run_length "
				"FROM public.laplace_constituents(ARRAY[public.blake3_from_bytea($1)]) c ORDER BY c.ordinal",
				1, at, "word_case_variants"),
			av, NULL, true, 0);
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

	/* --- 2. ONE batched case-map fetch, eff_mu winner per (grapheme, map). --- */
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(CaseMapEntry);
	cm = hash_create("word_case_variants casemap", 64, &ctl, HASH_ELEM | HASH_BLOBS);

	fetch_case_mappings(cm, subj, n_subj, "word_case_variants");

	/* --- 3. ONE batched render of every id any surface can need. --- */
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(RenderEntry);
	rmap = hash_create("word_case_variants render", 128, &ctl, HASH_ELEM | HASH_BLOBS);

	render_cap = n_graphemes * (CM_SLOTS + 1) + 1;
	render_ids = (Datum *) palloc(sizeof(Datum) * render_cap);

	/* p_word's own rendering, against which each surface is compared. */
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

		rc = SPI_execute_plan(
			ensure_lexical_plan(
				&scalar_render_plan,
				"SELECT u.id, realize.render_text(u.id) "
				"FROM unnest($1::bytea[]) AS u(id)",
				1, at, "word_case_variants"),
			av, NULL, true, 0);
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

	/* --- 4. Assemble the three case surfaces. --- */
	render_pword = render_lookup(rmap, pword_key);
	assemble_case_surfaces(graphemes, n_graphemes, cm, rmap, surf);

	/* --- 5. Drop surfaces equal to the word's own, mint ids, gate on existence. --- */
	surv = (Datum *) palloc(sizeof(Datum) * CM_SLOTS);
	for (int m = 0; m < CM_SLOTS; m++)
	{
		bool distinct;

		if (surf[m] == NULL)
			continue;
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

		rc = SPI_execute_plan(
			ensure_lexical_plan(
				&scalar_word_id_plan,
				"SELECT laplace.word_id(u.s) FROM unnest($1::text[]) AS u(s)",
				1, at, "word_case_variants"),
			av, NULL, true, 0);
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
				continue;
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

		rc = SPI_execute_plan(
			ensure_lexical_plan(
				&scalar_entity_exists_plan,
				"SELECT u.id, consensus.entity_exists(u.id) "
				"FROM unnest($1::bytea[]) AS u(id)",
				1, at, "word_case_variants"),
			av, NULL, true, 0);
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

	/* --- 6. Distinct ids, bytewise ascending. --- */
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
