#include "postgres.h"

#include "executor/spi.h"
#include "utils/memutils.h"

#include "lexical_case_internal.h"

#include <string.h>

/*
 * Shared helpers of lexical.lexical_peers_batch (lexical_case_peers.c): kept
 * plans, 16-byte id Datums and a word's peer set. A word is its own only peer:
 * case is content (Native identity), never folded or widened.
 */

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

Datum
make_bytea16(const char *raw)
{
	bytea *b = (bytea *) palloc(VARHDRSZ + 16);

	SET_VARSIZE(b, VARHDRSZ + 16);
	memcpy(VARDATA(b), raw, 16);
	return PointerGetDatum(b);
}

bool
datum_key16(Datum d, char *out)
{
	bytea *b = DatumGetByteaPP(d);

	if (VARSIZE_ANY_EXHDR(b) != 16)
		return false;
	memcpy(out, VARDATA_ANY(b), 16);
	return true;
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
