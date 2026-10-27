#include <cstring>
#include "target_ops.hpp"
#include <limits>
#include "laplace/dynamics/eigenmaps.h"
#include <cstdint>
#include <climits>
#include "laplace/synthesis/lookup_compile.h"
#include "laplace/synthesis/basis.h"
#include <algorithm>
#include <cmath>
#include <map>
#include <set>
#include <utility>
#include <vector>
#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
#include <mkl_lapacke.h>
#endif

/* R is vocab x width, rows are representations. The score transform and the
 * value transform are different equations:
 *   R S R^T ~= A, S=R^+ A (R^+)^T
 *   R V     ~= A R, V=R^+ A R
 * A Gram contraction R^T A R solves neither equation in a general basis. */
static int operator_construct(const double *r,size_t n,size_t d,
    const int *rows,const int *cols,const double *weights,size_t nnz,
    double *score,double *value,size_t *rank,double *score_error,double *value_error,double *constant)
{
#ifndef LAPLACE_HAS_MKL
    return -2;
#else
    if(!r||!score||!value||!rank||!score_error||!value_error||!n||!d) return -1;
    if(n>INT32_MAX||d>INT32_MAX||n>SIZE_MAX/d/sizeof(double)||d>SIZE_MAX/d/sizeof(double)||
       (nnz&&(!rows||!cols||!weights))) return -1;
    size_t k=std::min(n,d);
    std::vector<double> a(r,r+n*d),u(n*k),s(k),vt(k*d),pinv(d*n,0);
    for(double v:a) if(!std::isfinite(v)) return -1;
    int rc=LAPACKE_dgesdd(LAPACK_ROW_MAJOR,'S',(lapack_int)n,(lapack_int)d,
        a.data(),(lapack_int)d,s.data(),u.data(),(lapack_int)k,vt.data(),(lapack_int)d);
    if(rc) return rc;
    // The consumer matrices are F32. Directions below its matrix-size error
    // bound must not be inverted at double precision and then overflow on export.
    double cutoff=(s.empty()?0:s[0])*std::max(n,d)*std::numeric_limits<float>::epsilon();
    *rank=0;
    auto scaled=vt;
    for(size_t z=0;z<k;z++) {
        double gain=s[z]>cutoff?1/s[z]:0;
        if(gain>0) ++*rank;
        for(size_t c=0;c<d;c++) scaled[z*d+c]*=gain;
    }
    cblas_dgemm(CblasRowMajor,CblasTrans,CblasTrans,(MKL_INT)d,(MKL_INT)n,(MKL_INT)k,
        1,scaled.data(),(MKL_INT)d,u.data(),(MKL_INT)k,0,pinv.data(),(MKL_INT)n);
    if(constant){std::fill_n(constant,d,0.0);for(size_t c=0;c<d;c++)for(size_t x=0;x<n;x++)constant[c]+=pinv[c*n+x];}
    std::map<std::pair<int,int>,double> edges;
    for(size_t e=0;e<nnz;e++) {
        if(rows[e]<0||cols[e]<0||(size_t)rows[e]>=n||(size_t)cols[e]>=n||!std::isfinite(weights[e])) return -1;
        edges[{rows[e],cols[e]}]+=weights[e];
    }
    std::vector<double> ar(n*d,0),ax(n*d,0),au(n*k,0);
    double anorm=0;
    for(const auto &edge:edges) {
        size_t x=edge.first.first,y=edge.first.second; double w=edge.second;
        anorm+=w*w;
        for(size_t c=0;c<d;c++) {ar[x*d+c]+=w*r[y*d+c];ax[x*d+c]+=w*pinv[c*n+y];}
        for(size_t z=0;z<k;z++) if(s[z]>cutoff) au[x*k+z]+=w*u[y*k+z];
    }
    cblas_dgemm(CblasRowMajor,CblasNoTrans,CblasNoTrans,(MKL_INT)d,(MKL_INT)d,(MKL_INT)n,
        1,pinv.data(),(MKL_INT)n,ax.data(),(MKL_INT)d,0,score,(MKL_INT)d);
    cblas_dgemm(CblasRowMajor,CblasNoTrans,CblasNoTrans,(MKL_INT)d,(MKL_INT)d,(MKL_INT)n,
        1,pinv.data(),(MKL_INT)n,ar.data(),(MKL_INT)d,0,value,(MKL_INT)d);
    std::vector<double> projected(k*k,0),projected_value(k*d,0);
    cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)k,(MKL_INT)k,(MKL_INT)n,
        1,u.data(),(MKL_INT)k,au.data(),(MKL_INT)k,0,projected.data(),(MKL_INT)k);
    cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)k,(MKL_INT)d,(MKL_INT)n,
        1,u.data(),(MKL_INT)k,ar.data(),(MKL_INT)d,0,projected_value.data(),(MKL_INT)d);
    double kept=0,total_value=0,kept_value=0;
    for(size_t i=0;i<k;i++) if(s[i]>cutoff) {
        for(size_t j=0;j<k;j++) if(s[j]>cutoff) kept+=projected[i*k+j]*projected[i*k+j];
        for(size_t c=0;c<d;c++) kept_value+=projected_value[i*d+c]*projected_value[i*d+c];
    }
    for(double v:ar) total_value+=v*v;
    *score_error=anorm>0?std::sqrt(std::max(0.0,anorm-kept)/anorm):0;
    *value_error=total_value>0?std::sqrt(std::max(0.0,total_value-kept_value)/total_value):0;
    return 0;
