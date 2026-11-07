#include "glicko2_internal.h"

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

static period_u256_t period_u256_divide_scale(period_u256_t value)
{
    if ((value.word[2] | value.word[3]) == 0) {
        period_uwide_t magnitude = ((period_uwide_t)value.word[1] << 64) | value.word[0];
        period_uwide_t quotient = magnitude / LAPLACE_FP_ONE;
        if (magnitude % LAPLACE_FP_ONE >= LAPLACE_FP_HALF) ++quotient;
        return period_u256_from(quotient);
    }
    uint64_t remainder = 0;
    for (int i = 3; i >= 0; --i) {
        period_uwide_t partial = ((period_uwide_t)remainder << 64) | value.word[i];
        value.word[i] = (uint64_t)(partial / LAPLACE_FP_ONE);
        remainder = (uint64_t)(partial % LAPLACE_FP_ONE);
    }
    if (remainder >= LAPLACE_FP_HALF)
        (void)period_u256_increment(&value);
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
            if (carry || period_u256_compare(remainder, denominator) >= 0) {
                remainder = period_u256_subtract(remainder, denominator);
                quotient.word[bit / 64] |= UINT64_C(1) << (bit % 64);
            }
        }
    }
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
    period_uwide_t magnitude;
    period_uwide_t den = period_magnitude(denominator);
    if (!__builtin_mul_overflow(period_magnitude(a), period_magnitude(b), &magnitude)) {
        period_uwide_t quotient = magnitude / den;
        period_uwide_t remainder = magnitude % den;
        if (remainder >= den - remainder) ++quotient;
        return period_signed_magnitude(period_u256_from(quotient), negative, ok);
    }
    return period_ratio(period_u256_product(period_magnitude(a), period_magnitude(b)),
                        period_u256_from(den), negative, ok);
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
    if (!*ok) return 0;
    period_wide_t product;
    if (!__builtin_mul_overflow(a, b, &product)) {
        period_uwide_t magnitude = period_magnitude(product);
        period_uwide_t quotient = magnitude / LAPLACE_FP_ONE;
        if (magnitude % LAPLACE_FP_ONE >= LAPLACE_FP_HALF) ++quotient;
        return product < 0 ? -(period_wide_t)quotient : (period_wide_t)quotient;
    }
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
    if (x >= 90LL * LAPLACE_FP_ONE) { *ok = 0; return 0; }
    int64_t value = (int64_t)x;
    int64_t k = value >= 0
        ? (value + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2
        : -((-value + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2);
    int64_t r = value - k * LAPLACE_FP_LN2;
    int64_t result = LAPLACE_FP_ONE, term = LAPLACE_FP_ONE;
    for (int n = 1; n <= 14; ++n) {
        term = laplace_fp_mul(term, r) / n;
        result += term;
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

int laplace_glicko2_finish_period(glicko2_state_t* st,
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
