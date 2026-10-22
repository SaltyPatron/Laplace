#include "laplace/core/text_identity.h"
#include "laplace/tier0.h"
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <pthread.h>
#endif

_Static_assert(sizeof(hash128_t) == sizeof(lp_id), "text identities are Native IDs");

void *text_identity_create(void) {
    const lp_tier0_record *floor = lp_tier0_map(NULL);
    return floor ? lp_text_new(floor) : NULL;
}

void text_identity_destroy(void *context) {
    lp_text_free(context);
}

int text_identity_utf8(void *context, const uint8_t *bytes, size_t length, hash128_t *out) {
    return lp_text_id(context, (const char *)bytes, length, (lp_id *)out) ? 0 : -1;
}

#ifdef _WIN32
static INIT_ONCE text_once = INIT_ONCE_STATIC_INIT;
static DWORD text_slot = FLS_OUT_OF_INDEXES;
static void WINAPI thread_text_destroy(void *context) { text_identity_destroy(context); }
static BOOL CALLBACK thread_text_init(PINIT_ONCE once, void *parameter, void **context) {
    (void)once; (void)parameter; (void)context;
    text_slot = FlsAlloc(thread_text_destroy);
    return text_slot != FLS_OUT_OF_INDEXES;
}
static void *thread_text(void) {
    if (!InitOnceExecuteOnce(&text_once, thread_text_init, NULL, NULL)) return NULL;
    void *context = FlsGetValue(text_slot);
    if (!context) {
        context = text_identity_create();
        if (context && !FlsSetValue(text_slot, context)) {
            text_identity_destroy(context); return NULL;
        }
    }
    return context;
}
#else
static pthread_once_t text_once = PTHREAD_ONCE_INIT;
static pthread_key_t text_slot;
static int text_slot_status;
static void thread_text_init(void) {
    text_slot_status = pthread_key_create(&text_slot, text_identity_destroy);
}
static void *thread_text(void) {
    if (pthread_once(&text_once, thread_text_init) || text_slot_status) return NULL;
    void *context = pthread_getspecific(text_slot);
    if (!context) {
        context = text_identity_create();
        if (context && pthread_setspecific(text_slot, context)) {
            text_identity_destroy(context); return NULL;
        }
    }
    return context;
}
#endif

int hash128_text(const uint8_t *bytes, size_t length, hash128_t *out) {
    void *context = thread_text();
    return context ? text_identity_utf8(context, bytes, length, out) : -2;
}

void *text_identity_thread_context(void) { return thread_text(); }

int text_identity_atom(uint32_t scalar, hash128_t *id, double coord[4], hilbert128_t *hilbert) {
    if (!id || !coord || !hilbert || !lp_unicode_scalar(scalar)) return -1;
    const lp_tier0_record *floor = lp_tier0_map(NULL);
    if (!floor) return -2;
    lp_ref atom = lp_ref_atom(floor, scalar);
    memcpy(id, &atom.id, sizeof *id);
    lp_coord_xyzm(&atom.c, coord);
    hilbert4d_encode(coord, hilbert);
    return 0;
}
