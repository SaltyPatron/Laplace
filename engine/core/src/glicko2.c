#include "laplace/core/glicko2.h"

#include <math.h>
#include <stddef.h>
#include <stdint.h>

#define LAPLACE_FP_ONE      1000000000LL
#define LAPLACE_FP_HALF      500000000LL
#define LAPLACE_FP_LN2       693147181LL
#define LAPLACE_FP_LN2_HALF  346573590LL
#define LAPLACE_FP_PI       3141592654LL
#define LAPLACE_FP_PI_SQ    9869604401LL

#define LAPLACE_FP_BASE_RATING   LAPLACE_GLICKO2_NEUTRAL_MU_FP
#define LAPLACE_FP_RATING_SCALE   173717800000LL
#define LAPLACE_FP_RD_MAX         350000000000LL

/*
 * The old fold let ordinary signed-int64 arithmetic surround a handful of
 * __int128 intermediates.  Once a rating escaped the normal Glicko range those
 * edges were enough to invoke signed overflow/UB and turn one bad transition
 * into a runaway carrier.  Keep the public fixed-point surface total instead:
 * every carrier operation has a deterministic saturating result, while the
 * stateful Glicko entry points still reject an illegal state rather than
 * publishing saturation as a rating.
 */
static int64_t clamp_i128(__int128 value)
{
    if (value > (__int128)INT64_MAX) return INT64_MAX;
    if (value < (__int128)INT64_MIN) return INT64_MIN;
    return (int64_t)value;
}

static int64_t sat_add_i64(int64_t a, int64_t b)
{
    return clamp_i128((__int128)a + (__int128)b);
}

static int64_t sat_sub_i64(int64_t a, int64_t b)
{
    return clamp_i128((__int128)a - (__int128)b);
}

static int64_t sat_neg_i64(int64_t value)
{
    return value == INT64_MIN ? INT64_MAX : -value;
}

static int64_t sat_scale_i64(int64_t value, int64_t multiplier)
{
    return clamp_i128((__int128)value * (__int128)multiplier);
}

static int glicko2_state_is_admissible(const glicko2_state_t *st)
{
    return st != NULL &&
           (__int128)st->rating - 2 * (__int128)st->rd > INT64_MIN &&
           (__int128)st->rating + 2 * (__int128)st->rd < INT64_MAX &&
           st->rd > 0 && st->rd <= LAPLACE_FP_RD_MAX &&
           st->volatility > 0 && st->observation_count >= 0;
}

int64_t laplace_fp_mul(int64_t a, int64_t b)
{
    __int128 prod = (__int128)a * (__int128)b;
    __int128 rounded;
    if (prod >= 0)
        rounded = (prod + (LAPLACE_FP_ONE / 2)) / LAPLACE_FP_ONE;
    else
        rounded = -(((-prod) + (LAPLACE_FP_ONE / 2)) / LAPLACE_FP_ONE);
    return clamp_i128(rounded);
}

int64_t laplace_fp_div(int64_t a, int64_t b)
{
    if (b == 0)
        return (a >= 0) ? INT64_MAX : INT64_MIN;

    /* Do not form -INT64_MIN in int64.  Both magnitudes live in int128 until
     * the rounded quotient has been range-checked. */
    __int128 aa = (__int128)a;
    __int128 bb = (__int128)b;
    const int negative = ((a < 0) ^ (b < 0));
    if (aa < 0) aa = -aa;
    if (bb < 0) bb = -bb;
    __int128 q = (aa * (__int128)LAPLACE_FP_ONE + bb / 2) / bb;
    if (negative) q = -q;
    return clamp_i128(q);
}

static uint64_t isqrt_u128(__int128 n)
{
    if (n <= 0) return 0;
    uint64_t hi = (uint64_t)(n >> 64);
    uint64_t lo = (uint64_t)n;
    int bits = 0;
    if (hi) {
        bits = 64;
        uint64_t t = hi;
        while (t) { bits++; t >>= 1; }
    } else {
        uint64_t t = lo;
        while (t) { bits++; t >>= 1; }
    }
    __int128 x = (__int128)1 << ((bits + 1) / 2);
    for (int i = 0; i < 80; ++i) {
        __int128 y = (x + n / x) >> 1;
        if (y >= x) break;
        x = y;
    }
    return (uint64_t)x;
}

int64_t laplace_fp_sqrt(int64_t x)
{
    if (x <= 0) return 0;
    __int128 scaled = (__int128)x * (__int128)LAPLACE_FP_ONE;
    return (int64_t)isqrt_u128(scaled);
}

