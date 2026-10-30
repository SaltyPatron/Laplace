#ifndef LAPLACE_DECLARED_PATHS_H
#define LAPLACE_DECLARED_PATHS_H

#include "postgres.h"
#include "utils/array.h"

/* Entry relations of the same producer program used by declared path reads. */
extern ArrayType *laplace_declared_binding_types(void);

#endif