#endif
}

extern "C" int laplace_operator_compile(const double*r,size_t n,size_t d,const int*rows,const int*cols,const double*weights,size_t nnz,double*score,double*value,size_t*rank,double*se,double*ve){
 return operator_construct(r,n,d,rows,cols,weights,nnz,score,value,rank,se,ve,nullptr);
}
extern "C" int laplace_operator_compile_constant(const double*r,size_t n,size_t d,const int*rows,const int*cols,const double*weights,size_t nnz,double*score,double*value,size_t*rank,double*se,double*ve,double*constant){
 return operator_construct(r,n,d,rows,cols,weights,nnz,score,value,rank,se,ve,constant);
}
extern "C" int laplace_operator_apply(const double *r,size_t n,size_t d,const double *value,double scale,double *accum)
{
#ifndef LAPLACE_HAS_MKL
    return -2;
#else
    if(!r||!value||!accum||!n||!d||n>INT32_MAX||d>INT32_MAX||!std::isfinite(scale)) return -1;
    cblas_dgemm(CblasRowMajor,CblasNoTrans,CblasNoTrans,(MKL_INT)n,(MKL_INT)d,(MKL_INT)d,
        scale,r,(MKL_INT)d,value,(MKL_INT)d,1,accum,(MKL_INT)d);
    return 0;
#endif
}
static int readout_construct(const float *r,size_t n,size_t d,
    const int *input,const int *emit,const int *rows,const int *cols,const double *weights,size_t nnz,
    float *head,size_t *rank,double *relative_error,double *top1_accuracy,bool solve)
{
#ifndef LAPLACE_HAS_MKL
    return -2;
#else
    try {
        auto targets=laplace::synthesis::transfer_targets(n,2,input,emit,rows,cols,weights,nnz);
        return laplace::synthesis::fit_targets(r,n,n,d,targets,head,rank,relative_error,top1_accuracy,solve);
    }catch(...){return -1;}
#endif
}

extern "C" int laplace_readout_fit(const float*r,size_t n,size_t d,const int*input,const int*emit,const int*rows,const int*cols,const double*weights,size_t nnz,float*head,size_t*rank,double*error,double*accuracy){
    return readout_construct(r,n,d,input,emit,rows,cols,weights,nnz,head,rank,error,accuracy,true);
}
extern "C" int laplace_readout_measure(const float*r,size_t n,size_t d,const int*input,const int*emit,const int*rows,const int*cols,const double*weights,size_t nnz,float*head,size_t*rank,double*error,double*accuracy){
    return readout_construct(r,n,d,input,emit,rows,cols,weights,nnz,head,rank,error,accuracy,false);
}

extern "C" int laplace_graph_basis(const int *rows,const int *cols,const double *weights,size_t nnz,
    size_t n,size_t requested,double *output,size_t *rank)
{
    if(!rows||!cols||!weights||!output||!rank||!n||!requested||n>INT32_MAX||n>SIZE_MAX/requested/sizeof(double)) return -1;
    std::vector<int> map(n,-1),ids,rr,cc;
    std::vector<double> ww;
    for(size_t e=0;e<nnz;e++) {
        if(rows[e]<0||cols[e]<0||(size_t)rows[e]>=n||(size_t)cols[e]>=n||!std::isfinite(weights[e])) return -1;
        if(weights[e]>0) map[rows[e]]=map[cols[e]]=0;
    }
    for(size_t x=0;x<n;x++) if(map[x]==0) {map[x]=(int)ids.size();ids.push_back((int)x);}
    if(ids.size()<3) return -2;
    *rank=std::min(requested,ids.size()-2);
    for(size_t e=0;e<nnz;e++) if(weights[e]>0) {rr.push_back(map[rows[e]]);cc.push_back(map[cols[e]]);ww.push_back(weights[e]);}
    std::vector<double> active(ids.size()* *rank);
    int rc=laplacian_eigenmaps_from_sparse_graph(rr.data(),cc.data(),ww.data(),ww.size(),ids.size(),*rank,active.data());
    if(rc) return rc;
    std::fill(output,output+n*requested,0);
    for(size_t x=0;x<ids.size();x++) std::copy_n(active.data()+x* *rank,*rank,output+(size_t)ids[x]*requested);
    return 0;
}

