// Laplace engine — placeholder implementation.
// Real C ABI functions (coord4d, hash128, hilbert4d, etc.) populate in
// subsequent chunks per DESIGN.md.

#include "laplace/laplace.h"

extern "C" const char* laplace_version(void) {
    return "0.1.0";
}
