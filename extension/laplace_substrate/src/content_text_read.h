#ifndef LAPLACE_CONTENT_TEXT_READ_H
#define LAPLACE_CONTENT_TEXT_READ_H
#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* Canonical input composition, ordered physical occurrence, then ancestor
 * closure. Results are complete, distinct IDs; labels and name attestations
 * are subsequent operations. No source data is written by this read. */
hash128_t *laplace_content_text_containers(ArrayType *forms, int *count);
#endif
