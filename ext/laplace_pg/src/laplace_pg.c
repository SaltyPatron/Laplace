/*
 * laplace_pg.c — PostgreSQL extension entry point.
 *
 * Phase 1 / Track A — scaffolding only. PG_init currently does nothing beyond
 * registering the extension as loadable. Real type registration (GEOMETRY4D
 * subtype family, S³ domain, BOX4D), GiST/SP-GiST 4D operator class
 * registration, and runtime CPU dispatch via CpuidService land in Phase 2 /
 * Track B + C2 as services come online.
 *
 * Same source compiles into:
 *   - laplace_pg shared library (loaded by PostgreSQL; LAPLACE_BUILD_PG_EXTENSION)
 *   - laplace_native shared library (linked by managed code via P/Invoke;
 *     LAPLACE_BUILD_NATIVE_DLL)
 *
 * The PG_FUNCTION_INFO_V1 wrappers compile only when LAPLACE_BUILD_PG_EXTENSION
 * is defined; the bare C symbols compile in both cases.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION
#  include "postgres.h"
#  include "fmgr.h"

PG_MODULE_MAGIC;

void _PG_init(void);
void _PG_init(void)
{
    /* Phase 2 will register types, operator classes, and dispatch CpuidService. */
}
#endif

/*
 * Plain C entry that links into laplace_native.dll.
 * Returning the build version lets managed P/Invoke verify the native library
 * matches the expected ABI before any other call is attempted.
 */
#include <stdint.h>

#if defined(_MSC_VER)
#  define LAPLACE_EXPORT __declspec(dllexport)
#else
#  define LAPLACE_EXPORT __attribute__((visibility("default")))
#endif

LAPLACE_EXPORT uint32_t laplace_native_version(void)
{
    return ((uint32_t) LAPLACE_VERSION_MAJOR << 16)
         | ((uint32_t) LAPLACE_VERSION_MINOR <<  8)
         | ((uint32_t) LAPLACE_VERSION_PATCH);
}
