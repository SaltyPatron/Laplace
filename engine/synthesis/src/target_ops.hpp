#pragma once
#include <cstddef>
#include <map>
#include <vector>
namespace laplace::synthesis {
struct TargetRow {double floor=0;std::map<int,double> delta;};
struct SupportMeasure {double mean_outside_mass=0,max_outside_mass=0;size_t rows=0,exceeding_bound=0;};
std::vector<TargetRow> transfer_targets(size_t,int,const int*,const int*,const int*,const int*,const double*,size_t,double outside_bound=0,bool equal_candidates=false);
void bound_target(TargetRow&,size_t,double,bool);
int fit_targets(const float*,size_t,size_t,size_t,const std::vector<TargetRow>&,float*,size_t*,double*,double*,bool,SupportMeasure* measure=nullptr,double outside_bound=0);
}
