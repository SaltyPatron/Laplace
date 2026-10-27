#include "laplace/synthesis/program.h"
#include "laplace/synthesis/init.h"
#include "target_ops.hpp"
#include "laplace/synthesis/lookup_compile.h"
#include "laplace/synthesis/tensor_decompose.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <climits>
#include <cstring>
#include <limits>
#include <map>
#include <memory>
#include <numeric>
#include <stdexcept>
#include <string>
#include <tuple>
#include <utility>
#include <vector>
#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
#include <mkl_lapacke.h>
#endif
namespace {
using Vec=std::vector<float>;
struct Plane{std::vector<int> rows,cols;std::vector<double> weights;};
struct Layer{size_t heads,kv,ffn,experts,used;std::vector<int> operators,expert_ops;int ffn_op;};
struct Factors{size_t d=0,k=0;Vec left,right;};
void require(bool condition,const char *message){if(!condition)throw std::runtime_error(message);}
size_t product(size_t a,size_t b){require(!a||b<=SIZE_MAX/a/sizeof(float),"tensor shape overflow");return a*b;}
void finite(const Vec&v){for(float x:v)require(std::isfinite(x),"nonfinite constructed tensor or executed state");}
void multiply(const Vec&a,const Vec&b,Vec&out,size_t m,size_t n,size_t k,bool transpose_b=true){
#ifdef LAPLACE_HAS_MKL
    if(!m||!n||!k){out.assign(product(m,n),0);return;}
    out.resize(product(m,n));
    cblas_sgemm(CblasRowMajor,CblasNoTrans,transpose_b?CblasTrans:CblasNoTrans,(MKL_INT)m,(MKL_INT)n,(MKL_INT)k,
        1,a.data(),(MKL_INT)k,b.data(),(MKL_INT)(transpose_b?k:n),0,out.data(),(MKL_INT)n);
#else
    throw std::runtime_error("native program requires MKL");
#endif
}
Vec normalized(const Vec &r,size_t m,size_t d,double epsilon){Vec out(r.size());for(size_t x=0;x<m;x++){
    double ss=0;for(size_t c=0;c<d;c++)ss+=(double)r[x*d+c]*r[x*d+c];double inv=1/std::sqrt(ss/d+epsilon);
    for(size_t c=0;c<d;c++)out[x*d+c]=(float)(r[x*d+c]*inv);
}return out;}
Factors factor(const std::vector<double>&matrix,size_t d,size_t cap,bool transpose){
    Vec a(d*d),u(d*d),s(d),vt(d*d);for(size_t x=0;x<d;x++)for(size_t y=0;y<d;y++)a[x*d+y]=(float)matrix[transpose?y*d+x:x*d+y];
    size_t rank=0;int rc=tensor_svd_truncate(a.data(),d,d,0,&rank,u.data(),s.data(),vt.data(),d);
    require(rc==0,"native operator factorization failed");
    Factors f;f.d=d;f.k=std::min(rank,cap);f.left.resize(f.k*d);f.right.resize(f.k*d);
    for(size_t z=0;z<f.k;z++){float gain=std::sqrt(std::max(0.0f,s[z]));for(size_t c=0;c<d;c++){
        f.left[z*d+c]=gain*u[c*d+z];f.right[z*d+c]=gain*vt[z*d+c];}}
    finite(f.left);finite(f.right);return f;
}
Factors slice(const Factors &f,size_t ordinal,size_t copies,size_t cap){Factors out;out.d=f.d;
    for(size_t z=ordinal;z<f.k&&out.k<cap;z+=copies){out.left.insert(out.left.end(),f.left.begin()+z*f.d,f.left.begin()+(z+1)*f.d);
        out.right.insert(out.right.end(),f.right.begin()+z*f.d,f.right.begin()+(z+1)*f.d);++out.k;}return out;}
Vec shared_basis(const std::vector<Factors>&f,size_t first,size_t count,size_t cap,size_t d){
#ifdef LAPLACE_HAS_MKL
    size_t rows=0;for(size_t h=first;h<first+count;h++)rows+=f[h].k;
    Vec out(cap*d,0);if(!rows)return out;size_t k=std::min(rows,d);
    Vec stacked(rows*d),u(rows*k),s(k),vt(k*d);size_t offset=0;
    for(size_t h=first;h<first+count;h++){std::copy(f[h].right.begin(),f[h].right.end(),stacked.begin()+offset);offset+=f[h].right.size();}
    int rc=LAPACKE_sgesdd(LAPACK_ROW_MAJOR,'S',(lapack_int)rows,(lapack_int)d,stacked.data(),(lapack_int)d,s.data(),u.data(),(lapack_int)k,vt.data(),(lapack_int)d);
    require(rc==0,"shared KV basis construction failed");
    for(size_t z=0;z<std::min(k,cap);z++)std::copy_n(vt.data()+z*d,d,out.data()+z*d);return out;
#else
    throw std::runtime_error("native program requires MKL");
#endif
}
Vec project_left(const Factors &f,const Vec &common,size_t cap){
    Vec overlap,out(f.d*cap,0);if(!f.k)return out;
    multiply(f.right,common,overlap,f.k,cap,f.d);
#ifdef LAPLACE_HAS_MKL
    cblas_sgemm(CblasRowMajor,CblasTrans,CblasNoTrans,(MKL_INT)f.d,(MKL_INT)cap,(MKL_INT)f.k,
        1,f.left.data(),(MKL_INT)f.d,overlap.data(),(MKL_INT)cap,0,out.data(),(MKL_INT)cap);
#endif
    return out;
}
void rope(Vec &v,size_t m,size_t heads,size_t hd,const std::vector<int>&positions,double base,double frequency_scale){
    for(size_t x=0;x<m;x++)for(size_t h=0;h<heads;h++)for(size_t c=0;c<hd/2;c++){
        double theta=positions[x]*frequency_scale*std::pow(base,-2.0*c/hd),cs=std::cos(theta),sn=std::sin(theta);
        size_t a=x*heads*hd+h*hd+2*c,b=a+1;float va=v[a],vb=v[b];v[a]=(float)(va*cs-vb*sn);v[b]=(float)(va*sn+vb*cs);
    }
}
float silu(float x){return x>=0?x/(1+std::exp(-x)):x*std::exp(x)/(1+std::exp(x));}
}
struct laplace_synthesis_program{
    size_t n,d,expected;int bos,eos;double eps,rope_base,rope_scale,attn_gain,resid_gain;Vec states,final_states;
    std::vector<int> tokens,parents,positions;std::map<std::pair<int,int>,int> prefixes;
    std::vector<Plane> planes;std::vector<Layer> layers;std::map<std::pair<int,int>,double> observed;
    std::string error;bool compiled=false,tied=false,equal_candidates=false,observation_readout=false;double outside_bound=0;
    std::vector<size_t> target_nodes,target_offsets;std::vector<double> target_floors,target_deltas;std::vector<int> target_columns;
};
extern "C" laplace_synthesis_program *laplace_program_create(size_t n,size_t d,const double *embedding,int bos,int eos,double epsilon,double rope_base,double rope_scale,double ag,double rg,size_t layers){
    try{require(laplace_synthesis_init()==0,"native synthesis runtime initialization failed");require(n>2&&d>0&&n<INT32_MAX&&d<INT32_MAX&&embedding&&bos>=0&&eos>=0&&(size_t)bos<n&&(size_t)eos<n,"invalid native program dimensions");
        require(std::isfinite(epsilon)&&epsilon>0&&std::isfinite(rope_base)&&rope_base>0&&std::isfinite(rope_scale)&&rope_scale>=0&&std::isfinite(ag)&&std::isfinite(rg)&&layers>0,"invalid native program scale policy");
        auto p=std::make_unique<laplace_synthesis_program>();p->n=n;p->d=d;p->expected=layers;p->bos=bos;p->eos=eos;p->eps=epsilon;p->rope_base=rope_base;p->rope_scale=rope_scale;p->attn_gain=ag;p->resid_gain=rg;
        p->tokens.push_back(bos);p->parents.push_back(-1);p->positions.push_back(0);
        for(size_t x=0;x<n;x++){p->tokens.push_back((int)x);p->parents.push_back(0);p->positions.push_back(1);p->prefixes[{0,(int)x}]=(int)x+1;}
        p->states.resize(product(n,d));for(size_t i=0;i<n*d;i++){require(std::isfinite(embedding[i]),"invalid input representation");p->states[i]=(float)embedding[i];}
        return p.release();
    }catch(...){return nullptr;}}
