#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "content_trajectory_read.h"
#include "lexical_case_internal.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"
#include "laplace/core/mantissa.h"

#include <string.h>

static SPIPlanPtr batch_traj_fallback_plan = NULL;
static SPIPlanPtr batch_render_plan = NULL;
static SPIPlanPtr batch_word_id_plan = NULL;
static SPIPlanPtr batch_exists_bitmap_plan = NULL;

typedef struct WordCaseTrajCtx
{
	Oid           as_binary;
	HTAB         *word_lookup;
	BatchWord    *words;
	bool         *resolved_word;
	int          *total_graphemes;
	MemoryContext owner;
} WordCaseTrajCtx;

static void
word_case_consume_trajectory(Datum physicality, Datum entity, Datum geometry, void *opaque)
{
	WordCaseTrajCtx *ctx = (WordCaseTrajCtx *) opaque;
	char             wkey[16];
	BatchWordLookup *entry;

	(void) physicality;
	if (!datum_key16(entity, wkey))
		return;
	entry = (BatchWordLookup *) hash_search(ctx->word_lookup, wkey, HASH_FIND, NULL);
	if (entry == NULL || ctx->resolved_word[entry->index])
		return;
	ctx->resolved_word[entry->index] = true;
	{
		MemoryContext        prev = MemoryContextSwitchTo(ctx->owner);
		bytea               *wkb = DatumGetByteaP(OidFunctionCall1(ctx->as_binary, geometry));
		uint32               npoints = 0;
		const unsigned char *points = laplace_trajectory_wkb_points(wkb, &npoints);

		for (uint32 i = 0; i < npoints; ++i)
		{
			double             vertex[4];
			mantissa_payload_t payload;
			int32              run;

			memcpy(vertex, points + (Size) i * 32, sizeof(vertex));
			mantissa_unpack(vertex, &payload);
			run = payload.run_length ? (int32) payload.run_length : 1;
			batch_word_add_grapheme(&ctx->words[entry->index],
									(const char *) &payload.entity_id, run);
			(*ctx->total_graphemes)++;
		}
		pfree(wkb);
		MemoryContextSwitchTo(prev);
	}
}

/*
 * Set form over a whole frontier of words: one trajectory unpack, one mapping
 * read over every distinct grapheme, one render, one mint and one existence
 * bitmap for the complete array. With classes_only it emits each word's lower
 * surface instead of minted variants.
 */
