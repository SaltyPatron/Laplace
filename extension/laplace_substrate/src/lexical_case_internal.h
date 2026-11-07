#ifndef LAPLACE_SUBSTRATE_LEXICAL_CASE_INTERNAL_H
#define LAPLACE_SUBSTRATE_LEXICAL_CASE_INTERNAL_H

#include "postgres.h"
#include "executor/spi.h"
#include "utils/hsearch.h"

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

typedef struct BatchWord
{
	char          key[16];
	GraphemeItem *graphemes;
	int           n_graphemes;
	int           grapheme_cap;
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
Oid ensure_lexical_asbinary_oid(void);
Datum make_bytea16(const char *raw);
bool datum_key16(Datum d, char *out);
char *render_lookup(HTAB *r, const char *key16);
void fetch_case_mappings(HTAB *cm, Datum *subjects, int n_subjects, const char *caller);
char *piece_base(HTAB *cm, HTAB *r, const char *gkey, int slot);
void assemble_case_surfaces(GraphemeItem *graphemes, int n_graphemes,
							HTAB *cm, HTAB *rmap, char *surf[CM_SLOTS]);
void batch_word_add_grapheme(BatchWord *word, const char *key, int32 run);
void batch_word_add_variant(BatchWord *word, const char *key);
int id16_cmp(const void *a, const void *b);

#endif
