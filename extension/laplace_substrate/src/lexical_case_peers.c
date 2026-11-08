#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "lexical_case_internal.h"
#include "spi_common.h"
#include "spi_nested.h"

#include <string.h>

static SPIPlanPtr peers_exact_one_plan = NULL;
static SPIPlanPtr peers_exact_batch_plan = NULL;
static SPIPlanPtr peers_widen_plan = NULL;

/*
 * Lexical peers of every input word, one row per input position (NULL inputs
 * yield NULL rows). A word that is itself the subject of a HAS_SENSE-family
 * consensus cell is its own only peer; every other word is widened to its
 * case variants by one word_case_variants_batch call. One indexed read
 * classifies the whole set; peers are emitted in bytewise order.
 */
PG_FUNCTION_INFO_V1(pg_laplace_lexical_peers_batch);

Datum
pg_laplace_lexical_peers_batch(PG_FUNCTION_ARGS)
{
	ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
	ArrayType     *input;
	Datum         *input_datums;
	bool          *input_nulls;
	int            n_input;
	BatchWord     *words;
	int            n_words = 0;
	HASHCTL        ctl;
	HTAB          *lookup;
	HTAB          *resolved;
	bool           need_finish = false;
	int            rc;

	if (PG_ARGISNULL(0))
		ereport(ERROR, (errmsg("lexical_peers_batch: words must not be NULL")));
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
	lookup = hash_create("lexical peer words", n_input + 1, &ctl,
					 HASH_ELEM | HASH_BLOBS);
	for (int i = 0; i < n_input; i++)
	{
		BatchWordLookup *entry;
		bytea           *word;
		bool             found;

		if (input_nulls[i])
			continue;
		word = DatumGetByteaPP(input_datums[i]);
		if (VARSIZE_ANY_EXHDR(word) != 16)
			ereport(ERROR, (errmsg("lexical_peers_batch: words must be 16-byte ids")));
		entry = (BatchWordLookup *) hash_search(lookup, VARDATA_ANY(word),
											 HASH_ENTER, &found);
		if (!found)
		{
			entry->index = n_words;
			memcpy(words[n_words].key, VARDATA_ANY(word), 16);
			batch_word_add_variant(&words[n_words], VARDATA_ANY(word));
			n_words++;
		}
	}

	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = 16;
	resolved = hash_create("lexical peer exact words", n_words + 1, &ctl,
					   HASH_ELEM | HASH_BLOBS);

	if (n_words > 0 && laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
		elog(ERROR, "lexical_peers_batch: SPI_connect failed");

	if (n_words == 1)
	{
		Oid   at[1] = { BYTEAOID };
		Datum av[1] = { make_bytea16(words[0].key) };
		bool  isnull;
		bool  exists;

		rc = SPI_execute_plan(
			ensure_lexical_plan(
				&peers_exact_one_plan,
				"SELECT EXISTS ("
				"  SELECT 1 FROM public.laplace_holders("
				"      ARRAY[public.blake3_from_bytea($1)], '{}'::public.blake3[], false, -1::smallint, -1::bigint, 2::smallint,"
				"      ARRAY[public.laplace_bank_bit('kind', 'tuple')], 1::smallint) h "
				"  WHERE public.laplace_mask_has_value(h.mask, 'kind', 'tuple') "
				"    AND public.laplace_path_length(h.path) = 3 "
				"    AND (public.laplace_parts(h.path))[1] = public.blake3_from_bytea($1)"
				") OR EXISTS ("
				"  SELECT 1 FROM laplace.consensus c "
				"  WHERE c.subject_id = $1 "
				"    AND c.type_id = ANY(consensus.relation_family_ids('HAS_SENSE'))"
				")",
				1, at, "lexical_peers_batch"),
			av, NULL, true, 1);
		if (rc != SPI_OK_SELECT || SPI_processed != 1)
			elog(ERROR, "lexical_peers_batch: exact probe failed: %s",
				 SPI_result_code_string(rc));
		exists = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
										 SPI_tuptable->tupdesc, 1, &isnull));
		if (!isnull && exists)
			(void) hash_search(resolved, words[0].key, HASH_ENTER, NULL);
	}
	else if (n_words > 1)
	{
		Datum     *ids = (Datum *) palloc(sizeof(Datum) * n_words);
		ArrayType *id_array;
		Oid        at[1] = { BYTEAARRAYOID };
		Datum      av[1];

		for (int i = 0; i < n_words; i++)
			ids[i] = make_bytea16(words[i].key);
		id_array = construct_array(ids, n_words, BYTEAOID, -1, false, TYPALIGN_INT);
		av[0] = PointerGetDatum(id_array);
		rc = SPI_execute_plan(
			ensure_lexical_plan(
				&peers_exact_batch_plan,
				"SELECT DISTINCT public.blake3_send((public.laplace_parts(h.path))[1]) "
				"FROM public.laplace_holders("
				"    ARRAY(SELECT public.blake3_from_bytea(u.id) FROM unnest($1::bytea[]) u(id)),"
				"    '{}'::public.blake3[], false, -1::smallint, -1::bigint, 2::smallint,"
				"    ARRAY[public.laplace_bank_bit('kind', 'tuple')], 1::smallint) h "
				"WHERE public.laplace_mask_has_value(h.mask, 'kind', 'tuple') "
				"  AND public.laplace_path_length(h.path) = 3 "
				"UNION "
				"SELECT DISTINCT c.subject_id FROM laplace.consensus c "
				"WHERE c.subject_id = ANY($1) "
				"  AND c.type_id = ANY(consensus.relation_family_ids('HAS_SENSE'))",
				1, at, "lexical_peers_batch"),
			av, NULL, true, 0);
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "lexical_peers_batch: exact batch probe failed: %s",
				 SPI_result_code_string(rc));
		for (uint64 row = 0; row < SPI_processed; row++)
		{
			bool  isnull;
			Datum d = SPI_getbinval(SPI_tuptable->vals[row],
								SPI_tuptable->tupdesc, 1, &isnull);
			char  key[16];

			if (!isnull && datum_key16(d, key))
				(void) hash_search(resolved, key, HASH_ENTER, NULL);
		}
	}

	/* Widen only exact-sense misses, in one native batch. */
	if (n_words > 0)
	{
		Datum *misses = (Datum *) palloc(sizeof(Datum) * n_words);
		int    n_misses = 0;

		for (int i = 0; i < n_words; i++)
			if (hash_search(resolved, words[i].key, HASH_FIND, NULL) == NULL)
				misses[n_misses++] = make_bytea16(words[i].key);
		if (n_misses > 0)
		{
			ArrayType *miss_array = construct_array(misses, n_misses, BYTEAOID,
											-1, false, TYPALIGN_INT);
			Oid        at[1] = { BYTEAARRAYOID };
			Datum      av[1] = { PointerGetDatum(miss_array) };

			rc = SPI_execute_plan(
				ensure_lexical_plan(
					&peers_widen_plan,
					"SELECT word_id, variant_id "
					"FROM lexical.word_case_variants_batch($1)",
					1, at, "lexical_peers_batch"),
				av, NULL, true, 0);
			if (rc != SPI_OK_SELECT)
				elog(ERROR, "lexical_peers_batch: case widening failed: %s",
					 SPI_result_code_string(rc));
			for (uint64 row = 0; row < SPI_processed; row++)
			{
				bool             isnull;
				char             wkey[16], vkey[16];
				Datum            wd;
				Datum            vd;
				BatchWordLookup *entry;

				wd = SPI_getbinval(SPI_tuptable->vals[row],
								   SPI_tuptable->tupdesc, 1, &isnull);
				if (isnull || !datum_key16(wd, wkey))
					continue;
				vd = SPI_getbinval(SPI_tuptable->vals[row],
								   SPI_tuptable->tupdesc, 2, &isnull);
				if (isnull || !datum_key16(vd, vkey))
					continue;
				entry = (BatchWordLookup *) hash_search(lookup, wkey,
													 HASH_FIND, NULL);
				if (entry != NULL)
					batch_word_add_variant(&words[entry->index], vkey);
			}
		}
	}

	laplace_spi_finish(need_finish);

	for (int i = 0; i < n_input; i++)
	{
		Datum values[3];
		bool  out_nulls[3] = { false, false, false };

		values[0] = Int64GetDatum((int64) i + 1);
		if (input_nulls[i])
		{
			out_nulls[1] = true;
			out_nulls[2] = true;
		}
		else
		{
			char             key[16];
			BatchWordLookup *entry;
			BatchWord       *word;
			Datum           *peer_datums;

			(void) datum_key16(input_datums[i], key);
			entry = (BatchWordLookup *) hash_search(lookup, key, HASH_FIND, NULL);
			word = &words[entry->index];
			qsort(word->variants, (size_t) word->n_variants, 16, id16_cmp);
			peer_datums = (Datum *) palloc(sizeof(Datum) * word->n_variants);
			for (int j = 0; j < word->n_variants; j++)
				peer_datums[j] = make_bytea16(word->variants + (Size) j * 16);
			values[1] = make_bytea16(key);
			values[2] = PointerGetDatum(construct_array(peer_datums,
												word->n_variants, BYTEAOID,
												-1, false, TYPALIGN_INT));
		}
		tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, out_nulls);
	}
	return (Datum) 0;
}
