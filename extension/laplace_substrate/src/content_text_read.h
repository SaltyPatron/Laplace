#ifndef LAPLACE_CONTENT_TEXT_READ_H
#define LAPLACE_CONTENT_TEXT_READ_H
#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* Resolves input forms to stored entities: each form is composed under the
 * canonical recipe; its root, every trajectory where the root's operands occur
 * in order, and the full container closure above those. Returns distinct stored
 * ids in byte order. Writes nothing. */
hash128_t *laplace_content_text_containers(ArrayType *forms, int *count);
#endif
