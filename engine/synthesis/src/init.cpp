#include "laplace/synthesis/init.h"

#include "laplace/dynamics/init.h"

extern "C" int laplace_synthesis_init(void) {
    return laplace_runtime_init(LAPLACE_RUNTIME_HOST_SYNTHESIS, -1);
}