static Datum
word_case_batch(FunctionCallInfo fcinfo, bool classes_only)
{
	ReturnSetInfo   *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
	ArrayType       *input;
	Datum           *input_datums;
	bool            *input_nulls;
	int              n_input;
	BatchWord       *words;
	int              n_words = 0;
	HASHCTL          ctl;
	HTAB            *word_lookup;
	HTAB            *cm;
	HTAB            *rmap;
	bool             need_finish = false;
	int              rc;
	int              total_graphemes = 0;
	Datum           *grapheme_ids;
	int              n_grapheme_ids = 0;
	Datum           *render_ids;
	int              n_render = 0;
	MemoryContext    owner = CurrentMemoryContext;

	if (PG_ARGISNULL(0))
		ereport(ERROR, (errmsg("word_case_variants_batch: words must not be NULL")));
	input = PG_GETARG_ARRAYTYPE_P(0);
	deconstruct_array(input, BYTEAOID, -1, false, TYPALIGN_INT,
					  &input_datums, &input_nulls, &n_input);
	InitMaterializedSRF(fcinfo, 0);
	if (n_input < 1)
		return (Datum) 0;

	words = (BatchWord *) palloc0(sizeof(BatchWord) * n_input);
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(BatchWordLookup);
	word_lookup = hash_create("word case batch words", n_input, &ctl,
						  HASH_ELEM | HASH_BLOBS);
	for (int i = 0; i < n_input; i++)
	{
		bytea           *word;
		BatchWordLookup *entry;
		bool             found;

		if (input_nulls[i])
			ereport(ERROR, (errmsg("word_case_variants_batch: words contain NULL")));
		word = DatumGetByteaPP(input_datums[i]);
		if (VARSIZE_ANY_EXHDR(word) != 16)
			ereport(ERROR, (errmsg("word_case_variants_batch: words must be 16-byte ids")));
		entry = (BatchWordLookup *) hash_search(word_lookup, VARDATA_ANY(word),
											 HASH_ENTER, &found);
		if (!found)
		{
			entry->index = n_words;
			memcpy(words[n_words].key, VARDATA_ANY(word), 16);
			batch_word_add_variant(&words[n_words], VARDATA_ANY(word));
			n_words++;
		}
	}

	/* One trajectory unpack for every input word: first probe the canonical
	 * (entity_id, type=1) primary-key address on the owning HASH(id) leaf via
	 * laplace_content_trajectory_read, and fall back to the entity_id-indexed
	 * query only for any word not found at its canonical physicality id. */
	{
		Datum          *word_datums = (Datum *) palloc(sizeof(Datum) * n_words);
		bool           *resolved_word = (bool *) palloc0(sizeof(bool) * n_words);
		ArrayType      *word_array;
		WordCaseTrajCtx traj_ctx;
		int             rem_n = 0;

		for (int i = 0; i < n_words; i++)
			word_datums[i] = make_bytea16(words[i].key);
		word_array = construct_array(word_datums, n_words, BYTEAOID,
								 -1, false, TYPALIGN_INT);
		traj_ctx.as_binary = ensure_lexical_asbinary_oid();
		traj_ctx.word_lookup = word_lookup;
		traj_ctx.words = words;
		traj_ctx.resolved_word = resolved_word;
		traj_ctx.total_graphemes = &total_graphemes;
		traj_ctx.owner = owner;
		laplace_content_trajectory_read(word_array, word_case_consume_trajectory, &traj_ctx);

		if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
			elog(ERROR, "word_case_variants_batch: SPI_connect failed");

		for (int i = 0; i < n_words; i++)
			if (!resolved_word[i])
				word_datums[rem_n++] = make_bytea16(words[i].key);

		if (rem_n > 0)
		{
			ArrayType *rem_array = construct_array(word_datums, rem_n, BYTEAOID,
											   -1, false, TYPALIGN_INT);
			Oid        at[1] = { BYTEAARRAYOID };
			Datum      av[1] = { PointerGetDatum(rem_array) };

			rc = SPI_execute_plan(
				ensure_lexical_plan(
					&batch_traj_fallback_plan,
					"SELECT entity_id, constituent_id, run_length FROM ("
					"SELECT q.entity_id, c.ordinal, c.entity_id AS constituent_id, c.run_length "
					"FROM (SELECT DISTINCT ON (p.entity_id) p.entity_id, p.trajectory "
					"      FROM laplace.physicalities p "
					"      WHERE p.type = 1 AND p.trajectory IS NOT NULL "
					"        AND p.entity_id = ANY($1) "
					"      ORDER BY p.entity_id, p.id) q "
					"CROSS JOIN LATERAL public.laplace_trajectory_constituents(q.trajectory) c "
					"UNION ALL "
					"SELECT public.blake3_send(c.entity), c.ordinal, "
					"       public.blake3_send(c.child), c.run_length "
					"FROM public.laplace_constituents("
					"    ARRAY(SELECT public.blake3_from_bytea(u.id) "
					"          FROM unnest($1::bytea[]) u(id) "
					"          WHERE NOT EXISTS (SELECT 1 FROM laplace.physicalities p "
					"                            WHERE p.entity_id = u.id AND p.type = 1 "
					"                              AND p.trajectory IS NOT NULL))) c"
					") s ORDER BY entity_id, ordinal",
					1, at, "word_case_variants_batch"),
				av, NULL, true, 0);
			if (rc != SPI_OK_SELECT)
				elog(ERROR, "word_case_variants_batch: constituents failed: %s",
					 SPI_result_code_string(rc));

			for (uint64 rr = 0; rr < SPI_processed; rr++)
			{
				HeapTuple        tup = SPI_tuptable->vals[rr];
				TupleDesc        td = SPI_tuptable->tupdesc;
				bool             isnull;
				Datum            wd = SPI_getbinval(tup, td, 1, &isnull);
				char             wkey[16], gkey[16];
				BatchWordLookup *entry;
				Datum            gd;
				int32            run;

				if (isnull || !datum_key16(wd, wkey))
					continue;
				entry = (BatchWordLookup *) hash_search(word_lookup, wkey,
													HASH_FIND, NULL);
				if (entry == NULL)
					continue;
				gd = SPI_getbinval(tup, td, 2, &isnull);
				if (isnull || !datum_key16(gd, gkey))
					continue;
				run = DatumGetInt32(SPI_getbinval(tup, td, 3, &isnull));
				batch_word_add_grapheme(&words[entry->index], gkey,
									 isnull ? 1 : run);
				total_graphemes++;
			}
		}
	}

	/* Every distinct grapheme mapping in one statement. */
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(CaseMapEntry);
	cm = hash_create("word case batch mappings", total_graphemes + 1, &ctl,
					 HASH_ELEM | HASH_BLOBS);
	grapheme_ids = (Datum *) palloc(sizeof(Datum) * (total_graphemes + 1));
	{
		HTAB *seen;

		memset(&ctl, 0, sizeof(ctl));
		ctl.keysize = 16;
		ctl.entrysize = 16;
		seen = hash_create("word case batch graphemes", total_graphemes + 1,
						   &ctl, HASH_ELEM | HASH_BLOBS);
		for (int w = 0; w < n_words; w++)
			for (int g = 0; g < words[w].n_graphemes; g++)
			{
				bool found;

				(void) hash_search(seen, words[w].graphemes[g].key,
								   HASH_ENTER, &found);
				if (!found)
					grapheme_ids[n_grapheme_ids++] =
						make_bytea16(words[w].graphemes[g].key);
			}
	}

	if (n_grapheme_ids > 0)
		fetch_case_mappings(cm, grapheme_ids, n_grapheme_ids, "word_case_variants_batch");

	/* Render every word, grapheme and mapped target once, position aligned. */
	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(RenderEntry);
	rmap = hash_create("word case batch render", n_words + total_graphemes * 4 + 1,
					   &ctl, HASH_ELEM | HASH_BLOBS);
	render_ids = (Datum *) palloc(sizeof(Datum) *
								(n_words + total_graphemes * 4 + 1));

	for (int w = 0; w < n_words; w++)
	{
		bool         found;
		RenderEntry *entry = (RenderEntry *) hash_search(rmap, words[w].key,
													 HASH_ENTER, &found);

		if (!found)
		{
			entry->text = NULL;
			render_ids[n_render++] = make_bytea16(words[w].key);
		}
	}
	for (int g = 0; g < n_grapheme_ids; g++)
	{
		char          gkey[16];
		CaseMapEntry *mapping;
		bool          found;
		RenderEntry  *entry;

		(void) datum_key16(grapheme_ids[g], gkey);
		entry = (RenderEntry *) hash_search(rmap, gkey, HASH_ENTER, &found);
		if (!found)
		{
			entry->text = NULL;
			render_ids[n_render++] = make_bytea16(gkey);
		}
		mapping = (CaseMapEntry *) hash_search(cm, gkey, HASH_FIND, &found);
		if (found)
			for (int s = 0; s < CM_SLOTS; s++)
			{
				char okey[16];

				if (mapping->obj[s] == (Datum) 0 ||
					!datum_key16(mapping->obj[s], okey))
					continue;
				entry = (RenderEntry *) hash_search(rmap, okey, HASH_ENTER, &found);
				if (!found)
				{
					entry->text = NULL;
					render_ids[n_render++] = make_bytea16(okey);
				}
			}
	}

	if (n_render > 0)
	{
		ArrayType *render_array = construct_array(render_ids, n_render, BYTEAOID,
											-1, false, TYPALIGN_INT);
		Oid        at[1] = { BYTEAARRAYOID };
		Datum      av[1] = { PointerGetDatum(render_array) };

		rc = SPI_execute_plan(
			ensure_lexical_plan(&batch_render_plan,
								"SELECT realize.render_text_batch($1)",
								1, at, "word_case_variants_batch"),
			av, NULL, true, 1);
		if (rc != SPI_OK_SELECT || SPI_processed != 1)
			elog(ERROR, "word_case_variants_batch: render batch failed: %s",
				 SPI_result_code_string(rc));
		{
			bool   isnull;
			Datum  rendered = SPI_getbinval(SPI_tuptable->vals[0],
										SPI_tuptable->tupdesc, 1, &isnull);
			Datum *texts;
			bool  *nulls;
			int    n_texts;

			if (isnull)
				elog(ERROR, "word_case_variants_batch: render batch returned NULL");
			deconstruct_array(DatumGetArrayTypeP(rendered), TEXTOID, -1, false,
							  TYPALIGN_INT, &texts, &nulls, &n_texts);
			if (n_texts != n_render)
				elog(ERROR, "word_case_variants_batch: render result length mismatch");
			for (int i = 0; i < n_render; i++)
			{
				char        key[16];
				RenderEntry *entry;

				(void) datum_key16(render_ids[i], key);
				entry = (RenderEntry *) hash_search(rmap, key, HASH_FIND, NULL);
				entry->text = nulls[i] ? NULL :
					text_to_cstring(DatumGetTextPP(texts[i]));
			}
		}
	}

	/* Assemble every surface, mint all ids in one query, then run one existence
	 * bitmap. Candidate ordinality carries the originating word throughout. */
	{
		Datum *surface_texts = (Datum *) palloc(sizeof(Datum) * n_words * CM_SLOTS);
		int   *surface_words = (int *) palloc(sizeof(int) * n_words * CM_SLOTS);
		int    n_surfaces = 0;

		for (int w = 0; w < n_words; w++)
		{
			char *surf[CM_SLOTS] = { NULL, NULL, NULL };
			char *original = render_lookup(rmap, words[w].key);

			assemble_case_surfaces(words[w].graphemes, words[w].n_graphemes,
								cm, rmap, surf);
			if (classes_only)
			{
				/* The class is the assembled lower surface as text: no locale
				 * category, no minted or existence-gated identity. */
				Datum values[2] = {
					make_bytea16(words[w].key),
					surf[CM_LOWER] == NULL ? (Datum) 0 :
						CStringGetTextDatum(surf[CM_LOWER])
				};
				bool nulls[2] = { false, surf[CM_LOWER] == NULL };

				tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
				continue;
			}
			for (int s = 0; s < CM_SLOTS; s++)
				if (surf[s] != NULL &&
					(original == NULL || strcmp(surf[s], original) != 0))
				{
					surface_texts[n_surfaces] = CStringGetTextDatum(surf[s]);
					surface_words[n_surfaces] = w;
					n_surfaces++;
				}
		}

		if (n_surfaces > 0)
		{
			ArrayType *surface_array = construct_array(surface_texts, n_surfaces,
												 TEXTOID, -1, false, TYPALIGN_INT);
			Oid        at[1] = { TEXTARRAYOID };
			Datum      av[1] = { PointerGetDatum(surface_array) };
			Datum     *candidate_ids = (Datum *) palloc(sizeof(Datum) * n_surfaces);
			int       *candidate_words = (int *) palloc(sizeof(int) * n_surfaces);
			int        n_candidates = 0;

			rc = SPI_execute_plan(
				ensure_lexical_plan(
					&batch_word_id_plan,
					"SELECT laplace.word_id(u.s) FROM unnest($1::text[]) "
					"WITH ORDINALITY AS u(s, ord) ORDER BY u.ord",
					1, at, "word_case_variants_batch"),
				av, NULL, true, 0);
			if (rc != SPI_OK_SELECT)
				elog(ERROR, "word_case_variants_batch: word-id batch failed: %s",
					 SPI_result_code_string(rc));
			for (uint64 rr = 0; rr < SPI_processed && rr < (uint64) n_surfaces; rr++)
			{
				bool  isnull;
				Datum id = SPI_getbinval(SPI_tuptable->vals[rr],
									 SPI_tuptable->tupdesc, 1, &isnull);

				if (!isnull)
				{
					candidate_ids[n_candidates] = copy_bytea_datum(id);
					candidate_words[n_candidates] = surface_words[rr];
					n_candidates++;
				}
			}

			if (n_candidates > 0)
			{
				ArrayType *candidate_array = construct_array(candidate_ids, n_candidates,
													 BYTEAOID, -1, false, TYPALIGN_INT);
				Oid        eat[1] = { BYTEAARRAYOID };
				Datum      eav[1] = { PointerGetDatum(candidate_array) };

				rc = SPI_execute_plan(
					ensure_lexical_plan(&batch_exists_bitmap_plan,
										"SELECT ord::int4 - 1 "
										"FROM unnest($1::bytea[]) WITH ORDINALITY AS u(id, ord) "
										"WHERE consensus.entity_exists(u.id)",
										1, eat, "word_case_variants_batch"),
					eav, NULL, true, 0);
				if (rc != SPI_OK_SELECT)
					elog(ERROR, "word_case_variants_batch: existence batch failed: %s",
						 SPI_result_code_string(rc));
				for (uint64 rr = 0; rr < SPI_processed; rr++)
				{
					bool  isnull;
					int32 idx = DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[rr],
														SPI_tuptable->tupdesc, 1, &isnull));

					if (!isnull && idx >= 0 && idx < n_candidates)
					{
						bytea *id = DatumGetByteaPP(candidate_ids[idx]);

						batch_word_add_variant(&words[candidate_words[idx]],
										   VARDATA_ANY(id));
					}
				}
			}
		}
	}

	if (classes_only)
	{
		laplace_spi_finish(need_finish);
		return (Datum) 0;
	}

	/* First input occurrence, then the scalar function's bytewise variant order. */
	for (int w = 0; w < n_words; w++)
	{
		qsort(words[w].variants, (size_t) words[w].n_variants, 16, id16_cmp);
		for (int v = 0; v < words[w].n_variants; v++)
		{
			Datum values[2] = {
				make_bytea16(words[w].key),
				make_bytea16(words[w].variants + (Size) v * 16)
			};
			bool nulls[2] = { false, false };

			tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
		}
	}

	laplace_spi_finish(need_finish);
	return (Datum) 0;
}

PG_FUNCTION_INFO_V1(pg_laplace_word_case_variants_batch);

Datum
pg_laplace_word_case_variants_batch(PG_FUNCTION_ARGS)
{
	return word_case_batch(fcinfo, false);
}

/* Share the exact lower-surface construction with variants, without minting
 * identities or requiring that the lower-case word already exists. */
PG_FUNCTION_INFO_V1(pg_laplace_word_case_classes_batch);

Datum
pg_laplace_word_case_classes_batch(PG_FUNCTION_ARGS)
{
	return word_case_batch(fcinfo, true);
}