extern "C" int laplace_program_tie_embeddings(laplace_synthesis_program*p,int tied){if(!p||p->compiled)return -1;p->tied=tied!=0;return 0;}
extern "C" int laplace_program_output_floor(laplace_synthesis_program*p,double bound,int equal){if(!p||p->compiled||!std::isfinite(bound)||bound<0||bound>=1)return -1;p->outside_bound=bound;p->equal_candidates=equal!=0;return 0;}
extern "C" int laplace_program_observation_readout(laplace_synthesis_program*p,int enabled){if(!p||p->compiled)return -1;p->observation_readout=enabled!=0;return 0;}
extern "C" int laplace_program_plane(laplace_synthesis_program*p,const int*r,const int*c,const double*w,size_t count){if(!p||p->compiled)return -1;try{
    require(count&&r&&c&&w,"declared operator has no evidence");Plane plane;for(size_t e=0;e<count;e++){
        require(r[e]>=0&&c[e]>=0&&(size_t)r[e]<p->n&&(size_t)c[e]<p->n&&std::isfinite(w[e]),"invalid operator endpoints");
        plane.rows.push_back(r[e]);plane.cols.push_back(c[e]);plane.weights.push_back(w[e]);}p->planes.push_back(std::move(plane));return (int)p->planes.size()-1;
    }catch(const std::exception&e){p->error=e.what();return -1;}}
