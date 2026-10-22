#pragma once
#include "hash128.h"
#include "hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* A context belongs to one caller/thread. Native retains the validated,
 * read-only scalar mapping for process lifetime and reuses its text arenas. */
void *text_identity_create(void);
void text_identity_destroy(void *context);
int text_identity_utf8(void *context, const uint8_t *bytes, size_t length, hash128_t *out);
/* Native C callers share one reusable context per OS thread, released on exit. */
int hash128_text(const uint8_t *bytes, size_t length, hash128_t *out);
void *text_identity_thread_context(void);
int text_identity_atom(uint32_t scalar, hash128_t *id, double coord[4], hilbert128_t *hilbert);

#ifdef __cplusplus
}
#endif
