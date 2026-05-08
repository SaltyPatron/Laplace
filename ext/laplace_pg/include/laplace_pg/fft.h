/*
 * fft.h — FftService public API.
 *
 * Phase 2 / Track B / Service B20.
 *
 * Forward and inverse 1-D complex FFTs of arbitrary length n via Intel
 * oneMKL DFTI. Used by:
 *   - B21 SpectralFeatureService (downstream consumer)
 *   - Audio modality decomposer (semantic spec extraction via harmonic
 *     analysis of waveform samples)
 *   - Diffusion-model probe runners (frequency-domain residual analysis)
 *
 * Buffers are interleaved complex doubles: pairs (re, im) packed as
 * adjacent doubles. Length n is the count of complex points, so the
 * buffer size in doubles is 2 * n.
 *
 * Forward FFT is unscaled (X[k] = Σ x[n] * exp(-2πi k n / N)). Inverse
 * FFT divides by N so that round-trip x → X → x recovers the input
 * (within float epsilon). This matches MKL's default scaling convention
 * for DFTI_BACKWARD with DFTI_BACKWARD_SCALE = 1/N.
 */

#ifndef LAPLACE_FFT_H
#define LAPLACE_FFT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Compute the forward 1-D complex FFT of `n` points in-place.
 *
 * Buffer layout (doubles): [re_0, im_0, re_1, im_1, ..., re_{n-1}, im_{n-1}].
 * The same buffer holds output after the call.
 *
 * Returns 0 on success, nonzero on MKL error.
 */
int laplace_fft_forward_d(double *interleaved_complex, int n);

/*
 * Compute the inverse 1-D complex FFT of `n` points in-place. Scaled by
 * 1/n so x = inverse(forward(x)) within float epsilon.
 *
 * Returns 0 on success, nonzero on MKL error.
 */
int laplace_fft_inverse_d(double *interleaved_complex, int n);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_FFT_H */
