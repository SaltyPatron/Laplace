#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The staged load. A source is extracted completely into staging before any of it
 * is processed; these operations then stream staged records out of PostgreSQL once,
 * in the order they need, and return binary COPY streams to write straight back.
 * Input arrives as slices of one binary COPY stream (header first); output is taken
 * in pieces (the first carries the header, the piece after final=1 ends with the
 * trailer). Memory is one claim or one cell, not the source.
 */
typedef struct laplace_staged_claims laplace_staged_claims_t;
typedef struct laplace_staged_score laplace_staged_score_t;

/* Claims (the 14 attestation columns) sorted by attestation id in, one merged claim
 * per id out: games and score sums added, qualifiers OR-ed, the latest observation's
 * row kept, the outcome classified from the totals. */
laplace_staged_claims_t* laplace_staged_claims_new(void);
void laplace_staged_claims_free(laplace_staged_claims_t* merge);
const char* laplace_staged_claims_error(const laplace_staged_claims_t* merge);
int laplace_staged_claims_feed(laplace_staged_claims_t* merge, const uint8_t* bytes, size_t n, int final);
const uint8_t* laplace_staged_claims_output(laplace_staged_claims_t* merge, size_t* len);
void laplace_staged_claims_consume(laplace_staged_claims_t* merge);
void laplace_staged_claims_counts(const laplace_staged_claims_t* merge, uint64_t* rows_in, uint64_t* rows_out);

/* New evidence in, sorted by (subject, type, object, witness, opponent rating,
 * opponent rd); row: subject, type, object?, witness, opponent rating, opponent rd,
 * games, score sum, observed at, prior rating?, prior rd?, prior volatility?. The
 * consensus id of each (subject, type, object) cell is computed natively.
 * Out: one rating period per witness per cell on the cell's prior standing; novel
 * cells (standing = 0) and cells with prior standing (standing = 1), each row
 * id, subject, type, object, rating, rd, volatility, games, last observed at. */
laplace_staged_score_t* laplace_staged_score_new(void);
void laplace_staged_score_free(laplace_staged_score_t* score);
const char* laplace_staged_score_error(const laplace_staged_score_t* score);
int laplace_staged_score_feed(laplace_staged_score_t* score, const uint8_t* bytes, size_t n, int final);
const uint8_t* laplace_staged_score_output(laplace_staged_score_t* score, int standing, size_t* len);
void laplace_staged_score_consume(laplace_staged_score_t* score, int standing);
void laplace_staged_score_counts(const laplace_staged_score_t* score, uint64_t* cells, uint64_t* games);

#ifdef __cplusplus
}
#endif
