#include "target_ops.hpp"
#include <algorithm>
#include <cmath>
#include <climits>
#include <stdexcept>
#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
#include <mkl_lapacke.h>
#endif
namespace laplace::synthesis {
void bound_target(TargetRow&row,size_t outputs,double bound,bool equal_candidates){
 if(!(bound>0&&bound<1)||!std::isfinite(bound)||row.delta.empty()||row.delta.size()>outputs)throw std::runtime_error("invalid candidate-set output bound");
 double maximum=-INFINITY;for(const auto&e:row.delta)maximum=std::max(maximum,row.floor+e.second);
 double total=0;for(const auto&e:row.delta)total+=std::exp(row.floor+e.second-maximum);
 double normalizer=maximum+std::log(total),minimum=0;std::map<int,double> logits;
 for(const auto&e:row.delta){double value=equal_candidates?0:row.floor+e.second-normalizer;logits[e.first]=value;minimum=std::min(minimum,value);}
 size_t outside=outputs-row.delta.size();double floor=minimum-1;
 if(outside){double selected_mass=equal_candidates?double(row.delta.size()):1;floor=std::min(floor,std::log(selected_mass)+std::log(bound)-std::log1p(-bound)-std::log(double(outside)));}
 row.floor=floor;for(const auto&e:logits)row.delta[e.first]=e.second-floor;
}
std::vector<TargetRow> transfer_targets(size_t n,int eos,const int*input,const int*emit,const int*rows,const int*cols,const double*weights,size_t count,double outside_bound,bool equal_candidates){
 if(!n||eos<0||(size_t)eos>=n||!input||!emit||(count&&(!rows||!cols||!weights)))throw std::runtime_error("invalid transfer dimensions");
 std::vector<std::map<int,double>> mass(n);for(size_t x=0;x<n;x++)if(input[x]<0||(size_t)input[x]>=n||emit[x]<-1||(emit[x]>=0&&(size_t)emit[x]>=n))throw std::runtime_error("invalid realization map");
 for(size_t x=0;x<n;x++)if(input[input[x]]!=input[x])throw std::runtime_error("input realization map must resolve to canonical representatives");
 std::map<std::pair<int,int>,double> literal;
 for(size_t e=0;e<count;e++){
  if(rows[e]<0||cols[e]<0||(size_t)rows[e]>=n||(size_t)cols[e]>=n||!std::isfinite(weights[e])||weights[e]<0)throw std::runtime_error("transfer requires finite positive evidence");
  literal[{rows[e],cols[e]}]+=weights[e];
 }
 std::map<std::pair<int,int>,std::pair<double,size_t>> canonical_cells;
 for(const auto&edge:literal){auto&cell=canonical_cells[{input[edge.first.first],input[edge.first.second]}];cell.first+=edge.second;++cell.second;}
 for(const auto&edge:canonical_cells){int x=edge.first.first,y=edge.first.second;if(x!=y&&emit[y]>=0)mass[x][emit[y]]+=edge.second.first/edge.second.second;}
 std::vector<TargetRow> canonical(n),result(n);
 for(size_t x=0;x<n;x++){
  double sum=0;for(auto&e:mass[x])sum+=e.second;if(!std::isfinite(sum))throw std::runtime_error("transfer mass overflow");
  auto&r=canonical[x];if(sum==0){r.delta[eos]=1;if(outside_bound>0)bound_target(r,n,outside_bound,equal_candidates);continue;}
  for(auto&e:mass[x])if(e.second>0)r.floor=std::min(r.floor,std::log(e.second/sum*n));r.floor-=1;
  for(auto&e:mass[x])if(e.second>0)r.delta[e.first]=std::log(e.second/sum*n)-r.floor;
  if(outside_bound>0)bound_target(r,n,outside_bound,equal_candidates);
 }
 for(size_t x=0;x<n;x++)result[x]=canonical[input[x]];return result;
}
int fit_targets(const float*r,size_t samples,size_t outputs,size_t d,const std::vector<TargetRow>&targets,float*head,size_t*rank,double*error,double*accuracy,bool solve,SupportMeasure*measure,double outside_bound){
#ifndef LAPLACE_HAS_MKL
 return -2;
#else
 if(!r||!head||!rank||!error||!accuracy||!samples||!outputs||!d||samples>INT_MAX||outputs>INT_MAX||d>INT_MAX||targets.size()!=samples)return -1;
 try{
  size_t k=std::min(samples,d);std::vector<double> a,u,s,vt,base;
  if(solve){a.resize(samples*d);for(size_t i=0;i<a.size();i++){if(!std::isfinite(r[i]))return -1;a[i]=r[i];}
   u.resize(samples*k);s.resize(k);vt.resize(k*d);int rc=LAPACKE_dgesdd(LAPACK_ROW_MAJOR,'S',(lapack_int)samples,(lapack_int)d,a.data(),(lapack_int)d,s.data(),u.data(),(lapack_int)k,vt.data(),(lapack_int)d);
   if(rc)return rc;a.clear();a.shrink_to_fit();*rank=0;while(*rank<k&&s[*rank]>s[0]*1e-6)++*rank;base.assign(*rank,0);
   for(size_t x=0;x<samples;x++)for(size_t z=0;z<*rank;z++)base[z]+=u[x*k+z]*targets[x].floor;
  }else *rank=0;
  constexpr size_t block=128;std::vector<float> best(samples,-INFINITY);std::vector<int> winner(samples,-1);std::vector<double> desired(samples,0);
  std::vector<double> maximum(samples,-INFINITY),inside(samples,0),outside(samples,0);
  for(size_t x=0;x<samples;x++){desired[x]=targets[x].floor;for(const auto&e:targets[x].delta){if(e.first<0||(size_t)e.first>=outputs||!std::isfinite(e.second))return -1;desired[x]=std::max(desired[x],targets[x].floor+e.second);}}
  double squared=0,total=0;
  for(size_t first=0;first<outputs;first+=block){size_t b=std::min(block,outputs-first);
   if(solve){std::vector<double> projected(*rank*b),weights(d*b);for(size_t z=0;z<*rank;z++)std::fill_n(projected.data()+z*b,b,base[z]);
    for(size_t x=0;x<samples;x++){
     auto begin=targets[x].delta.lower_bound((int)first),end=targets[x].delta.lower_bound((int)(first+b));
     for(auto it=begin;it!=end;it++)for(size_t z=0;z<*rank;z++)projected[z*b+(size_t)it->first-first]+=u[x*k+z]*it->second;
    }
    for(size_t z=0;z<*rank;z++)for(size_t y=0;y<b;y++)projected[z*b+y]/=s[z];
    if(*rank)cblas_dgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)d,(MKL_INT)b,(MKL_INT)*rank,1,vt.data(),(MKL_INT)d,projected.data(),(MKL_INT)b,0,weights.data(),(MKL_INT)b);
    for(size_t y=0;y<b;y++)for(size_t c=0;c<d;c++)head[(first+y)*d+c]=(float)weights[c*b+y];
   }
   std::vector<float> prediction(samples*b);cblas_sgemm(CblasRowMajor,CblasNoTrans,CblasTrans,(MKL_INT)samples,(MKL_INT)b,(MKL_INT)d,1,r,(MKL_INT)d,head+first*d,(MKL_INT)d,0,prediction.data(),(MKL_INT)b);
   for(size_t x=0;x<samples;x++){
    auto it=targets[x].delta.lower_bound((int)first);for(size_t y=0;y<b;y++){
     bool admitted=it!=targets[x].delta.end()&&it->first==(int)(first+y);
     double target=targets[x].floor;if(admitted){target+=it->second;++it;}
     float value=prediction[x*b+y];if(!std::isfinite(value))return -3;double diff=value-target;squared+=diff*diff;total+=target*target;
     if(measure){if(value>maximum[x]){double scale=std::exp(maximum[x]-value);inside[x]*=scale;outside[x]*=scale;maximum[x]=value;}
      (admitted?inside[x]:outside[x])+=std::exp(value-maximum[x]);}
     if(value>best[x]){best[x]=value;winner[x]=(int)(first+y);}
    }
   }
  }
  size_t correct=0;for(size_t x=0;x<samples;x++){double target=targets[x].floor;auto it=targets[x].delta.find(winner[x]);if(it!=targets[x].delta.end())target+=it->second;if(std::abs(target-desired[x])<=1e-6)++correct;}
  if(measure){*measure={};measure->rows=samples;for(size_t x=0;x<samples;x++){
   double mass=outside[x]/(inside[x]+outside[x]);measure->mean_outside_mass+=mass;measure->max_outside_mass=std::max(measure->max_outside_mass,mass);
   if(outside_bound>0&&mass>outside_bound)++measure->exceeding_bound;
  }measure->mean_outside_mass/=samples;}
  *error=total>0?std::sqrt(squared/total):0;*accuracy=(double)correct/samples;return 0;
 }catch(...){return -1;}
#endif
}
}
