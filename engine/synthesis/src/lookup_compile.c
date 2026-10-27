#include "laplace/synthesis/lookup_compile.h"
#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

int laplace_lookup_compile(size_t n, size_t d, int eos, double eps,
    const int *input, const int *emit, const int *rows, const int *cols,
    const double *weights, size_t nnz, double *embedding, double *output)
{
    if (!n || d < n || !input || !emit || !embedding || !output ||
        eos < 0 || (size_t)eos >= n || !isfinite(eps) || eps < 0 ||
        (nnz && (!rows || !cols || !weights)) || n > SIZE_MAX / n / sizeof(double) ||
        d > SIZE_MAX / n / sizeof(double)) return -1;
    for (size_t i=0; i<n; ++i)
        if (input[i]<0 || (size_t)input[i]>=n || emit[i]<-1 ||
            (emit[i]>=0 && (size_t)emit[i]>=n)) return -1;
    double *mass = calloc(n*n, sizeof(double));
    if (!mass) return -2;
    for (size_t e=0; e<nnz; ++e) {
        int x=rows[e], y=cols[e];
        if (x<0 || y<0 || (size_t)x>=n || (size_t)y>=n) {free(mass); return -1;}
        if (!isfinite(weights[e]) || weights[e]<0) {free(mass); return -3;}
        x=input[x]; y=input[y];
        if (x==y || emit[y]<0) continue;
        mass[(size_t)x*n+(size_t)emit[y]] += weights[e];
        if (!isfinite(mass[(size_t)x*n+(size_t)emit[y]])) {free(mass); return -3;}
    }
    memset(embedding,0,n*d*sizeof(double));
    memset(output,0,n*d*sizeof(double));
    /* RMSNorm(e_x) = e_x/sqrt(1/d+eps). This cancels it exactly. */
    double cancel = sqrt(1.0/(double)d+eps);
    for (size_t i=0; i<n; ++i) embedding[i*d+(size_t)input[i]]=1;
    for (size_t x=0; x<n; ++x) {
        double sum=0;
        for(size_t y=0; y<n; ++y) sum += mass[x*n+y];
        if (!isfinite(sum)) {free(mass); return -3;}
        if (sum==0) {output[(size_t)eos*d+x]=cancel; continue;}
        /* Unobserved outputs are below every observed log-odds, including a
         * nonpositive observed log-odds. They cannot win an unsupported tie. */
        double floor=0;
        for(size_t y=0; y<n; ++y) if(mass[x*n+y]>0) {
            double v=log(mass[x*n+y]/sum*(double)n);
            if(v<floor) floor=v;
        }
        floor-=1;
        for(size_t y=0; y<n; ++y)
            output[y*d+x]=(mass[x*n+y]>0
                ? log(mass[x*n+y]/sum*(double)n) : floor)*cancel;
    }
    free(mass);
    return 0;
}
