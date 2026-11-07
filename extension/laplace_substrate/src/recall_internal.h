#ifndef LAPLACE_SUBSTRATE_RECALL_INTERNAL_H
#define LAPLACE_SUBSTRATE_RECALL_INTERNAL_H

#include "postgres.h"
#include "funcapi.h"
#include "utils/memutils.h"

typedef struct ReplyRow
{
    Datum reply;
    Datum mu;
    Datum witnesses;
    bool  reply_null;
    bool  mu_null;
    bool  wit_null;
} ReplyRow;

typedef struct ReplyBuf
{
    MemoryContext cxt;
    ReplyRow *rows;
    int       n;
    int       cap;
} ReplyBuf;

void reply_buf_init(ReplyBuf *buf);
void reply_buf_add(ReplyBuf *buf, Datum reply, Datum mu, Datum witnesses,
                   bool reply_null, bool mu_null, bool wit_null);
void emit_reply_row(ReturnSetInfo *rsinfo, Datum reply, Datum mu, Datum witnesses,
                    bool reply_null, bool mu_null, bool wit_null);
void reply_buf_emit(ReturnSetInfo *rsinfo, ReplyBuf *buf);
int spi_forward_replies(ReplyBuf *buf, const char *query,
                        int nparams, Oid *types, Datum *values, const char *nulls);

#endif
