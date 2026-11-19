#ifndef LAPLACE_CONVERSE_BINDINGS_H
#define LAPLACE_CONVERSE_BINDINGS_H

#include "postgres.h"
#include "laplace/core/hash128.h"

typedef struct ConverseLexiconTextRow
{
    hash128_t entity;
    char     *content;
    bool      content_null;
    bool      textual;
} ConverseLexiconTextRow;

typedef struct ConverseBindingRow
{
    hash128_t term;
    hash128_t key;
    hash128_t binding;
    hash128_t claim;
    hash128_t language;
    bool      language_null;
    bool      in_lang;
    double    eff_mu;
    int64     witnesses;
    bool      witnesses_null;
    int64     games;
    int64     rating;
    int64     rd;
} ConverseBindingRow;

typedef struct ConverseWordLangRow
{
    hash128_t word;
    hash128_t lang;
    double    mu;
} ConverseWordLangRow;

typedef struct LaplaceSenseRow
{
    hash128_t term;
    hash128_t sense_id;
    hash128_t synset_id;
    double    eff_mu;
    int64     witnesses;
} LaplaceSenseRow;

extern ConverseLexiconTextRow *converse_lexicon_texts_c(
    const hash128_t *ids,
    int              n_ids,
    int             *out_count);

extern ConverseBindingRow *converse_binding_evidence_c(
    const hash128_t *terms,
    int              n_terms,
    const hash128_t *lang,
    bool             support,
    int             *out_count);

extern ConverseBindingRow *converse_key_binding_evidence_c(
    const hash128_t *keys,
    int              n_keys,
    const hash128_t *lang,
    bool             support,
    int             *out_count);

extern ConverseWordLangRow *converse_word_language_candidates_batch_c(
    const hash128_t *words,
    int              n_words,
    int             *out_count);

extern LaplaceSenseRow *laplace_senses_batch_c(
    const hash128_t *words,
    int              n_words,
    int             *out_count);

extern bool converse_legacy_consensus_has_rows(void);

extern void laplace_are_iso639_lang_ids(
    const hash128_t *ids,
    int              n_ids,
    bool            *out_is_lang);

#endif /* LAPLACE_CONVERSE_BINDINGS_H */
