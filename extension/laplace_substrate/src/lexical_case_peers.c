#include "postgres.h"

#include "catalog/pg_type.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "lexical_case_internal.h"

#include <string.h>

/*
 * Lexical peers of every input word, one row per input position (NULL inputs
 * yield NULL rows). A word that is itself the subject of a HAS_SENSE-family
 * consensus cell or native sense tuple is its own only peer, and so is every
 * other word: case is content, never folded or widened. Peers are emitted in
 * bytewise order.
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
