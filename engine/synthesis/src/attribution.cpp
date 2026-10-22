#include "laplace/synthesis/attribution.h"
#include "laplace/core/glicko2.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <map>
#include <set>
#include <vector>
extern "C" int laplace_source_attribution(const laplace_source_observation*obs,size_t count,laplace_source_share*out,size_t capacity,size_t*written)try{
 if(!obs||!count||!out||!written)return -1;
 using Id=std::array<uint8_t,16>;struct Acc{std::set<Id> claims;uint64_t games=0;long double fisher=0,remainder=0;};std::map<Id,Acc> sources;
 constexpr int64_t scale=173717800000LL,neutral=1500000000000LL,one=1000000000LL;
 for(size_t i=0;i<count;i++){
  const auto&o=obs[i];if(o.games<=0||o.rd<0||o.opponent_rd<0||o.rating<0||o.opponent_rating<0)return -1;
  Id source,claim;std::copy_n(o.source_id,16,source.begin());std::copy_n(o.claim_id,16,claim.begin());auto&a=sources[source];
  if((uint64_t)o.games>UINT64_MAX-a.games)return -1;a.games+=(uint64_t)o.games;a.claims.insert(claim);
  auto phi=laplace_fp_div(o.opponent_rd,scale);auto g=laplace_glicko2_g(phi);
  auto mu=laplace_fp_div(o.rating-neutral,scale),opponent=laplace_fp_div(o.opponent_rating-neutral,scale);
  auto expected=laplace_glicko2_E(mu,opponent,g);
  long double gd=(long double)g/one,e=(long double)expected/one;
  a.fisher+=(long double)o.games*gd*gd*e*(1-e);
 }
 *written=sources.size();if(capacity<sources.size())return -2;long double total=0;for(const auto&s:sources)total+=s.second.fisher;
 if(!std::isfinite(total)||total<=0)return -3;
 uint32_t allocated=0;size_t i=0;std::vector<std::pair<long double,size_t>> remainders;
 for(auto&s:sources){auto&a=s.second;auto&r=out[i];std::memset(&r,0,sizeof(r));std::copy(s.first.begin(),s.first.end(),r.source_id);
  r.claims_touched=a.claims.size();r.games=a.games;r.fisher_weight=(double)a.fisher;
  long double exact=10000*a.fisher/total;r.share_bp=(uint32_t)std::floor(exact);allocated+=r.share_bp;remainders.push_back({exact-r.share_bp,i++});}
 if(allocated>10000)return -4;
 std::stable_sort(remainders.begin(),remainders.end(),[](auto&a,auto&b){return a.first>b.first;});
 for(uint32_t k=allocated;k<10000;k++)++out[remainders[(k-allocated)%remainders.size()].second].share_bp;
 return 0;
}catch(...){return -1;}
