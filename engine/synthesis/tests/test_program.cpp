#include <gtest/gtest.h>
#include "laplace/synthesis/program.h"
#include "laplace/synthesis/lookup_compile.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <map>
#include <memory>
#include <string>
#include <vector>
namespace {
struct Tensor{std::vector<size_t> shape;std::vector<float> values;};
struct Capture{std::map<std::string,Tensor> hf,gg;};
int sink(void*user,const char*hf,const char*gg,const size_t*shape,size_t rank,const float*values,size_t count){
 auto&c=*static_cast<Capture*>(user);Tensor t{{shape,shape+rank},{values,values+count}};
 for(float x:t.values)if(!std::isfinite(x))return -1;
 if(hf&&!c.hf.emplace(hf,t).second)return -1;if(gg&&!c.gg.emplace(gg,t).second)return -1;return 0;
}
struct Fixture{
 static constexpr size_t n=12,d=64;std::vector<double> embedding=std::vector<double>(n*d,0);std::vector<int> input,output,rows,cols;std::vector<double> weights;
 std::unique_ptr<laplace_synthesis_program,decltype(&laplace_program_free)> program{nullptr,laplace_program_free};
 int op;
 Fixture(size_t layers){for(size_t x=0;x<n;x++){embedding[x*d+x]=7;embedding[x*d+d-1]=1;input.push_back(x);output.push_back(x);rows.push_back(x);cols.push_back((x+1)%n);weights.push_back(1);}
  program.reset(laplace_program_create(n,d,embedding.data(),1,2,1e-5,10000,1,0.5,0.5,layers));
  op=laplace_program_plane(program.get(),rows.data(),cols.data(),weights.data(),weights.size());
 }
 int layer(size_t experts=0){std::vector<int> ops(4,op),e(experts,op);return laplace_program_layer(program.get(),4,1,96,ops.data(),op,e.data(),experts,experts?2:0);}
 int compile(Capture&c){return laplace_program_compile(program.get(),op,input.data(),output.data(),sink,nullptr,&c);}
};
}
TEST(LaplaceSynthesisProgram, CompleteThirtyTwoLayerGroupedQueryProgram){
 Fixture f(32);ASSERT_NE(f.program,nullptr);for(int i=0;i<32;i++)ASSERT_EQ(f.layer(),0);
 Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());ASSERT_EQ(c.gg.size(),32*9+3);
 for(int i=0;i<32;i++){std::string p="blk."+std::to_string(i)+".";EXPECT_EQ(c.gg.at(p+"attn_k.weight").shape,(std::vector<size_t>{16,64}));
  for(const char*name:{"attn_q.weight","attn_k.weight","attn_v.weight","attn_output.weight","ffn_up.weight","ffn_gate.weight","ffn_down.weight"}){
   const auto&v=c.gg.at(p+name).values;EXPECT_TRUE(std::any_of(v.begin(),v.end(),[](float x){return x!=0;}))<<p<<name;}}
 EXPECT_NE(c.gg.at("blk.0.attn_q.weight").values,c.gg.at("blk.31.attn_q.weight").values);
 size_t rows=0,width=0;auto*states=laplace_program_states(f.program.get(),&rows,&width);ASSERT_NE(states,nullptr);EXPECT_EQ(rows,13);EXPECT_EQ(width,64);
 EXPECT_NE(f.compile(c),0);
}
TEST(LaplaceSynthesisProgram, OrderedPrefixesCarryDifferentStatesForSameFinalToken){
 Fixture f(8);for(int i=0;i<8;i++)ASSERT_EQ(f.layer(),0);int first[]={3,4,5},second[]={6,4,7};
 ASSERT_EQ(laplace_program_sequence(f.program.get(),first,3,1),0);ASSERT_EQ(laplace_program_sequence(f.program.get(),second,3,1),0);
 Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());size_t rows,width;const float*states=laplace_program_states(f.program.get(),&rows,&width);
 // Vocabulary nodes occupy 0..12. The distinct [3,4] and [6,4] prefix
 // states occupy 13 and 15; both end in token 4.
 ASSERT_EQ(rows,17);double difference=0;for(size_t x=0;x<width;x++)difference+=std::abs(states[13*width+x]-states[15*width+x]);EXPECT_GT(difference,1e-5);
}
TEST(LaplaceSynthesisProgram, UsageSequencesDoNotReplaceTheDeclaredClaimOperator){
 for(bool observed_readout:{false,true}){
  Fixture f(4);for(int i=0;i<4;i++)ASSERT_EQ(f.layer(),0);int sequence[]={3,7,10};
  ASSERT_EQ(laplace_program_sequence(f.program.get(),sequence,3,1),0);
  ASSERT_EQ(laplace_program_observation_readout(f.program.get(),observed_readout),0);
  Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());
  const size_t *nodes,*offsets;const double *floors,*deltas;const int* columns;
  size_t count=laplace_program_targets(f.program.get(),&nodes,&floors,&offsets,&columns,&deltas);bool found=false;
  for(size_t x=0;x<count;x++)if(nodes[x]==4){found=true;EXPECT_EQ(offsets[x+1]-offsets[x],1);EXPECT_EQ(columns[offsets[x]],observed_readout?7:4);}
  EXPECT_TRUE(found);
 }
}
TEST(LaplaceSynthesisProgram, RepeatedExpertOperatorsPartitionEvidenceAndRouteDistinctly){
 Fixture f(4);for(int i=0;i<4;i++)ASSERT_EQ(f.layer(4),0);Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());
 const auto&router=c.gg.at("blk.0.ffn_gate_inp.weight").values;for(int e=1;e<4;e++)EXPECT_FALSE(std::equal(router.begin(),router.begin()+64,router.begin()+e*64));
 EXPECT_EQ(c.gg.at("blk.0.ffn_up_exps.weight").shape,(std::vector<size_t>{4,96,64}));
 EXPECT_NE(c.hf.at("model.layers.0.block_sparse_moe.experts.0.w3.weight").values,c.hf.at("model.layers.0.block_sparse_moe.experts.1.w3.weight").values);
 EXPECT_EQ(c.gg.size(),4*10+3);
}
TEST(LaplaceSynthesisProgram, IncompleteSchedulesCannotExport){Fixture f(4);ASSERT_EQ(f.layer(),0);Capture c;EXPECT_NE(f.compile(c),0);EXPECT_TRUE(c.gg.empty());}
TEST(LaplaceSynthesisProgram, ExplicitlyDisabledBodyOperatorsEmitZeroContributions){
 Fixture f(4);int ops[]={-2,-2,-2,-2};for(int i=0;i<4;i++)ASSERT_EQ(laplace_program_layer(f.program.get(),4,1,96,ops,-2,nullptr,0,0),0);
 Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());
 for(int i=0;i<4;i++)for(const char* name:{"attn_q.weight","attn_v.weight","attn_output.weight","ffn_gate.weight","ffn_up.weight","ffn_down.weight"}){
  const auto&values=c.gg.at("blk."+std::to_string(i)+"."+name).values;EXPECT_TRUE(std::all_of(values.begin(),values.end(),[](float x){return x==0;}));}
}
TEST(LaplaceSynthesisProgram, CandidateSetFloorBoundsTotalOutsideSoftmaxMass){
 Fixture f(4);for(int i=0;i<4;i++)ASSERT_EQ(f.layer(),0);
 ASSERT_EQ(laplace_program_output_floor(f.program.get(),1e-6,1),0);
 Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());
 size_t rows,width;const float* states=laplace_program_states(f.program.get(),&rows,&width);
 const auto&head=c.gg.at("output.weight").values;
 for(size_t x=0;x<f.n;x++) {
  std::vector<double> logits(f.n);double maximum=-INFINITY;
  for(size_t y=0;y<f.n;y++){for(size_t z=0;z<f.d;z++)logits[y]+=states[(x+1)*f.d+z]*head[y*f.d+z];maximum=std::max(maximum,logits[y]);}
  double inside=0,outside=0;for(size_t y=0;y<f.n;y++)(y==(x+1)%f.n?inside:outside)+=std::exp(logits[y]-maximum);
  EXPECT_LT(outside/(inside+outside),1.01e-6);
 }
 const size_t *nodes,*offsets;const double *floors,*deltas;const int* columns;
 ASSERT_EQ(laplace_program_targets(f.program.get(),&nodes,&floors,&offsets,&columns,&deltas),f.n);
 for(size_t x=0;x<f.n;x++){EXPECT_EQ(nodes[x],x+1);EXPECT_EQ(offsets[x+1]-offsets[x],1);EXPECT_EQ(columns[offsets[x]],(x+1)%f.n);EXPECT_NEAR(floors[x]+deltas[offsets[x]],0,1e-12);}
}
TEST(LaplaceSynthesisProgram, SetScoringPreservesAllMembersWithoutUsingTheirRelativeWeights){
 Fixture f(4);for(int i=0;i<4;i++)ASSERT_EQ(f.layer(),0);
 std::vector<int> rows,cols;std::vector<double> weights;
 for(size_t x=0;x<f.n;x++){rows.push_back(x);cols.push_back((x+1)%f.n);weights.push_back(1);rows.push_back(x);cols.push_back((x+2)%f.n);weights.push_back(9);}
 int readout=laplace_program_plane(f.program.get(),rows.data(),cols.data(),weights.data(),weights.size());ASSERT_GE(readout,0);
 ASSERT_EQ(laplace_program_output_floor(f.program.get(),1e-6,1),0);
 Capture c;ASSERT_EQ(laplace_program_compile(f.program.get(),readout,f.input.data(),f.output.data(),sink,nullptr,&c),0);
 const size_t *nodes,*offsets;const double *floors,*deltas;const int* columns;
 ASSERT_EQ(laplace_program_targets(f.program.get(),&nodes,&floors,&offsets,&columns,&deltas),f.n);
 for(size_t x=0;x<f.n;x++){
  EXPECT_EQ(offsets[x+1]-offsets[x],2);EXPECT_DOUBLE_EQ(deltas[offsets[x]],deltas[offsets[x]+1]);
  double supported=2*std::exp(floors[x]+deltas[offsets[x]]),excluded=(f.n-2)*std::exp(floors[x]);
  EXPECT_NEAR(excluded/(supported+excluded),1e-6,1e-15);
 }
}
TEST(LaplaceSynthesisProgram, TiedEmbeddingConstraintUsesTheConstructedInputWeights){
 Fixture f(4);for(int i=0;i<4;i++)ASSERT_EQ(f.layer(),0);ASSERT_EQ(laplace_program_tie_embeddings(f.program.get(),1),0);
 Capture c;ASSERT_EQ(f.compile(c),0)<<laplace_program_error(f.program.get());EXPECT_EQ(c.gg.at("token_embd.weight").values,c.gg.at("output.weight").values);
}
TEST(LaplaceSynthesisProgram, LargeVocabularyReadoutUsesBoundedBlocksAndEveryTarget){
 constexpr size_t n=10000,d=32;std::vector<float> states(n*d,0),head(n*d);std::vector<int> input(n),emit(n),rows(n),cols(n);std::vector<double> weights(n,1);
 for(size_t x=0;x<n;x++){input[x]=emit[x]=rows[x]=(int)x;cols[x]=3+(int)((x+1)%16);states[x*d+x%16]=1;}
 size_t rank=0;double error=0,accuracy=0;
 ASSERT_EQ(laplace_readout_fit(states.data(),n,d,input.data(),emit.data(),rows.data(),cols.data(),weights.data(),n,head.data(),&rank,&error,&accuracy),0);
 EXPECT_EQ(rank,16);EXPECT_LT(error,1e-5);EXPECT_EQ(accuracy,1);
}
