/*
 * Syzygy tablebase probe kernel — Laplace ABI over the vendored Fathom
 * prober. See include/laplace/core/syzygy.h for the contract.
 *
 * Vendored dependency pin (repo law: record upstream commit + hash):
 *   external/fathom = https://github.com/jdart1/Fathom
 *   commit c9c6fef0dddc05d2e242c183acf5833149ab676d (MIT)
 *
 * Locking: tb_init and tb_probe_root are not thread-safe (Fathom documents
 * both); tb_probe_wdl is thread-safe once initialized. One process-wide lock
 * serializes init/free/root-probe; WDL probes run lock-free in parallel —
 * the shape the ingest pipeline's parallel Compose needs.
 */

#include "laplace/core/syzygy.h"

#include <tbprobe.h>

#ifdef _WIN32
#include <windows.h>
static SRWLOCK g_syzygy_lock = SRWLOCK_INIT;
static void syzygy_lock(void)   { AcquireSRWLockExclusive(&g_syzygy_lock); }
static void syzygy_unlock(void) { ReleaseSRWLockExclusive(&g_syzygy_lock); }
#else
#include <pthread.h>
static pthread_mutex_t g_syzygy_lock = PTHREAD_MUTEX_INITIALIZER;
static void syzygy_lock(void)   { pthread_mutex_lock(&g_syzygy_lock); }
static void syzygy_unlock(void) { pthread_mutex_unlock(&g_syzygy_lock); }
#endif

static int g_largest = 0;

int laplace_syzygy_init(const char* path)
{
    if (path == NULL || path[0] == '\0')
        return -1;
    syzygy_lock();
    int rc;
    if (!tb_init(path)) {
        g_largest = 0;
        rc = -1;
    } else {
        g_largest = (int)TB_LARGEST;
        rc = g_largest;
    }
    syzygy_unlock();
    return rc;
}

void laplace_syzygy_free(void)
{
    syzygy_lock();
    tb_free();
    g_largest = 0;
    syzygy_unlock();
}

int laplace_syzygy_largest(void)
{
    return g_largest;
}

int laplace_syzygy_probe_wdl(
    uint64_t white, uint64_t black, uint64_t kings, uint64_t queens,
    uint64_t rooks, uint64_t bishops, uint64_t knights, uint64_t pawns,
    unsigned ep, int white_to_move)
{
    if (g_largest == 0)
        return -1;
    /* rule50 = 0, castling = 0 by contract (see header): the rule50-agnostic
     * verdict is the fact keyed by Laplace position identity. */
    unsigned res = tb_probe_wdl(
        white, black, kings, queens, rooks, bishops, knights, pawns,
        0 /* rule50 */, 0 /* castling */, ep, white_to_move != 0);
    if (res == TB_RESULT_FAILED)
        return -1;
    return (int)res;
}

int laplace_syzygy_probe_root(
    uint64_t white, uint64_t black, uint64_t kings, uint64_t queens,
    uint64_t rooks, uint64_t bishops, uint64_t knights, uint64_t pawns,
    unsigned ep, int white_to_move, int* out_wdl, int* out_dtz,
    int* out_from, int* out_to, int* out_promotes)
{
    if (g_largest == 0 || out_wdl == NULL || out_dtz == NULL
        || out_from == NULL || out_to == NULL || out_promotes == NULL)
        return -1;
    syzygy_lock();
    unsigned res = tb_probe_root(
        white, black, kings, queens, rooks, bishops, knights, pawns,
        0 /* rule50 */, 0 /* castling */, ep, white_to_move != 0, NULL);
    syzygy_unlock();
    if (res == TB_RESULT_FAILED
        || res == TB_RESULT_CHECKMATE
        || res == TB_RESULT_STALEMATE)
        return -1;
    *out_wdl = (int)TB_GET_WDL(res);
    *out_dtz = (int)TB_GET_DTZ(res);
    *out_from = (int)TB_GET_FROM(res);
    *out_to = (int)TB_GET_TO(res);
    *out_promotes = (int)TB_GET_PROMOTES(res);
    return 0;
}