extern "C" int laplace_program_layer(laplace_synthesis_program*p,size_t heads,size_t kv,size_t ffn,const int*ops,int ffn_op,const int*expert_ops,size_t experts,size_t used){if(!p||p->compiled)return -1;try{
    require(heads&&kv&&heads%kv==0&&p->d%heads==0&&(p->d/heads)%2==0&&ffn&&ops,"invalid layer/head/KV dimensions");
    require(experts==0||(expert_ops&&used>0&&used<=experts),"invalid expert routing dimensions");
    auto valid=[&](int op){return op==-2||op==-1||(op>=0&&(size_t)op<p->planes.size());};
    Layer layer{heads,kv,ffn,experts,used,{},{},ffn_op};for(size_t h=0;h<heads;h++){require(valid(ops[h]),"unknown head operator");layer.operators.push_back(ops[h]);}
    require(valid(ffn_op)&&ffn_op!=-1,"FFN requires an evidenced or explicitly disabled operator");
    for(size_t e=0;e<experts;e++){require(valid(expert_ops[e])&&expert_ops[e]!=-1,"expert requires an evidenced or explicitly disabled operator");layer.expert_ops.push_back(expert_ops[e]);}
    p->layers.push_back(std::move(layer));return 0;
    }catch(const std::exception&e){p->error=e.what();return -1;}}
extern "C" int laplace_program_sequence(laplace_synthesis_program*p,const int*tokens,size_t count,double weight){if(!p||p->compiled||!tokens)return -1;try{
    require(std::isfinite(weight)&&weight>0,"invalid sequence observation weight");int parent=0;
    for(size_t x=0;x<count;x++){int token=tokens[x];require(token>=0&&(size_t)token<p->n,"sequence token outside export vocabulary");
        if(x)p->observed[{parent,token}]+=weight;auto key=std::make_pair(parent,token);auto it=p->prefixes.find(key);
        if(it==p->prefixes.end()){int node=(int)p->tokens.size();p->prefixes[key]=node;p->tokens.push_back(token);p->parents.push_back(parent);p->positions.push_back(p->positions[parent]+1);parent=node;}else parent=it->second;
    }if(count)p->observed[{parent,p->eos}]+=weight;return 0;}catch(const std::exception&e){p->error=e.what();return -1;}}