int64_t laplace_fp_exp(int64_t x)
{
    /*
     * Q1e9 spends roughly 30 carrier bits on 1.0.  Range reduction therefore
     * cannot treat the reduced mantissa as an unscaled integer: shifting a
     * ~1e9 result left by only ~34 bits already crosses signed int64.
     *
     * Clamp BEFORE range-reduction arithmetic so INT64_MIN/INT64_MAX cannot
     * overflow in -x or x + ln(2)/2.  Positive overflow saturates with one
     * Q1e9 unit of headroom because the logistic computes 1 + exp(x).
     * Negative underflow saturates to the smallest positive Q1e9 value: exp(x)
     * is strictly positive, and a zero here can become an illegal Glicko
     * volatility even though PostgreSQL correctly requires volatility > 0.
     */
    const int64_t saturation = INT64_MAX - LAPLACE_FP_ONE;
    const int64_t min_positive = 1;
    const int64_t range_limit = 60LL * LAPLACE_FP_ONE;
    if (x >= range_limit) return saturation;
    if (x <= -range_limit) return min_positive;

    int64_t k;
    if (x >= 0)
        k = (x + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2;
    else
        k = -(((-x) + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2);
    int64_t r = x - k * LAPLACE_FP_LN2;

    int64_t result = LAPLACE_FP_ONE;
    int64_t term   = LAPLACE_FP_ONE;
    for (int n = 1; n <= 14; ++n) {
        term = laplace_fp_mul(term, r) / n;
        result = sat_add_i64(result, term);
        if (term > -10 && term < 10) break;
    }

    if (result <= 0) return min_positive;

    if (k > 0) {
        if (k >= 63) return saturation;
        if (result > (saturation >> k)) return saturation;
        result <<= k;
    } else if (k < 0) {
        int64_t shift = -k;
        if (shift >= 63) return min_positive;
        int64_t add = INT64_C(1) << (shift - 1);
        result = (result + add) >> shift;
    }
    if (result < min_positive) return min_positive;
    return result > saturation ? saturation : result;
}

int64_t laplace_fp_log(int64_t x)
{
    if (x <= 0) return INT64_MIN / 2;

    int64_t y = x;
    int k = 0;
    while (y >= 2 * LAPLACE_FP_ONE) { y >>= 1; ++k; }
    while (y < LAPLACE_FP_ONE)      { y <<= 1; --k; }

    int64_t num = y - LAPLACE_FP_ONE;
    int64_t den = y + LAPLACE_FP_ONE;
    int64_t u   = laplace_fp_div(num, den);
    int64_t u2  = laplace_fp_mul(u, u);

    int64_t term = u;
    int64_t sum = term;
    for (int i = 3; i <= 31; i += 2) {
        term = laplace_fp_mul(term, u2);
        int64_t inc = term / i;
        sum = sat_add_i64(sum, inc);
        if (inc > -1 && inc < 1) break;
    }
    int64_t ln_m = sat_scale_i64(sum, 2);
    return sat_add_i64(sat_scale_i64((int64_t)k, LAPLACE_FP_LN2), ln_m);
}

void glicko2_init(glicko2_state_t* st,
                  int64_t r0,
                  int64_t rd0,
                  int64_t vol0)
{
    if (!st) return;
    st->rating                    = r0;
    st->rd                        = rd0;
    st->volatility                = vol0;
    st->last_observed_at_unix_ns  = 0;
    st->observation_count         = 0;
}

static int64_t g1_to_mu(int64_t r)
{
    return laplace_fp_div(sat_sub_i64(r, LAPLACE_FP_BASE_RATING),
                          LAPLACE_FP_RATING_SCALE);
}

static int64_t g1_to_phi(int64_t rd)
{
    return laplace_fp_div(rd, LAPLACE_FP_RATING_SCALE);
}

static int64_t phi_to_g1(int64_t phi)
{
    return laplace_fp_mul(phi, LAPLACE_FP_RATING_SCALE);
}

int64_t laplace_glicko2_g(int64_t phi)
{
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t three_phi_sq = sat_scale_i64(phi_sq, 3);
    int64_t denom_inside = sat_add_i64(
        LAPLACE_FP_ONE,
        laplace_fp_div(three_phi_sq, LAPLACE_FP_PI_SQ));
    int64_t denom = laplace_fp_sqrt(denom_inside);
    return laplace_fp_div(LAPLACE_FP_ONE, denom);
}

int64_t laplace_glicko2_E(int64_t mu, int64_t mu_j, int64_t g_j)
{
    int64_t difference = sat_sub_i64(mu, mu_j);
    int64_t arg = sat_neg_i64(laplace_fp_mul(g_j, difference));
    int64_t ex = laplace_fp_exp(arg);
    int64_t denom = sat_add_i64(LAPLACE_FP_ONE, ex);
    int64_t result = laplace_fp_div(LAPLACE_FP_ONE, denom);
    if (result < 0) return 0;
    if (result > LAPLACE_FP_ONE) return LAPLACE_FP_ONE;
    return result;
}


/* Period internals need more range than the published Q1e9 carrier. Squaring
 * delta or the Illinois denominator can overflow int64 even when the final
 * rating is representable. Keep quantities in checked int128 and products in
 * uint256; capacity failure is explicit and never becomes a saturated state.
 * This is deliberately private: public fixed-point helpers retain their total,
 * saturating ABI. No floating-point arithmetic or allocation enters the fold. */
typedef __int128 period_wide_t;
typedef unsigned __int128 period_uwide_t;
typedef struct { uint64_t word[4]; } period_u256_t;

static period_uwide_t period_magnitude(period_wide_t value)
{
    return value < 0 ? (period_uwide_t)(-(value + 1)) + 1
                     : (period_uwide_t)value;
}

static period_u256_t period_u256_from(period_uwide_t value)
{
    period_u256_t out = {{(uint64_t)value, (uint64_t)(value >> 64), 0, 0}};
    return out;
}

static int period_u256_compare(period_u256_t a, period_u256_t b)
{
    for (int i = 3; i >= 0; --i)
        if (a.word[i] != b.word[i]) return a.word[i] > b.word[i] ? 1 : -1;
    return 0;
}

static period_u256_t period_u256_subtract(period_u256_t a, period_u256_t b)
{
    uint64_t borrow = 0;
    for (int i = 0; i < 4; ++i) {
        uint64_t first = a.word[i] - b.word[i];
        uint64_t next = (a.word[i] < b.word[i]) | (first < borrow);
        a.word[i] = first - borrow;
        borrow = next;
    }
    return a;
}

static period_u256_t period_u256_product(period_uwide_t a, period_uwide_t b)
{
    uint64_t left[2] = {(uint64_t)a, (uint64_t)(a >> 64)};
    uint64_t right[2] = {(uint64_t)b, (uint64_t)(b >> 64)};
    period_u256_t out = {{0, 0, 0, 0}};
    for (int i = 0; i < 2; ++i) {
        period_uwide_t carry = 0;
        for (int j = 0; j < 2; ++j) {
            period_uwide_t product = (period_uwide_t)left[i] * right[j]
                                     + out.word[i + j] + carry;
            out.word[i + j] = (uint64_t)product;
            carry = product >> 64;
        }
        out.word[i + 2] = (uint64_t)carry;
    }
    return out;
}

static int period_u256_increment(period_u256_t *value)
{
    for (int i = 0; i < 4; ++i)
        if (++value->word[i] != 0) return 1;
    return 0;
}

/* Dividing a product by the fixed scale is common and has a four-limb fast
 * path. The returned magnitude uses the same half-away-from-zero rule as the
 * public helpers; sign is applied only after rounding the magnitude. */
static period_u256_t period_u256_divide_scale(period_u256_t value)
{
    uint64_t remainder = 0;
    for (int i = 3; i >= 0; --i) {
        period_uwide_t partial = ((period_uwide_t)remainder << 64) | value.word[i];
        value.word[i] = (uint64_t)(partial / LAPLACE_FP_ONE);
        remainder = (uint64_t)(partial % LAPLACE_FP_ONE);
    }
    if (remainder >= LAPLACE_FP_HALF)
        (void)period_u256_increment(&value); /* quotient has 29 bits of headroom */
    return value;
}

static int period_u256_scale(period_u256_t *value, uint64_t factor)
{
    period_uwide_t carry = 0;
    for (int i = 0; i < 4; ++i) {
        period_uwide_t product = (period_uwide_t)value->word[i] * factor + carry;
        value->word[i] = (uint64_t)product;
        carry = product >> 64;
    }
    return carry == 0;
}

static period_wide_t period_signed_magnitude(period_u256_t value,
                                              int negative, int *ok)
{
    const period_uwide_t sign_bit = (period_uwide_t)1 << 127;
    if (value.word[2] != 0 || value.word[3] != 0) { *ok = 0; return 0; }
    period_uwide_t magnitude = ((period_uwide_t)value.word[1] << 64) | value.word[0];
    if (magnitude > sign_bit - (negative ? 0 : 1)) { *ok = 0; return 0; }
    if (!negative) return (period_wide_t)magnitude;
    if (magnitude == 0) return 0;
    return -(period_wide_t)(magnitude - 1) - 1;
}

static period_wide_t period_ratio(period_u256_t numerator,
                                  period_u256_t denominator,
                                  int negative, int *ok)
{
    if (!*ok) return 0;
    if ((denominator.word[0] | denominator.word[1] |
         denominator.word[2] | denominator.word[3]) == 0) {
        *ok = 0; return 0;
    }
    period_u256_t quotient = {{0, 0, 0, 0}};
    period_u256_t remainder = {{0, 0, 0, 0}};
    if ((numerator.word[2] | numerator.word[3] |
         denominator.word[2] | denominator.word[3]) == 0) {
        period_uwide_t n = ((period_uwide_t)numerator.word[1] << 64) | numerator.word[0];
        period_uwide_t d = ((period_uwide_t)denominator.word[1] << 64) | denominator.word[0];
        quotient = period_u256_from(n / d);
        remainder = period_u256_from(n % d);
    } else {
        for (int bit = 255; bit >= 0; --bit) {
            uint64_t carry = (numerator.word[bit / 64] >> (bit % 64)) & 1;
            for (int i = 0; i < 4; ++i) {
                uint64_t next = remainder.word[i] >> 63;
                remainder.word[i] = (remainder.word[i] << 1) | carry;
                carry = next;
            }
            /* carry is the 257th remainder bit. Subtraction modulo 2^256
             * retains the exact low limbs and the result is again < divisor. */
            if (carry || period_u256_compare(remainder, denominator) >= 0) {
                remainder = period_u256_subtract(remainder, denominator);
                quotient.word[bit / 64] |= UINT64_C(1) << (bit % 64);
            }
        }
    }
    /* remainder >= denominator/2, including an exact tie, without doubling. */
    if (period_u256_compare(remainder,
            period_u256_subtract(denominator, remainder)) >= 0 &&
        !period_u256_increment(&quotient)) {
        *ok = 0; return 0;
    }
    return period_signed_magnitude(quotient, negative, ok);
}

static period_wide_t period_product_ratio(period_wide_t a, period_wide_t b,
                                          period_wide_t denominator, int *ok)
{
    if (!*ok || denominator == 0) { *ok = 0; return 0; }
    int negative = (a < 0) ^ (b < 0) ^ (denominator < 0);
    return period_ratio(period_u256_product(period_magnitude(a), period_magnitude(b)),
                        period_u256_from(period_magnitude(denominator)), negative, ok);
}

static period_wide_t period_add(period_wide_t a, period_wide_t b, int *ok)
{
    period_wide_t out = 0;
    if (!*ok || __builtin_add_overflow(a, b, &out)) { *ok = 0; return 0; }
    return out;
}

static period_wide_t period_sub(period_wide_t a, period_wide_t b, int *ok)
{
    period_wide_t out = 0;
    if (!*ok || __builtin_sub_overflow(a, b, &out)) { *ok = 0; return 0; }
    return out;
}

static period_wide_t period_mul(period_wide_t a, period_wide_t b, int *ok)
{
    return period_product_ratio(a, b, LAPLACE_FP_ONE, ok);
}

static period_wide_t period_div(period_wide_t a, period_wide_t b, int *ok)
{
    return period_product_ratio(a, LAPLACE_FP_ONE, b, ok);
}

static period_wide_t period_exp(period_wide_t x, int *ok)
{
    if (!*ok) return 0;
    if (x <= -60LL * LAPLACE_FP_ONE) return 1;
    /* exp(90) exceeds the private int128 Q1e9 capacity. Check before any
     * range-reduction arithmetic, then check the actual shift below. */
    if (x >= 90LL * LAPLACE_FP_ONE) { *ok = 0; return 0; }
    int64_t value = (int64_t)x;
    int64_t k = value >= 0
        ? (value + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2
        : -((-value + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2);
    int64_t r = value - k * LAPLACE_FP_LN2;
    int64_t result = LAPLACE_FP_ONE, term = LAPLACE_FP_ONE;
    for (int n = 1; n <= 14; ++n) {
        term = laplace_fp_mul(term, r) / n;
        result += term; /* reduced |r| <= ln(2)/2 keeps this below 2e9 */
        if (term > -10 && term < 10) break;
    }
    if (result <= 0) return 1;
    period_uwide_t out = (period_uwide_t)result;
    if (k > 0) {
        const period_uwide_t maximum = ((period_uwide_t)1 << 127) - 1;
        if (k >= 127 || out > (maximum >> k)) { *ok = 0; return 0; }
        out <<= k;
    } else if (k < 0) {
        int shift = (int)-k;
        if (shift >= 127) return 1;
        out = (out + ((period_uwide_t)1 << (shift - 1))) >> shift;
    }
    return out == 0 ? 1 : (period_wide_t)out;
}

static period_wide_t period_log(period_wide_t x, int *ok)
{
    if (!*ok || x <= 0) { *ok = 0; return 0; }
    int k = 0;
    while (x >= 2 * LAPLACE_FP_ONE) { x >>= 1; ++k; }
    while (x < LAPLACE_FP_ONE) { x <<= 1; --k; }
    return (period_wide_t)k * LAPLACE_FP_LN2 + laplace_fp_log((int64_t)x);
}

static period_wide_t period_sqrt(period_wide_t x, int *ok)
{
    period_wide_t scaled;
    if (!*ok || x <= 0 ||
        __builtin_mul_overflow(x, (period_wide_t)LAPLACE_FP_ONE, &scaled)) {
        *ok = 0; return 0;
    }
    return (period_wide_t)isqrt_u128(scaled);
}

static period_wide_t period_illinois_f(period_wide_t x,
                                       period_wide_t delta_sq,
                                       period_wide_t phi_sq,
                                       period_wide_t v,
                                       period_wide_t a,
                                       period_wide_t tau_sq, int *ok)
{
    period_wide_t ex = period_exp(x, ok);
    period_wide_t inner_den = period_add(period_add(phi_sq, v, ok), ex, ok);
    period_wide_t inner_num = period_sub(delta_sq, inner_den, ok);
    if (!*ok) return 0;

    /* Preserve both fixed-point product rounding steps before the ratio.
     * Neither squared denominator nor scaled numerator must fit int128. */
    period_u256_t numerator = period_u256_divide_scale(
        period_u256_product(period_magnitude(ex), period_magnitude(inner_num)));
    period_u256_t denominator = period_u256_divide_scale(
        period_u256_product(period_magnitude(inner_den), period_magnitude(inner_den)));
    if (!period_u256_scale(&numerator, LAPLACE_FP_ONE) ||
        !period_u256_scale(&denominator, 2)) {
        *ok = 0; return 0;
    }
    period_wide_t lhs = period_ratio(numerator, denominator, inner_num < 0, ok);
    period_wide_t rhs = period_div(period_sub(x, a, ok), tau_sq, ok);
    return period_sub(lhs, rhs, ok);
}

static int glicko2_finish_period(glicko2_state_t* st,
                                 int64_t mu, int64_t phi, int64_t phi_sq,
                                 int64_t sigma, int64_t v_inv,
                                 int64_t delta_inner, size_t n,
                                 int64_t tau, int64_t now_ns,
                                 glicko2_trace_t* trace)
{
    if (!st || sigma <= 0 || tau <= 0 ||
        n > (size_t)INT64_MAX || st->observation_count < 0 ||
        st->observation_count > INT64_MAX - (int64_t)n)
        return -1;

    int ok = 1;
    if (v_inv <= 0) v_inv = 1;
    period_wide_t v = period_div(LAPLACE_FP_ONE, v_inv, &ok);
    if (v <= 0) v = 1;
    period_wide_t delta = period_mul(v, delta_inner, &ok);
    period_wide_t delta_sq = period_mul(delta, delta, &ok);
    period_wide_t sigma_sq = period_mul(sigma, sigma, &ok);
    period_wide_t tau_sq = period_mul(tau, tau, &ok);
    if (!ok || sigma_sq <= 0 || tau_sq <= 0) return -1;
    period_wide_t a = period_log(sigma_sq, &ok);

    period_wide_t A = a, B;
    period_wide_t phi_v = period_add(phi_sq, v, &ok);
    if (delta_sq > phi_v) {
        B = period_log(period_sub(delta_sq, phi_v, &ok), &ok);
    } else {
        int k = 1;
        for (;;) {
            period_wide_t step = period_product_ratio(k, tau, 1, &ok);
            B = period_sub(a, step, &ok);
            period_wide_t fB = period_illinois_f(
                B, delta_sq, phi_sq, v, a, tau_sq, &ok);
            if (!ok) return -1;
            if (fB >= 0) break;
            if (++k > 100) return -1;
        }
    }
    if (!ok) return -1;

    period_wide_t fA = period_illinois_f(A, delta_sq, phi_sq, v, a, tau_sq, &ok);
    period_wide_t fB = period_illinois_f(B, delta_sq, phi_sq, v, a, tau_sq, &ok);
    if (!ok) return -1;

    int iter_count = 0;
    for (; iter_count < 100; ++iter_count) {
        period_wide_t diff = period_sub(B, A, &ok);
        if (!ok) return -1;
        if (period_magnitude(diff) <= LAPLACE_GLICKO2_ILLINOIS_EPS) break;
        period_wide_t f_span = period_sub(fA, fB, &ok);
        if (!ok || f_span == 0) return -1;

        /* Keep the secant product wide and round only the final ratio. */
        period_wide_t correction = period_product_ratio(diff, fA, f_span, &ok);
        period_wide_t C = period_add(A, correction, &ok);
        period_wide_t fC = period_illinois_f(
            C, delta_sq, phi_sq, v, a, tau_sq, &ok);
        if (!ok) return -1;
        const int crosses_zero = fC == 0 || fB == 0 || ((fC < 0) != (fB < 0));
        if (crosses_zero) {
            A = B;
            fA = fB;
        } else {
            fA /= 2;
        }
        B = C;
        fB = fC;
    }
    if (iter_count == 100) return -1;

    period_wide_t sigma_new = period_exp(A / 2, &ok);
    if (!ok || sigma_new <= 0 || sigma_new > INT64_MAX) return -1;

    period_wide_t sigma_new_sq = period_mul(sigma_new, sigma_new, &ok);
    period_wide_t phi_star_sq = period_add(phi_sq, sigma_new_sq, &ok);
    if (!ok || sigma_new_sq <= 0 || phi_star_sq <= 0) return -1;
    period_wide_t phi_star = period_sqrt(phi_star_sq, &ok);
    if (!ok || phi_star <= 0) return -1;

    period_wide_t inv_phi_star_sq = period_div(LAPLACE_FP_ONE, phi_star_sq, &ok);
    period_wide_t inv_v = period_div(LAPLACE_FP_ONE, v, &ok);
    period_wide_t precision = period_add(inv_phi_star_sq, inv_v, &ok);
    if (!ok || precision <= 0) return -1;
    period_wide_t phi_new_sq = period_div(LAPLACE_FP_ONE, precision, &ok);
    if (!ok || phi_new_sq <= 0) return -1;
    period_wide_t phi_new = period_sqrt(phi_new_sq, &ok);
    if (!ok || phi_new <= 0) return -1;

    period_wide_t mu_new = period_add(mu, period_mul(phi_new_sq, delta_inner, &ok), &ok);
    period_wide_t r_new = period_add(
        period_mul(mu_new, LAPLACE_FP_RATING_SCALE, &ok),
        LAPLACE_FP_BASE_RATING, &ok);
    period_wide_t rd_new = period_mul(phi_new, LAPLACE_FP_RATING_SCALE, &ok);
    if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
    if (!ok || r_new > INT64_MAX || r_new < INT64_MIN || rd_new <= 0) return -1;

    glicko2_state_t next = *st;
    next.rating = (int64_t)r_new;
    next.rd = (int64_t)rd_new;
    next.volatility = (int64_t)sigma_new;
    next.last_observed_at_unix_ns = now_ns;
    next.observation_count += (int64_t)n;
    if (!glicko2_state_is_admissible(&next)) return -1;

    if (trace) {
        trace->mu             = mu;
        trace->phi            = phi;
        trace->v              = clamp_i128(v);
        /* The legacy diagnostic fields remain int64. Only this optional
         * projection saturates; no projected value feeds the state update. */
        trace->delta          = clamp_i128(delta);
        trace->a_value        = clamp_i128(a);
        trace->sigma_new      = next.volatility;
        trace->phi_star       = clamp_i128(phi_star);
        trace->phi_new        = clamp_i128(phi_new);
        trace->mu_new         = clamp_i128(mu_new);
        trace->r_new          = next.rating;
        trace->rd_new         = next.rd;
        trace->illinois_iters = iter_count;
    }
    *st = next;
    return 0;
}

static void glicko2_update_period_impl(glicko2_state_t* st,
                                       const glicko2_observation_t* obs,
                                       size_t n,
                                       int64_t tau,
                                       int64_t now_ns,
                                       glicko2_trace_t* trace)
{
    if (!glicko2_state_is_admissible(st) || tau <= 0) return;

    if (n == 0 || !obs) {
        int64_t phi = g1_to_phi(st->rd);
        int64_t sigma = st->volatility;
        int64_t phi_sq = laplace_fp_mul(phi, phi);
        int64_t sig_sq = laplace_fp_mul(sigma, sigma);
        int64_t phi_new_sq = sat_add_i64(phi_sq, sig_sq);
        int64_t phi_new = laplace_fp_sqrt(phi_new_sq);
        int64_t rd_new = phi_to_g1(phi_new);
        if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
        if (rd_new <= 0) return;
        if (trace) {
            trace->mu             = g1_to_mu(st->rating);
            trace->phi            = phi;
            trace->v              = 0;
            trace->delta          = 0;
            trace->a_value        = laplace_fp_log(laplace_fp_mul(sigma, sigma));
            trace->sigma_new      = sigma;
            trace->phi_star       = phi_new;
            trace->phi_new        = phi_new;
            trace->mu_new         = trace->mu;
            trace->r_new          = st->rating;
            trace->rd_new         = rd_new;
            trace->illinois_iters = 0;
        }
        st->rd = rd_new;
        st->last_observed_at_unix_ns = now_ns;
        return;
    }

    if (n > (size_t)INT64_MAX ||
        st->observation_count > INT64_MAX - (int64_t)n)
        return;

    int64_t mu = g1_to_mu(st->rating);
    int64_t phi = g1_to_phi(st->rd);
    int64_t sigma = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);

    int64_t v_inv = 0;
    int64_t delta_inner = 0;
    for (size_t i = 0; i < n; ++i) {
        if (obs[i].opponent_rd < 0 || obs[i].score < 0 ||
            obs[i].score > LAPLACE_FP_ONE)
            return;
        int64_t mu_j = g1_to_mu(obs[i].opponent_rating);
        int64_t phi_j = g1_to_phi(obs[i].opponent_rd);
        int64_t g_j = laplace_glicko2_g(phi_j);
        int64_t E_j = laplace_glicko2_E(mu, mu_j, g_j);
        int64_t g_sq = laplace_fp_mul(g_j, g_j);
        int64_t E_1mE = laplace_fp_mul(E_j, sat_sub_i64(LAPLACE_FP_ONE, E_j));
        v_inv = sat_add_i64(v_inv, laplace_fp_mul(g_sq, E_1mE));
        delta_inner = sat_add_i64(
            delta_inner,
            laplace_fp_mul(g_j, sat_sub_i64(obs[i].score, E_j)));
    }

    glicko2_state_t next = *st;
    if (glicko2_finish_period(&next, mu, phi, phi_sq, sigma,
                              v_inv, delta_inner, n, tau, now_ns,
                              trace) == 0)
        *st = next;
}

void glicko2_update_period(glicko2_state_t* st,
                           const glicko2_observation_t* obs,
                           size_t n,
                           int64_t tau,
                           int64_t now_ns)
{
    glicko2_update_period_impl(st, obs, n, tau, now_ns, NULL);
}

int glicko2_fold_grouped_period(glicko2_state_t* st,
                                const int64_t* opponent_ratings,
                                const int64_t* opponent_phis,
                                const int64_t* games,
                                const int64_t* score_sums,
                                size_t group_count,
                                int64_t tau,
                                int64_t now_ns)
{
    if (!glicko2_state_is_admissible(st) || !opponent_ratings ||
        !opponent_phis || !games || !score_sums || group_count == 0 ||
        tau <= 0)
        return -1;

    int64_t mu = g1_to_mu(st->rating);
    int64_t phi = g1_to_phi(st->rd);
    int64_t sigma = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);

    __int128 v_inv_wide = 0;
    __int128 delta_wide = 0;
    __int128 total_games_wide = 0;

    for (size_t i = 0; i < group_count; ++i) {
        int64_t n = games[i];
        if (n <= 0 || opponent_phis[i] < 0) return -1;
        __int128 maximum_score = (__int128)n * (__int128)LAPLACE_FP_ONE;
        if (score_sums[i] < 0 || (__int128)score_sums[i] > maximum_score)
            return -1;

        int64_t mu_j = g1_to_mu(opponent_ratings[i]);
        int64_t phi_j = g1_to_phi(opponent_phis[i]);
        int64_t g_j = laplace_glicko2_g(phi_j);
        int64_t E_j = laplace_glicko2_E(mu, mu_j, g_j);
        int64_t g_sq = laplace_fp_mul(g_j, g_j);
        int64_t E_1mE = laplace_fp_mul(
            E_j, sat_sub_i64(LAPLACE_FP_ONE, E_j));

        v_inv_wide += (__int128)n * laplace_fp_mul(g_sq, E_1mE);

        /* Score enters linearly, but fixed-point multiplication rounds per
         * observation. q/rem reproduces exactly the observation sequence
         * represented by an aggregate without allocating O(games) records. */
        int64_t q = score_sums[i] / n;
        __int128 rem_wide = (__int128)score_sums[i] - (__int128)q * (n - 1);
        if (rem_wide > INT64_MAX || rem_wide < INT64_MIN)
            return -1;
        int64_t rem = (int64_t)rem_wide;
        delta_wide += (__int128)(n - 1) *
                          laplace_fp_mul(g_j, sat_sub_i64(q, E_j))
                    + laplace_fp_mul(g_j, sat_sub_i64(rem, E_j));
        total_games_wide += n;
    }

    if (v_inv_wide > INT64_MAX || v_inv_wide < INT64_MIN ||
        delta_wide > INT64_MAX || delta_wide < INT64_MIN ||
        total_games_wide > INT64_MAX || total_games_wide <= 0 ||
        st->observation_count > INT64_MAX - (int64_t)total_games_wide)
        return -1;

    glicko2_state_t next = *st;
    if (glicko2_finish_period(
            &next, mu, phi, phi_sq, sigma,
            (int64_t)v_inv_wide, (int64_t)delta_wide,
            (size_t)total_games_wide, tau, now_ns, NULL) != 0)
        return -1;
    if (!glicko2_state_is_admissible(&next)) return -1;
    *st = next;
    return 0;
}

int glicko2_fold_uniform_period(glicko2_state_t* st,
                                int64_t opponent_rating,
                                int64_t opponent_phi,
                                int64_t games,
                                int64_t sum_score,
                                int64_t tau,
                                int64_t now_ns)
{
    return glicko2_fold_grouped_period(st, &opponent_rating, &opponent_phi,
                                       &games, &sum_score, 1, tau, now_ns);
}

void laplace_glicko2_update_period_traced(glicko2_state_t* st,
                                          const glicko2_observation_t* obs,
                                          size_t n,
                                          int64_t tau,
                                          int64_t now_ns,
                                          glicko2_trace_t* trace)
{
    glicko2_update_period_impl(st, obs, n, tau, now_ns, trace);
}

void glicko2_update(glicko2_state_t* st,
                    int64_t score,
                    int64_t source_credibility,
                    int64_t now_ns)
{
    if (!st) return;
    glicko2_observation_t obs = {
        .opponent_rating = source_credibility,
        .opponent_rd     = 30LL * LAPLACE_FP_ONE,
        .score           = score,
    };
    glicko2_update_period(st, &obs, 1, LAPLACE_GLICKO2_DEFAULT_TAU, now_ns);
}

void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns)
{
    if (!glicko2_state_is_admissible(st)) return;
    if (st->last_observed_at_unix_ns <= 0) {
        st->last_observed_at_unix_ns = now_ns;
        return;
    }
    if (now_ns <= st->last_observed_at_unix_ns) return;
    int64_t elapsed_ns = clamp_i128(
        (__int128)now_ns - (__int128)st->last_observed_at_unix_ns);

    int64_t periods_fp = laplace_fp_div(
        elapsed_ns, LAPLACE_GLICKO2_RATING_PERIOD_NS);
    int64_t phi = g1_to_phi(st->rd);
    int64_t sigma = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t sig_sq = laplace_fp_mul(sigma, sigma);
    int64_t bump = laplace_fp_mul(sig_sq, periods_fp);
    int64_t phi_new_sq = sat_add_i64(phi_sq, bump);
    int64_t phi_new = laplace_fp_sqrt(phi_new_sq);
    int64_t rd_new = phi_to_g1(phi_new);
    if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
    if (rd_new <= 0) return;
    st->rd = rd_new;
    st->last_observed_at_unix_ns = now_ns;
}

int64_t laplace_glicko2_neutral_mu_fp(void)
{
    return LAPLACE_GLICKO2_NEUTRAL_MU_FP;
}

int64_t laplace_effective_mu_fp(int64_t rating, int64_t rd)
{
    return clamp_i128((__int128)rating - 2 * (__int128)rd);
}

int64_t glicko2_effective_mu(const glicko2_state_t* st)
{
    if (!st) return 0;
    return laplace_effective_mu_fp(st->rating, st->rd);
}

int64_t laplace_glicko2_expected_score_fp(int64_t rating, int64_t rd)
{
    int64_t mu = g1_to_mu(rating);
    int64_t neutral_mu = g1_to_mu(LAPLACE_GLICKO2_NEUTRAL_MU_FP);
    int64_t phi = g1_to_phi(rd);
    int64_t confidence = laplace_glicko2_g(phi);
    return laplace_glicko2_E(mu, neutral_mu, confidence);
}

double laplace_glicko2_expected_score(int64_t rating, int64_t rd)
{
    return (double)laplace_glicko2_expected_score_fp(rating, rd)
           / LAPLACE_GLICKO2_FP_SCALE_D;
}

double laplace_walk_edge_weight(int64_t rating, int64_t rd)
{
    return 2.0 * laplace_glicko2_expected_score(rating, rd) - 1.0;
}
