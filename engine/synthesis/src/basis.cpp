#include "laplace/synthesis/basis.h"
#include "laplace/synthesis/lookup_compile.h"
#include "laplace/dynamics/gram_schmidt.h"
#include <algorithm>
#include <cmath>
#include <climits>
#include <cstdint>
#include <limits>
#include <map>
#include <numeric>
#include <set>
#include <vector>
#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
#include <mkl_lapacke.h>
#endif
namespace {
void rms_slack(double*e,size_t n,size_t d,size_t rank,const int*input){
 double maximum=0;for(size_t x=0;x<n;x++)if(input[x]==(int)x){double ss=0;for(size_t c=0;c<rank;c++)ss+=e[x*d+c]*e[x*d+c];maximum=std::max(maximum,ss);}
 double common=maximum+std::numeric_limits<double>::epsilon(),scale=std::sqrt((d-1)/common);
 for(size_t x=0;x<n;x++)if(input[x]==(int)x){double ss=0;for(size_t c=0;c<rank;c++){double v=e[x*d+c];ss+=v*v;e[x*d+c]*=scale;}e[x*d+d-2]=std::sqrt(std::max(0.0,common-ss))*scale;e[x*d+d-1]=1;}
 for(size_t x=0;x<n;x++)if(input[x]!=(int)x)std::copy_n(e+(size_t)input[x]*d,d,e+x*d);
}
}
extern "C" int laplace_basis_refine(double*e,size_t n,size_t d,size_t rank,const double*coordinates,const int*input,laplace_basis_receipt*receipt)try{
#ifndef LAPLACE_HAS_MKL
 return -2;
#else
 if(!e||!coordinates||!input||!receipt||!n||d<3||!rank||rank>d-2||n>INT_MAX||d>INT_MAX||rank>n||n>SIZE_MAX/d/sizeof(double)||rank>SIZE_MAX/rank/sizeof(double))return -1;
 *receipt={};receipt->rank=rank;std::vector<double> vectors(rank*n,0),anchors(n*4,0),q(n*rank);
 for(size_t x=0;x<n;x++){
  if(input[x]<0||(size_t)input[x]>=n||input[input[x]]!=input[x])return -1;
  if(input[x]!=(int)x)continue;bool live=false;
  for(size_t c=0;c<rank;c++){double v=e[x*d+c];if(!std::isfinite(v))return -1;vectors[c*n+x]=v;}
  for(size_t c=0;c<4;c++){double v=coordinates[x*4+c];if(!std::isfinite(v))return -1;anchors[x*4+c]=v;live|=v!=0;}
  if(live)++receipt->anchors;
 }
 int rc=gram_schmidt_orthonormalize(vectors.data(),rank,n);if(rc)return rc;
 for(size_t x=0;x<n;x++)for(size_t c=0;c<rank;c++)q[x*rank+c]=vectors[c*n+x];
 std::vector<double> gram(rank*rank);cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)rank,(MKL_INT)rank,(MKL_INT)n,1,q.data(),(MKL_INT)rank,q.data(),(MKL_INT)rank,0,gram.data(),(MKL_INT)rank);
 for(size_t i=0;i<rank;i++)for(size_t j=0;j<rank;j++)receipt->orthogonality_error=std::max(receipt->orthogonality_error,std::abs(gram[i*rank+j]-(i==j?1:0)));
 // QR changes coordinates, not the space spanned by the feature functions.
 // Check the reconstruction of every canonical row, rather than selected pairs.
 std::vector<double> coefficients(rank*rank),original(n*rank,0),reconstructed(n*rank);
 for(size_t x=0;x<n;x++)if(input[x]==(int)x)std::copy_n(e+x*d,rank,original.data()+x*rank);
 cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)rank,(MKL_INT)rank,(MKL_INT)n,1,q.data(),(MKL_INT)rank,original.data(),(MKL_INT)rank,0,coefficients.data(),(MKL_INT)rank);
 cblas_dgemm(CblasRowMajor,CblasNoTrans,CblasNoTrans,(MKL_INT)n,(MKL_INT)rank,(MKL_INT)rank,1,q.data(),(MKL_INT)rank,coefficients.data(),(MKL_INT)rank,0,reconstructed.data(),(MKL_INT)rank);
 double norm=0,residual=0;for(size_t x=0;x<original.size();x++){double delta=original[x]-reconstructed[x];norm+=original[x]*original[x];residual+=delta*delta;}
 receipt->span_relative_error=norm>0?std::sqrt(residual/norm):0;
 const size_t axes=std::min(rank,(size_t)4);std::vector<double> h(rank*axes),u(rank*rank),singular(axes),vt(axes*axes),rotation(rank*rank,0),oriented(n*rank);
 cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)rank,(MKL_INT)axes,(MKL_INT)n,1,q.data(),(MKL_INT)rank,anchors.data(),4,0,h.data(),(MKL_INT)axes);
 if(receipt->anchors){
  rc=LAPACKE_dgesdd(LAPACK_ROW_MAJOR,'A',(lapack_int)rank,(lapack_int)axes,h.data(),(lapack_int)axes,singular.data(),u.data(),(lapack_int)rank,vt.data(),(lapack_int)axes);if(rc)return rc;
  for(size_t i=0;i<rank;i++){
   for(size_t j=0;j<axes;j++)for(size_t z=0;z<axes;z++)rotation[i*rank+j]+=u[i*rank+z]*vt[z*axes+j];
   for(size_t j=axes;j<rank;j++)rotation[i*rank+j]=u[i*rank+j];
  }
 }else for(size_t i=0;i<rank;i++)rotation[i*rank+i]=1;
 cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)rank,(MKL_INT)rank,(MKL_INT)rank,1,rotation.data(),(MKL_INT)rank,rotation.data(),(MKL_INT)rank,0,gram.data(),(MKL_INT)rank);
 for(size_t i=0;i<rank;i++)for(size_t j=0;j<rank;j++)receipt->frame_orthogonality_error=std::max(receipt->frame_orthogonality_error,std::abs(gram[i*rank+j]-(i==j?1:0)));
 cblas_dgemm(CblasRowMajor,CblasNoTrans,CblasNoTrans,(MKL_INT)n,(MKL_INT)rank,(MKL_INT)rank,1,q.data(),(MKL_INT)rank,rotation.data(),(MKL_INT)rank,0,oriented.data(),(MKL_INT)rank);
 double before=0,after=0;for(size_t x=0;x<n;x++)for(size_t c=0;c<4;c++){
  double b=(c<rank?q[x*rank+c]:0)-anchors[x*4+c],a=(c<rank?oriented[x*rank+c]:0)-anchors[x*4+c];before+=b*b;after+=a*a;
 }
 receipt->anchor_error_before=std::sqrt(before);receipt->anchor_error_after=std::sqrt(after);
 for(size_t x=0;x<n;x++)for(size_t c=0;c<rank;c++)e[x*d+c]=oriented[x*rank+c];rms_slack(e,n,d,rank,input);return 0;