extern "C" int laplace_program_compile(laplace_synthesis_program*p,int readout,const int*input,const int*emit,laplace_synthesis_emit sink,laplace_synthesis_trace trace,void*user){if(!p||!sink||p->compiled)return -1;try{
    p->compiled=true;require(p->layers.size()==p->expected,"declared layer schedule incomplete");require(readout>=0&&(size_t)readout<p->planes.size()&&input&&emit,"missing output operator or realization maps");
    size_t n=p->n,d=p->d,m=p->tokens.size();Vec embedding=std::move(p->states),states(product(m,d));
    for(size_t x=0;x<m;x++)std::copy_n(embedding.data()+(size_t)p->tokens[x]*d,d,states.data()+x*d);
    auto send=[&](const std::string&hf,const std::string&gg,const std::vector<size_t>&shape,const Vec&v){finite(v);require(sink(user,hf.empty()?nullptr:hf.c_str(),gg.empty()?nullptr:gg.c_str(),shape.data(),shape.size(),v.data(),v.size())==0,"tensor sink refused output");};
    // Canonical identity remains separate from tokenizer realization. Collapse
    // duplicate realization pairs by their mean, then split each destination's
    // mass across its realizations so adding an alias cannot add evidence.
    std::vector<std::vector<int>> aliases(n);
    for(size_t x=0;x<n;x++){require(input[x]>=0&&(size_t)input[x]<n,"invalid input operand map");require(input[input[x]]==input[x],"input operand map must resolve to canonical representatives");aliases[input[x]].push_back((int)x);}
    std::vector<Plane> expanded;
    for(const auto &raw:p->planes){
        std::map<std::pair<int,int>,double> literal;
        for(size_t a=0;a<raw.rows.size();a++)literal[{raw.rows[a],raw.cols[a]}]+=raw.weights[a];
        std::map<std::pair<int,int>,std::pair<double,size_t>> canonical;
        for(const auto &a:literal){auto &v=canonical[{input[a.first.first],input[a.first.second]}];v.first+=a.second;++v.second;}
        Plane plane;
        for(const auto &a:canonical){const auto &src=aliases[a.first.first],&dst=aliases[a.first.second];
            double weight=a.second.first/a.second.second/dst.size();
            for(int x:src)for(int y:dst){plane.rows.push_back(x);plane.cols.push_back(y);plane.weights.push_back(weight);}}
        expanded.push_back(std::move(plane));
    }
    send("model.embed_tokens.weight","token_embd.weight",{n,d},embedding);Vec ones(d,1);
    for(size_t li=0;li<p->layers.size();li++){
        const Layer&l=p->layers[li];size_t hd=d/l.heads,qdim=l.heads*hd,kdim=l.kv*hd;
        std::string hf="model.layers."+std::to_string(li)+".",gg="blk."+std::to_string(li)+".";
        Vec norm=normalized(states,m,d,p->eps);std::vector<double> basis(n*d);for(size_t x=0;x<n;x++)for(size_t c=0;c<d;c++)basis[x*d+c]=norm[(x+1)*d+c];
        std::map<int,std::pair<Factors,Factors>> factors;
        for(int op:l.operators)if(op>=0&&!factors.count(op)){
            auto &plane=expanded[op];std::vector<double> score(d*d),value(d*d);size_t rank=0;double se=0,ve=0;
            int rc=laplace_operator_compile(basis.data(),n,d,plane.rows.data(),plane.cols.data(),plane.weights.data(),plane.rows.size(),score.data(),value.data(),&rank,&se,&ve);
            require(rc==0,"attention operator lowering failed");for(size_t c=0;c<d;c++)value[c*d+c]-=1;
            size_t copies=std::count(l.operators.begin(),l.operators.end(),op);
            factors[op]={factor(score,d,copies*(hd),false),factor(value,d,copies*hd,true)};
            if(trace)trace(user,li,"attention",rank,se,ve);
        }
        std::vector<Factors> qf(l.heads),vf(l.heads);std::map<int,size_t> ordinal;
        for(size_t h=0;h<l.heads;h++){
            if(l.operators[h]==-2){qf[h].d=vf[h].d=d;if(trace)trace(user,li,"disabled-head",h,0,0);continue;}
            int op=l.operators[h];if(op<0){Factors f;f.d=d;f.k=hd;f.left.resize(hd*d,0);f.right.resize(hd*d,0);
                for(size_t z=0;z<hd;z++)f.left[z*d+(h*hd+z)%d]=f.right[z*d+(h*hd+z)%d]=1;qf[h]=vf[h]=std::move(f);continue;}
            size_t index=ordinal[op]++,copies=std::count(l.operators.begin(),l.operators.end(),op);
            qf[h]=slice(factors[op].first,index,copies,hd);vf[h]=slice(factors[op].second,index,copies,hd);
        }
        double split=std::pow((double)p->expected,-0.25);float qs=(float)(p->attn_gain*split),vs=(float)(p->resid_gain*split);
        Vec wq(qdim*d,0),wk(kdim*d,0),wv(kdim*d,0),wo(d*qdim,0);
        for(size_t g=0;g<l.kv;g++){
            size_t first=g*(l.heads/l.kv),count=l.heads/l.kv;
            if(count==1){size_t h=first;const auto &a=qf[h];const auto &v=vf[h];
                for(size_t z=0;z<a.k&&z<hd;z++){size_t channel=z;for(size_t c=0;c<d;c++){
                    wq[(h*hd+channel)*d+c]=qs*a.left[z*d+c];wk[(g*hd+channel)*d+c]=qs*a.right[z*d+c];}}
                for(size_t z=0;z<v.k;z++)for(size_t c=0;c<d;c++){wv[(g*hd+z)*d+c]=vs*v.right[z*d+c];wo[c*qdim+h*hd+z]=vs*v.left[z*d+c];}
            }else{
                Vec commonk=shared_basis(qf,first,count,hd,d),commonv=shared_basis(vf,first,count,hd,d);
                for(size_t z=0;z<hd;z++){size_t channel=z;for(size_t c=0;c<d;c++)wk[(g*hd+channel)*d+c]=qs*commonk[z*d+c];}
                for(size_t z=0;z<hd;z++)for(size_t c=0;c<d;c++)wv[(g*hd+z)*d+c]=vs*commonv[z*d+c];
                for(size_t h=first;h<first+count;h++){
                    Vec q=project_left(qf[h],commonk,hd),o=project_left(vf[h],commonv,hd);
                    for(size_t z=0;z<hd;z++){size_t channel=z;for(size_t c=0;c<d;c++)wq[(h*hd+channel)*d+c]=qs*q[c*(hd)+z];}
                    for(size_t z=0;z<hd;z++)for(size_t c=0;c<d;c++)wo[c*qdim+h*hd+z]=vs*o[c*hd+z];
                }
            }
        }
        send(hf+"self_attn.q_proj.weight",gg+"attn_q.weight",{qdim,d},wq);send(hf+"self_attn.k_proj.weight",gg+"attn_k.weight",{kdim,d},wk);
        send(hf+"self_attn.v_proj.weight",gg+"attn_v.weight",{kdim,d},wv);send(hf+"self_attn.o_proj.weight",gg+"attn_output.weight",{d,qdim},wo);
        send(hf+"input_layernorm.weight",gg+"attn_norm.weight",{d},ones);
        Vec q,k,v,attended(m*qdim,0),delta;multiply(norm,wq,q,m,qdim,d);multiply(norm,wk,k,m,kdim,d);multiply(norm,wv,v,m,kdim,d);
        rope(q,m,l.heads,hd,p->positions,p->rope_base,p->rope_scale);rope(k,m,l.kv,hd,p->positions,p->rope_base,p->rope_scale);
        for(size_t x=0;x<m;x++)for(size_t h=0;h<l.heads;h++){
            size_t kh=h/(l.heads/l.kv);std::vector<std::pair<int,double>> weights;double maximum=-INFINITY;
            for(int node=(int)x;node>=0;node=p->parents[node]){double dot=0;for(size_t c=0;c<hd;c++)dot+=(double)q[x*qdim+h*hd+c]*k[(size_t)node*kdim+kh*hd+c];
                dot/=std::sqrt((double)hd);weights.push_back({node,dot});maximum=std::max(maximum,dot);}
            double sum=0;for(auto &item:weights){item.second=std::exp(item.second-maximum);sum+=item.second;}
            for(auto &item:weights)for(size_t c=0;c<hd;c++)attended[x*qdim+h*hd+c]+=(float)(item.second/sum*v[(size_t)item.first*kdim+kh*hd+c]);
        }
        multiply(attended,wo,delta,m,d,qdim);for(size_t i=0;i<states.size();i++)states[i]+=delta[i];finite(states);
        norm=normalized(states,m,d,p->eps);for(size_t x=0;x<n;x++)for(size_t c=0;c<d;c++)basis[x*d+c]=norm[(x+1)*d+c];
        size_t experts=std::max((size_t)1,l.experts);std::vector<Vec> gates(experts),ups(experts),downs(experts);Vec router(experts*d,0),result(states.size(),0);
        std::vector<double> constant(d);
        const Plane empty_plane;
        std::map<int,Factors> expert_factors;std::map<int,size_t> expert_ordinal;
        for(size_t e=0;e<experts;e++){
            int op=l.experts?l.expert_ops[e]:l.ffn_op;const auto &plane=op==-2?empty_plane:expanded[op];std::vector<double> score(d*d),value(d*d);size_t rank=0;double se=0,ve=0;
            if(op==-2){expert_factors[op]=Factors{d,0,{},{}};if(trace)trace(user,li,"disabled-ffn",e,0,0);}
            else if(!expert_factors.count(op)){
                int rc=laplace_operator_compile_constant(basis.data(),n,d,plane.rows.data(),plane.cols.data(),plane.weights.data(),plane.rows.size(),score.data(),value.data(),&rank,&se,&ve,constant.data());
                require(rc==0,"FFN operator lowering failed");for(size_t c=0;c<d;c++)value[c*d+c]-=1;
                expert_factors[op]=factor(value,d,d,true);
                if(trace)trace(user,li,"ffn",rank,se,ve);
            }
            size_t copies=l.experts?std::count(l.expert_ops.begin(),l.expert_ops.end(),op):1;
            size_t ordinal=expert_ordinal[op]++;
            Factors f=slice(expert_factors[op],ordinal,copies,l.ffn);gates[e].assign(l.ffn*d,0);ups[e].assign(l.ffn*d,0);downs[e].assign(d*l.ffn,0);
            // Construct the constant gate direction in this layer's actual
            // normalized basis. A fixed last-coordinate assumption ceases to be
            // constant after residual updates and can explode in deep programs.
            // Evidence directions remain in the gate; their scale bounds the
            // modulation to +/-0.5 on every canonical construction state.
            double bias_error=0;
            for(size_t x=0;x<n;x++){double b=0;for(size_t c=0;c<d;c++)b+=basis[x*d+c]*constant[c];bias_error=std::max(bias_error,std::abs(b-1));}
            Vec activation;multiply(norm,f.right,activation,m,f.k,d);
            for(size_t z=0;z<f.k;z++){
                double maximum=0,squared=0;for(size_t c=0;c<d;c++)squared+=(double)f.right[z*d+c]*f.right[z*d+c];
                for(size_t x=0;x<n;x++)maximum=std::max(maximum,(double)std::abs(activation[(x+1)*f.k+z]));
                double resolution=d*std::numeric_limits<float>::epsilon()*std::sqrt(d*squared);
                double gate_gain=maximum>resolution?0.5/maximum:0;
                for(size_t c=0;c<d;c++){
                    ups[e][z*d+c]=vs*f.right[z*d+c]/silu(6);
                    downs[e][c*l.ffn+z]=vs*f.left[z*d+c];
                    gates[e][z*d+c]=(float)(6*constant[c]+gate_gain*f.right[z*d+c]);
                }
            }
            if(trace)trace(user,li,"gate-constant",f.k,bias_error,0.5);
            std::vector<double> mass(n,0);for(size_t a=0;a<plane.rows.size();a++)mass[plane.rows[a]]+=std::abs(plane.weights[a]);
            for(size_t x=0;x<n;x++){
                double energy=0;for(size_t z=0;z<f.k;z++){double value=activation[(x+1)*f.k+z];energy+=value*value;}
                for(size_t c=0;c<d;c++)router[e*d+c]+=(float)(mass[x]*energy*basis[x*d+c]);}
            double ss=0;for(size_t c=0;c<d;c++)ss+=(double)router[e*d+c]*router[e*d+c];float inv=ss>0?(float)(1/std::sqrt(ss)):0;
            for(size_t c=0;c<d;c++)router[e*d+c]*=inv;
            std::string name=l.experts?hf+"block_sparse_moe.experts."+std::to_string(e)+".":hf+"mlp.";
            send(name+(l.experts?"w1.weight":"gate_proj.weight"),l.experts?"":gg+"ffn_gate.weight",{l.ffn,d},gates[e]);
            send(name+(l.experts?"w3.weight":"up_proj.weight"),l.experts?"":gg+"ffn_up.weight",{l.ffn,d},ups[e]);
            send(name+(l.experts?"w2.weight":"down_proj.weight"),l.experts?"":gg+"ffn_down.weight",{d,l.ffn},downs[e]);
        }
        if(l.experts)for(size_t c=0;c<d;c++){
            double mean=0;for(size_t e=0;e<experts;e++)mean+=router[e*d+c];mean/=experts;
            for(size_t e=0;e<experts;e++)router[e*d+c]=(float)(router[e*d+c]-mean);
        }
        Vec routing;if(l.experts){send(hf+"block_sparse_moe.gate.weight",gg+"ffn_gate_inp.weight",{experts,d},router);multiply(norm,router,routing,m,experts,d);
            for(int kind=0;kind<3;kind++){Vec packed;for(size_t e=0;e<experts;e++){const auto &data=kind==0?gates[e]:kind==1?ups[e]:downs[e];packed.insert(packed.end(),data.begin(),data.end());}
                send("",gg+(kind==0?"ffn_gate_exps.weight":kind==1?"ffn_up_exps.weight":"ffn_down_exps.weight"),kind==2?std::vector<size_t>{experts,d,l.ffn}:std::vector<size_t>{experts,l.ffn,d},packed);}}
        std::vector<std::vector<float>> routes(m,std::vector<float>(experts,0));
        for(size_t x=0;x<m;x++){
            if(!l.experts){routes[x][0]=1;continue;}std::vector<size_t> order(experts);std::iota(order.begin(),order.end(),0);
            // The consumer ranks rounded full-softmax probabilities, then
            // normalizes the selected weights. Ranking raw logits is algebraically
            // equivalent over reals, but changes F32 ties at expert boundaries.
            float maximum=*std::max_element(routing.begin()+x*experts,routing.begin()+(x+1)*experts);
            std::vector<float> probabilities(experts);double sum=0;
            for(size_t e=0;e<experts;e++){probabilities[e]=std::exp(routing[x*experts+e]-maximum);sum+=probabilities[e];}
            float inv=(float)(1/sum);for(float &v:probabilities)v*=inv;
            std::sort(order.begin(),order.end(),[&](size_t a,size_t b){return probabilities[a]>probabilities[b];});
            float selected=0;for(size_t z=0;z<l.used;z++)selected+=probabilities[order[z]];
            for(size_t z=0;z<l.used;z++)routes[x][order[z]]=probabilities[order[z]]/selected;
        }
        for(size_t e=0;e<experts;e++){Vec up,gate,down;multiply(norm,ups[e],up,m,l.ffn,d);multiply(norm,gates[e],gate,m,l.ffn,d);
            for(size_t i=0;i<up.size();i++)up[i]*=silu(gate[i]);multiply(up,downs[e],down,m,d,l.ffn);
            for(size_t x=0;x<m;x++)for(size_t c=0;c<d;c++)result[x*d+c]+=routes[x][e]*down[x*d+c];}
        for(size_t i=0;i<states.size();i++)states[i]+=result[i];finite(states);send(hf+"post_attention_layernorm.weight",gg+"ffn_norm.weight",{d},ones);
        if(trace)trace(user,li,"executed",m,0,0);
    }
    p->final_states=normalized(states,m,d,p->eps);send("model.norm.weight","output_norm.weight",{d},ones);
    const Plane&pl=p->planes[readout];Vec head(n*d);size_t rank=0;double error=0,accuracy=0;
    // Exact complete-layer states are now the operands for the output solve.
    int rc=0;
    if(p->tied)head=embedding;
    {
        auto canonical=laplace::synthesis::transfer_targets(n,p->eos,input,emit,pl.rows.data(),pl.cols.data(),pl.weights.data(),pl.rows.size(),p->outside_bound,p->equal_candidates);
        std::vector<size_t> sample_nodes;for(size_t x=1;x<=n;x++)sample_nodes.push_back(x);
        std::map<int,std::map<int,double>> observations;
        if(p->observation_readout)for(const auto &entry:p->observed){int y=emit[input[entry.first.second]];if(y<0)y=entry.first.second;observations[entry.first.first][y]+=entry.second;}
        if(p->observation_readout){for(const auto &row:observations)if((size_t)row.first>n)sample_nodes.push_back(row.first);}
        else for(size_t node=n+1;node<m;node++)sample_nodes.push_back(node);
        size_t samples=sample_nodes.size();Vec actual(samples*d);std::vector<laplace::synthesis::TargetRow> targets(samples);
        for(size_t row=0;row<samples;row++){
            size_t node=sample_nodes[row];std::copy_n(p->final_states.data()+node*d,d,actual.data()+row*d);
            auto found=observations.find((int)node);
            if(found==observations.end())targets[row]=canonical[p->tokens[node]];
            else {
                double total=0,floor=0;for(const auto &item:found->second)total+=item.second;
                for(const auto &item:found->second)floor=std::min(floor,std::log(item.second/total*n));
                targets[row].floor=floor-1;
                for(const auto &item:found->second)targets[row].delta[item.first]=std::log(item.second/total*n)-targets[row].floor;
                if(p->outside_bound>0)laplace::synthesis::bound_target(targets[row],n,p->outside_bound,p->equal_candidates);
            }
        }
        laplace::synthesis::SupportMeasure support;
        rc=laplace::synthesis::fit_targets(actual.data(),samples,n,d,targets,head.data(),&rank,&error,&accuracy,!p->tied,&support,p->outside_bound);
        p->target_nodes=sample_nodes;p->target_offsets.push_back(0);
        for(const auto&row:targets){p->target_floors.push_back(row.floor);for(const auto&entry:row.delta){p->target_columns.push_back(entry.first);p->target_deltas.push_back(entry.second);}p->target_offsets.push_back(p->target_columns.size());}
        if(trace){trace(user,p->layers.size(),"candidate-outside-mass",support.rows,support.mean_outside_mass,support.max_outside_mass);
            if(p->outside_bound>0)trace(user,p->layers.size(),"candidate-bound-exceeded",support.exceeding_bound,p->outside_bound,0);
            if(!p->observed.empty())trace(user,p->layers.size(),p->observation_readout?"observed-completion-targets":"declared-operator-prefix-targets",samples,error,accuracy);}
    }
    require(rc==0,"native output construction failed");send("lm_head.weight","output.weight",{n,d},head);
    if(trace)trace(user,p->layers.size(),p->tied?"readout-tied":"readout",rank,error,accuracy);return 0;
    }catch(const std::exception&e){p->error=e.what();return -1;}}
extern "C" const char *laplace_program_error(const laplace_synthesis_program*p){return p?p->error.c_str():"native program creation failed";}
extern "C" const float *laplace_program_states(const laplace_synthesis_program*p,size_t*rows,size_t*width){if(!p||!rows||!width)return nullptr;*rows=p->tokens.size();*width=p->d;return p->final_states.data();}
extern "C" size_t laplace_program_nodes(const laplace_synthesis_program*p,const int**tokens,const int**parents,const int**positions){if(!p||!tokens||!parents||!positions)return 0;*tokens=p->tokens.data();*parents=p->parents.data();*positions=p->positions.data();return p->tokens.size();}
extern "C" size_t laplace_program_targets(const laplace_synthesis_program*p,const size_t**nodes,const double**floors,const size_t**offsets,const int**columns,const double**deltas){if(!p||!nodes||!floors||!offsets||!columns||!deltas)return 0;*nodes=p->target_nodes.data();*floors=p->target_floors.data();*offsets=p->target_offsets.data();*columns=p->target_columns.data();*deltas=p->target_deltas.data();return p->target_nodes.size();}
extern "C" void laplace_program_free(laplace_synthesis_program*p){delete p;}
