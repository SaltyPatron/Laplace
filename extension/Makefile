# Laplace PostgreSQL extension — PGXS-based build.
# Per RULES.md R6: this extension is THIN wrappers around the C engine.
# Real entity math lives in liblaplace_engine.so. Only Glicko-2 update
# (via CREATE AGGREGATE) runs SQL-side.

MODULE_big = laplace
OBJS = src/laplace.o
EXTENSION = laplace
DATA = laplace--1.0.0.sql

# Pin to PG 18 explicitly
PG_CONFIG ?= /usr/lib/postgresql/18/bin/pg_config
PGXS := $(shell $(PG_CONFIG) --pgxs)

# Future: when extension links against liblaplace_engine.so,
# add to PG_CPPFLAGS and SHLIB_LINK:
# PG_CPPFLAGS = -I$(realpath ../engine/include)
# SHLIB_LINK = -L$(realpath ../engine/build) -llaplace_engine \
#              -Wl,-rpath,$(realpath ../engine/build)

include $(PGXS)