#endif
}catch(...){return -1;}
extern "C" int laplace_spectral_embed(size_t n,size_t d,const int*input,const double*coordinates,const int*rows,const int*cols,const double*weights,size_t count,double*embedding,size_t*features,laplace_spectral_receipt*receipt)try{
#ifndef LAPLACE_HAS_MKL
 return -2;
#else
 if(!n||d<7||!input||!coordinates||!rows||!cols||!weights||!embedding||!features||n>INT_MAX||d>INT_MAX||n>SIZE_MAX/d/sizeof(double))return -1;
 std::map<std::pair<int,int>,std::pair<double,size_t>> cells;
 for(size_t x=0;x<n;x++)if(input[x]<0||(size_t)input[x]>=n||input[input[x]]!=input[x])return -1;
 std::map<std::pair<int,int>,double> literal;
 for(size_t e=0;e<count;e++){if(rows[e]<0||cols[e]<0||(size_t)rows[e]>=n||(size_t)cols[e]>=n||!std::isfinite(weights[e]))return -1;literal[{rows[e],cols[e]}]+=weights[e];}
 for(const auto&edge:literal){auto&cell=cells[{input[edge.first.first],input[edge.first.second]}];cell.first+=edge.second;++cell.second;}
 std::vector<int> rr,cc;std::vector<double> ww;
 for(const auto&cell:cells)if(cell.second.first>0){rr.push_back(cell.first.first);cc.push_back(cell.first.second);ww.push_back(cell.second.first/cell.second.second);}
 size_t requested=std::min(d-6,n>2?n-2:0),spectral_rank=0;std::vector<double> spectral(n*requested);
 int rc=laplace_graph_basis(rr.data(),cc.data(),ww.data(),ww.size(),n,requested,spectral.data(),&spectral_rank);if(rc)return rc;
 laplace_spectral_receipt measured{};measured.requested_modes=requested;measured.retained_modes=spectral_rank;
 std::vector<double> degree(n,0),mass_scaled(n*spectral_rank),gram(spectral_rank*spectral_rank),lf(n*spectral_rank,0);
 std::vector<size_t> parent(n);std::iota(parent.begin(),parent.end(),0);
 auto root=[&](size_t x){while(parent[x]!=x){parent[x]=parent[parent[x]];x=parent[x];}return x;};
 for(size_t edge=0;edge<ww.size();edge++){
  size_t x=rr[edge],y=cc[edge];double half=0.5*ww[edge];degree[x]+=half;degree[y]+=half;parent[root(x)]=root(y);
  for(size_t c=0;c<spectral_rank;c++){lf[x*spectral_rank+c]-=half*spectral[y*requested+c];lf[y*spectral_rank+c]-=half*spectral[x*requested+c];}
 }
 std::set<size_t> components;
 for(size_t x=0;x<n;x++)if(degree[x]>0){++measured.active_vertices;components.insert(root(x));}
 measured.components=components.size();
 for(size_t x=0;x<n;x++)for(size_t c=0;c<spectral_rank;c++){
  double f=spectral[x*requested+c];lf[x*spectral_rank+c]+=degree[x]*f;mass_scaled[x*spectral_rank+c]=std::sqrt(degree[x])*f;
 }
 cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)spectral_rank,(MKL_INT)spectral_rank,(MKL_INT)n,1,mass_scaled.data(),(MKL_INT)spectral_rank,mass_scaled.data(),(MKL_INT)spectral_rank,0,gram.data(),(MKL_INT)spectral_rank);
 for(size_t c=0;c<spectral_rank;c++){
  double rayleigh=0,mass=gram[c*spectral_rank+c],residual=0,denominator=0;
  for(size_t x=0;x<n;x++)rayleigh+=spectral[x*requested+c]*lf[x*spectral_rank+c];
  if(!(mass>0))return -5;rayleigh/=mass;
  for(size_t x=0;x<n;x++){double left=lf[x*spectral_rank+c],right=rayleigh*degree[x]*spectral[x*requested+c];residual+=(left-right)*(left-right);denominator+=left*left+right*right;}
  measured.eigen_residual_max=std::max(measured.eigen_residual_max,std::sqrt(residual/std::max(1.0,denominator)));
  for(size_t j=0;j<spectral_rank;j++)measured.mass_orthogonality_error=std::max(measured.mass_orthogonality_error,std::abs(gram[c*spectral_rank+j]-(c==j?1:0)));
 }
 size_t f=spectral_rank+4,k=std::min(n,f);std::vector<double> a(n*f,0),u(n*k),s(k),vt(k*f);
 for(size_t x=0;x<n;x++)if(input[x]==(int)x){for(size_t c=0;c<4;c++){if(!std::isfinite(coordinates[x*4+c]))return -1;a[x*f+c]=coordinates[x*4+c];}for(size_t c=0;c<spectral_rank;c++)a[x*f+4+c]=spectral[x*requested+c];}
 rc=LAPACKE_dgesdd(LAPACK_ROW_MAJOR,'S',(lapack_int)n,(lapack_int)f,a.data(),(lapack_int)f,s.data(),u.data(),(lapack_int)k,vt.data(),(lapack_int)f);if(rc)return rc;
 double cutoff=s[0]*std::max(n,f)*std::numeric_limits<double>::epsilon();size_t rank=0;while(rank<k&&s[rank]>cutoff)++rank;
 measured.combined_rank=rank;double total=0,discarded=0;for(size_t c=0;c<k;c++){double energy=s[c]*s[c];total+=energy;if(c>=rank)discarded+=energy;}measured.discarded_svd_energy=total>0?discarded/total:0;
 if(receipt)*receipt=measured;
 std::fill_n(embedding,n*d,0.0);for(size_t x=0;x<n;x++)std::copy_n(u.data()+x*k,rank,embedding+x*d);*features=rank+2;rms_slack(embedding,n,d,rank,input);return 0;
#endif
}catch(...){return -1;}