/* A finite selected-operator representation: distinct emitted target columns,
 * one common unobserved-output column, exact input realization, S3 coordinates,
 * a norm slack coordinate and bias. This is not a vocabulary identity matrix.
 * The selected output table lies in this representation's column space. */
static int readout_embed_construct(size_t n,size_t d,const int *input,const int *emit,
    const int *rows,const int *cols,const double *weights,size_t nnz,const double *coordinates,
    double *embedding,size_t *features,double bound,bool equal_candidates)try
{
    if(!n||!d||!embedding||!features||!coordinates||n>INT_MAX||d>INT_MAX||n>SIZE_MAX/d/sizeof(double))return -1;
    if(d<6){*features=6;return -4;}
    auto targets=laplace::synthesis::transfer_targets(n,2,input,emit,rows,cols,weights,nnz,bound,equal_candidates);
    std::set<int> used;for(const auto&row:targets)for(const auto&e:row.delta)used.insert(e.first);
    size_t f=used.size()+5;std::fill(embedding,embedding+n*d,0);size_t rank=0;
    if(f+2>d){
        // Capacity is the selected width. A sparse graph projection replaces an
        // unbounded vocabulary-square table; the complete transfer still enters
        // the final output solve and its error is measured over every slot.
        return laplace_spectral_embed(n,d,input,coordinates,rows,cols,weights,nnz,embedding,features,nullptr);
    }else {
#ifndef LAPLACE_HAS_MKL
        return -2;
#else
        size_t k=std::min(n,f);std::vector<double> a(n*f),u(n*k),singular(k),vt(k*f);
        for(size_t x=0;x<n;x++){
            size_t c=0;for(int y:used){auto it=targets[x].delta.find(y);a[x*f+c++]=targets[x].floor+(it==targets[x].delta.end()?0:it->second);}
            a[x*f+c++]=targets[x].floor;
            for(size_t j=0;j<4;j++)a[x*f+c++]=coordinates[(size_t)input[x]*4+j];
        }
        for(double value:a)if(!std::isfinite(value))return -1;
        int rc=LAPACKE_dgesdd(LAPACK_ROW_MAJOR,'S',(lapack_int)n,(lapack_int)f,a.data(),(lapack_int)f,singular.data(),u.data(),(lapack_int)k,vt.data(),(lapack_int)f);if(rc)return rc;
        const double cutoff=singular[0]*std::max(n,f)*std::numeric_limits<double>::epsilon();while(rank<k&&singular[rank]>cutoff)++rank;
        for(size_t x=0;x<n;x++)std::copy_n(u.data()+x*k,rank,embedding+x*d);
#endif
    }
    *features=rank+2;double max_norm=0;
    for(size_t x=0;x<n;x++){double norm=0;for(size_t c=0;c<rank;c++){double v=embedding[x*d+c];if(!std::isfinite(v))return -1;norm+=v*v;}max_norm=std::max(max_norm,norm);}
    double common=max_norm+std::numeric_limits<double>::epsilon(),scale=std::sqrt((d-1)/common);
    for(size_t x=0;x<n;x++){
        double norm=0;for(size_t c=0;c<rank;c++){double v=embedding[x*d+c];norm+=v*v;embedding[x*d+c]*=scale;}
        embedding[x*d+d-2]=std::sqrt(std::max(0.0,common-norm))*scale;embedding[x*d+d-1]=1;
    }
    for(size_t x=0;x<n;x++)if((size_t)input[x]!=x)std::copy_n(embedding+(size_t)input[x]*d,d,embedding+x*d);
    return 0;
}catch(...){return -1;}
extern "C" int laplace_readout_embed(size_t n,size_t d,const int*input,const int*emit,const int*rows,const int*cols,const double*weights,size_t nnz,const double*coordinates,double*embedding,size_t*features){
 return readout_embed_construct(n,d,input,emit,rows,cols,weights,nnz,coordinates,embedding,features,0,false);
}
extern "C" int laplace_readout_embed_policy(size_t n,size_t d,const int*input,const int*emit,const int*rows,const int*cols,const double*weights,size_t nnz,const double*coordinates,double*embedding,size_t*features,double bound,int equal_candidates){
 if(!std::isfinite(bound)||bound<0||bound>=1)return -1;
 return readout_embed_construct(n,d,input,emit,rows,cols,weights,nnz,coordinates,embedding,features,bound,equal_candidates!=0);
}
