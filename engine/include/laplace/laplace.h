/*
 * Laplace engine — public C ABI umbrella header.
 *
 * This file currently provides only the library version. Real APIs
 * (coord4d, hash128, hilbert4d, mantissa_pack, geometry4d, glicko2,
 * astar, etc.) populate in subsequent chunks per DESIGN.md Section IV.
 *
 * ALL public engine symbols MUST be exposed via `extern "C"` and use
 * POD types only (no C++ exceptions across the boundary). This is the
 * C ABI that the PG extension wrappers AND the C# P/Invoke bindings
 * both consume.
 */
#ifndef LAPLACE_H
#define LAPLACE_H

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_VERSION_MAJOR 0
#define LAPLACE_VERSION_MINOR 1
#define LAPLACE_VERSION_PATCH 0

/* Returns a NUL-terminated version string. */
const char* laplace_version(void);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_H */
