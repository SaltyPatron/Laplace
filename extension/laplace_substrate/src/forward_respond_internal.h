#ifndef LAPLACE_SUBSTRATE_FORWARD_RESPOND_INTERNAL_H
#define LAPLACE_SUBSTRATE_FORWARD_RESPOND_INTERNAL_H

#include "postgres.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "consensus_neighbors.h"
#include "spi_common.h"
#include "laplace/core/firmware_law.h"
#include "cognition_program.h"
#include "consensus_scan.h"
#include "declared_paths.h"
#include "walk_score.h"
#include "spi_nested.h"

ArrayType *respond_relation_set(const char *name);

#endif
