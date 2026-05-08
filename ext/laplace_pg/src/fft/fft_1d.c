/*
 * fft_1d.c — Intel oneMKL DFTI 1-D complex FFT implementation.
 *
 * Per substrate invariant 10 (production-grade, no MVP): real implementation
 * via MKL DFTI, no homebrew Cooley-Tukey, no approximate methods.
 */

#include "laplace_pg/fft.h"

#include <mkl.h>
#include <mkl_dfti.h>

static int run_dfti(double *buf, int n, int forward)
{
    if (buf == NULL || n <= 0) { return 1; }

    DFTI_DESCRIPTOR_HANDLE handle = NULL;
    MKL_LONG status = DftiCreateDescriptor(
        &handle, DFTI_DOUBLE, DFTI_COMPLEX, 1, (MKL_LONG)n);
    if (status != DFTI_NO_ERROR) { return (int)status; }

    /* Inverse divides by n so round-trip is identity. */
    if (!forward) {
        const double scale = 1.0 / (double)n;
        status = DftiSetValue(handle, DFTI_BACKWARD_SCALE, scale);
        if (status != DFTI_NO_ERROR) {
            DftiFreeDescriptor(&handle);
            return (int)status;
        }
    }

    status = DftiCommitDescriptor(handle);
    if (status != DFTI_NO_ERROR) {
        DftiFreeDescriptor(&handle);
        return (int)status;
    }

    if (forward) {
        status = DftiComputeForward(handle, buf);
    } else {
        status = DftiComputeBackward(handle, buf);
    }
    DftiFreeDescriptor(&handle);
    return status == DFTI_NO_ERROR ? 0 : (int)status;
}

int laplace_fft_forward_d(double *interleaved_complex, int n)
{
    return run_dfti(interleaved_complex, n, /* forward = */ 1);
}

int laplace_fft_inverse_d(double *interleaved_complex, int n)
{
    return run_dfti(interleaved_complex, n, /* forward = */ 0);
}
