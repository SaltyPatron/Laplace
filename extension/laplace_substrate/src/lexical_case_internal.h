#ifndef LAPLACE_SUBSTRATE_LEXICAL_CASE_INTERNAL_H
#define LAPLACE_SUBSTRATE_LEXICAL_CASE_INTERNAL_H

#include "postgres.h"
#include "executor/spi.h"

typedef struct BatchWord
{
	char          key[16];
	char         *variants;
	int           n_variants;
	int           variant_cap;
} BatchWord;

typedef struct BatchWordLookup
{
	char key[16];
	int  index;
} BatchWordLookup;

SPIPlanPtr ensure_lexical_plan(SPIPlanPtr *slot, const char *sql, int nargs, Oid *argtypes, const char *label);
Datum make_bytea16(const char *raw);
bool datum_key16(Datum d, char *out);
void batch_word_add_variant(BatchWord *word, const char *key);
int id16_cmp(const void *a, const void *b);

#endif
