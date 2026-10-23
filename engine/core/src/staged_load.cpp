#include "laplace/core/staged_load.h"
#include "laplace/core/text_identity.h"
#include "laplace/highway.h"
#include "laplace/tier0.h"

#include <algorithm>
#include <cstring>
#include <limits>
#include <new>
#include <stdexcept>
#include <vector>

namespace {
enum State : uint8_t { unseen, pending, novel, recorded };
struct Node {
    lp_ref value{};
    size_t first = 0, count = 0;
    lp_mask entity_mask = 0, path_mask = 0;
    State state = unseen;
    bool spine = false, external = false;
};
struct Refused { canonical_stage_status_t status; };
void require(bool yes, canonical_stage_status_t status = CANONICAL_STAGE_INVALID) {
    if (!yes) throw Refused{status};
}
lp_ref native(const canonical_stage_ref_t& in) {
    lp_ref out{};
    std::memcpy(out.id.b, &in.id, 16);
    std::memcpy(out.c.m, in.coordinate, sizeof out.c.m);
    out.tier=in.tier; out.said=in.said; out.outcome=in.outcome;
    out.position=in.position; out.spare=in.spare;
    return out;
}
canonical_stage_ref_t exposed(const lp_ref& in) {
    canonical_stage_ref_t out{};
    std::memcpy(&out.id,in.id.b,16);
    std::memcpy(out.coordinate,in.c.m,sizeof out.coordinate);
    out.tier=in.tier; out.said=in.said; out.outcome=in.outcome;
    out.position=in.position; out.spare=in.spare;
    return out;
}
hash128_t exposed_id(const lp_id& in) {
    hash128_t out;std::memcpy(&out,in.b,16);return out;
}
lp_id native_id(const hash128_t& in) {
    lp_id out;std::memcpy(out.b,&in,16);return out;
}
bool same(const lp_vertex& a,const lp_vertex& b) {
    return lp_id_eq(&a.id,&b.id) && a.said==b.said && a.outcome==b.outcome
        && a.position==b.position && a.spare==b.spare;
}
void carrier(lp_mask value,uint8_t out[12]) {
    std::memset(out,0,12); out[3]=64;
    for(unsigned bit=0;bit<64;++bit)
        if(value&(UINT64_C(1)<<bit)) out[4+(bit>>3)]|=uint8_t(0x80u>>(bit&7));
}
}

struct canonical_stage {
    const lp_tier0_record* floor;
    const lp_highway* highway;
    lp_idmap* index;
    size_t maximum_nodes, maximum_vertices, maximum_copy_bytes;
    canonical_stage_status_t error=CANONICAL_STAGE_OK;
    unsigned phase=0;
    std::vector<Node> nodes;
    std::vector<lp_vertex> vertices, row;
    std::vector<lp_ref> arguments;
    std::vector<hash128_t> front, previous;
    std::vector<size_t> stack, order;
    std::vector<uint8_t> entities, paths;
    explicit canonical_stage(size_t n,size_t v,size_t bytes,const lp_highway* h)
        : floor(static_cast<const lp_tier0_record*>(text_identity_scalar_floor())),
          highway(h),index(lp_idmap_new()),maximum_nodes(n),maximum_vertices(v),
          maximum_copy_bytes(bytes) {}
    ~canonical_stage() { lp_idmap_free(index); }
};

namespace {
template<class F> canonical_stage_status_t guarded(canonical_stage* s,F work) {
    if(!s) return CANONICAL_STAGE_INVALID;
    if(s->error!=CANONICAL_STAGE_OK) return s->error;
    try { work(); return CANONICAL_STAGE_OK; }
    catch(const Refused& failure) { return s->error=failure.status; }
    catch(const std::bad_alloc&) { return s->error=CANONICAL_STAGE_LIMIT; }
    catch(const std::length_error&) { return s->error=CANONICAL_STAGE_LIMIT; }
}
bool floor_atom(canonical_stage* s,const lp_id& id) {
    // Only Unicode scalars are storage-free leaves. A highway entry supplies
    // identity and coordinates, not a receipt for its persisted logical path.
    return lp_tier0_contains(s->floor,&id);
}
void validate(canonical_stage* s,const lp_ref& ref) {
    require(lp_coord_inside(&ref.c) && ref.said<=LP_M_SAID_MASK && ref.outcome<=LP_OUTCOME_SCORE
        && ref.position<=LP_M_POSITION_MAX && ref.spare<(UINT32_C(1)<<LP_SPARE_BITS));
    const lp_tier0_record* known=nullptr;
    int64_t cp=lp_tier0_codepoint(s->floor,&ref.id);
    if(cp>=0) known=lp_tier0_at(s->floor,uint32_t(cp));
    else if(s->highway) known=lp_highway_find(s->highway,&ref.id);
    if(known) require(std::memcmp(known->m,ref.c.m,sizeof ref.c.m)==0,CANONICAL_STAGE_CONFLICT);
    else {
        int64_t at=lp_idmap_find(s->index,&ref.id);
        require(at>=0);
        require(std::memcmp(s->nodes[size_t(at)].value.c.m,ref.c.m,sizeof ref.c.m)==0,
            CANONICAL_STAGE_CONFLICT);
    }
}
size_t insert(canonical_stage* s,const lp_ref& value) {
    require(s->nodes.size()<s->maximum_nodes,CANONICAL_STAGE_LIMIT);
    size_t at=lp_idmap_put(s->index,&value.id,nullptr);
    require(at==s->nodes.size(),CANONICAL_STAGE_CONFLICT);
    Node node; node.value=value; node.value.said=node.value.outcome=0;
    node.value.position=node.value.spare=0;
    s->nodes.push_back(node);
    return at;
}
lp_ref compose(canonical_stage* s,const lp_ref* children,size_t count,uint8_t tier,
    lp_mask entity_mask,lp_mask path_mask,bool spine) {
    require(s->phase==0,CANONICAL_STAGE_STATE);
    require(children && count && count<=UINT32_MAX);
    for(size_t i=0;i<count;++i) validate(s,children[i]);
    require(count==1 || tier>=lp_tier_above(children,count));
    lp_ref value=lp_ref_compose(children,count,tier);
    value.said=value.outcome=0; value.position=value.spare=0;
    lp_mask checked;
    require(s->highway || (!entity_mask && !path_mask));
    if(s->highway) {
        require(lp_mask_merge(s->highway,0,entity_mask,&checked));
        require(lp_mask_merge(s->highway,0,path_mask,&checked));
    }
    if(count==1 || floor_atom(s,value.id)) {
        require(!entity_mask && !path_mask && !spine);
        validate(s,value);
        return value;
    }
    require(value.tier>0 && lp_coord_inside(&value.c));
    s->row.clear();
    for(size_t i=0;i<count;++i) {
        const lp_ref& child=children[i];
        lp_vertex vertex{child.id,1,child.said,child.spare,child.outcome,child.position};
        if(!s->row.empty() && same(s->row.back(),vertex)
            && s->row.back().run<((UINT32_C(1)<<LP_M_RUN_BITS)-1)) ++s->row.back().run;
        else s->row.push_back(vertex);
    }
    int64_t found=lp_idmap_find(s->index,&value.id);
    size_t at;
    if(found<0) {
        require(s->row.size()<=s->maximum_vertices-s->vertices.size(),CANONICAL_STAGE_LIMIT);
        at=insert(s,value);
        s->nodes[at].first=s->vertices.size(); s->nodes[at].count=s->row.size();
        s->vertices.insert(s->vertices.end(),s->row.begin(),s->row.end());
    } else {
        at=size_t(found); Node& node=s->nodes[at];
        require(std::memcmp(node.value.c.m,value.c.m,sizeof value.c.m)==0,CANONICAL_STAGE_CONFLICT);
        if(!node.external) {
            require(node.count==s->row.size(),CANONICAL_STAGE_CONFLICT);
            for(size_t i=0;i<node.count;++i) {
                const lp_vertex& old=s->vertices[node.first+i];
                require(same(old,s->row[i]) && old.run==s->row[i].run,CANONICAL_STAGE_CONFLICT);
            }
        } else require(!entity_mask && !path_mask,CANONICAL_STAGE_STATE);
        node.value.tier=std::min(node.value.tier,value.tier);
    }
    Node& node=s->nodes[at];
    if(s->highway) {
        require(lp_mask_merge(s->highway,node.entity_mask,entity_mask,&node.entity_mask),CANONICAL_STAGE_CONFLICT);
        require(lp_mask_merge(s->highway,node.path_mask,path_mask,&node.path_mask),CANONICAL_STAGE_CONFLICT);
    }
    node.spine|=spine;
    return node.value;
}
lp_ref text_composition(void* owner,const lp_ref* children,uint32_t n,uint8_t tier) {
    auto* s=static_cast<canonical_stage*>(owner);
    lp_ref out=lp_ref_compose(children,n,tier);
    if(s->error==CANONICAL_STAGE_OK)
        guarded(s,[&]{out=compose(s,children,n,tier,0,0,false);});
    return out;
}
void queue(canonical_stage* s,const lp_id& id) {
    int64_t at=lp_idmap_find(s->index,&id);
    if(at<0) { require(floor_atom(s,id)); return; }
    s->stack.push_back(size_t(at));
    while(!s->stack.empty()) {
        size_t i=s->stack.back();s->stack.pop_back();Node& node=s->nodes[i];
        if(node.state!=unseen) continue;
        if(node.external) {node.state=recorded;continue;}
        if(!node.spine) {node.state=pending;s->front.push_back(exposed_id(node.value.id));continue;}
        node.state=novel;
        for(size_t v=0;v<node.count;++v) {
            const lp_id child=s->vertices[node.first+v].id;
            int64_t child_at=lp_idmap_find(s->index,&child);
            if(child_at>=0)s->stack.push_back(size_t(child_at)); else require(floor_atom(s,child));
        }
    }
}
void known(canonical_stage* s,size_t at) {
    s->stack.push_back(at);
    while(!s->stack.empty()) {
        size_t i=s->stack.back();s->stack.pop_back();Node& node=s->nodes[i];
        if(node.state==recorded)continue;
        node.state=recorded;
        for(size_t v=0;v<node.count;++v) {
            int64_t child=lp_idmap_find(s->index,&s->vertices[node.first+v].id);
            if(child>=0)s->stack.push_back(size_t(child));
        }
    }
}
int output(void* context,const uint8_t* data,size_t bytes) {
    auto* buffer=static_cast<std::vector<uint8_t>*>(context);
    try {buffer->insert(buffer->end(),data,data+bytes);return 1;}
    catch(const std::bad_alloc&) {return 0;}
    catch(const std::length_error&) {return 0;}
}
struct Copy {
    lp_copy value{};
    ~Copy(){lp_buf_free(&value.buf);}
};
}

extern "C" canonical_stage_t* canonical_stage_new(size_t nodes,size_t vertices,size_t bytes,const void* highway) {
    if(!nodes || nodes>UINT32_MAX || !vertices || bytes<42 || !text_identity_scalar_floor())return nullptr;
    try {return new canonical_stage(nodes,vertices,bytes,static_cast<const lp_highway*>(highway));}
    catch(const std::bad_alloc&) {return nullptr;}
}
extern "C" void canonical_stage_free(canonical_stage_t* s){delete s;}
extern "C" void canonical_stage_reset(canonical_stage_t* s) {
    if(!s)return;
    lp_idmap_clear(s->index);s->nodes.clear();s->vertices.clear();s->front.clear();s->previous.clear();
    s->stack.clear();s->order.clear();s->entities.clear();s->paths.clear();s->error=CANONICAL_STAGE_OK;s->phase=0;
}
extern "C" canonical_stage_status_t canonical_stage_status(const canonical_stage_t* s) {
    return s?s->error:CANONICAL_STAGE_INVALID;
}
extern "C" canonical_stage_status_t canonical_stage_reference(canonical_stage_t* s,const canonical_stage_ref_t* input) {
    return guarded(s,[&]{
        require(input && s->phase==0,CANONICAL_STAGE_STATE);
        lp_ref value=native(*input);require(lp_coord_inside(&value.c));
        int64_t at=lp_idmap_find(s->index,&value.id);
        if(at>=0) {
            require(std::memcmp(s->nodes[size_t(at)].value.c.m,value.c.m,sizeof value.c.m)==0,CANONICAL_STAGE_CONFLICT);
            s->nodes[size_t(at)].external=true;
        } else if(floor_atom(s,value.id)) validate(s,value);
        else {
            require(value.tier>0);
            if(s->highway && lp_highway_contains(s->highway,&value.id))validate(s,value);
            s->nodes[insert(s,value)].external=true;
        }
    });
}
extern "C" canonical_stage_status_t canonical_stage_compose(canonical_stage_t* s,
    const canonical_stage_ref_t* children,size_t count,uint8_t tier,uint64_t em,uint64_t pm,int spine,
    canonical_stage_ref_t* out) {
    return guarded(s,[&]{
        require(out && children && count && count<=s->maximum_vertices);
        s->arguments.resize(count);
        for(size_t i=0;i<count;++i)s->arguments[i]=native(children[i]);
        lp_ref value=compose(s,s->arguments.data(),count,tier,em,pm,spine!=0);
        *out=exposed(value);
    });
}
extern "C" canonical_stage_status_t canonical_stage_text(canonical_stage_t* s,const uint8_t* bytes,size_t n,
    canonical_stage_ref_t* out) {
    return guarded(s,[&]{
        require(out && bytes && n && n<=INT32_MAX && s->phase==0);
        for(size_t at=0;at<n;) {
            size_t first=at;uint32_t cp;uint8_t utf8[4];
            require(lp_utf8_next(bytes,n,&at,&cp) && lp_unicode_scalar(cp));
            size_t width=lp_utf8_put(cp,utf8);
            require(at-first==width && std::memcmp(utf8,bytes+first,width)==0);
        }
        auto* context=static_cast<lp_text*>(text_identity_thread_context());require(context);
        lp_ref value=lp_text_decompose(context,bytes,n,text_composition,s);
        require(s->error==CANONICAL_STAGE_OK,s->error);
        *out=exposed(value);
    });
}
extern "C" canonical_stage_status_t canonical_stage_begin(canonical_stage_t* s,const hash128_t* roots,size_t n) {
    return guarded(s,[&]{
        require(s->phase==0,CANONICAL_STAGE_STATE);require(roots || !n);
        s->phase=1;
        for(size_t i=0;i<n;++i){lp_id id;std::memcpy(id.b,roots+i,16);queue(s,id);}
    });
}
extern "C" const hash128_t* canonical_stage_frontier(const canonical_stage_t* s,size_t* n) {
    if(n)*n=s && s->phase==1 && s->error==CANONICAL_STAGE_OK?s->front.size():0;
    return s && s->phase==1 && s->error==CANONICAL_STAGE_OK
        ?s->front.data():nullptr;
}
extern "C" canonical_stage_status_t canonical_stage_answer(canonical_stage_t* s,const uint8_t* present,size_t n) {
    return guarded(s,[&]{
        require(s->phase==1 && n==s->front.size() && n && present,CANONICAL_STAGE_STATE);
        for(size_t i=0;i<n;++i)require(present[i]<=1);
        s->previous.clear();s->previous.swap(s->front);
        for(size_t i=0;i<n;++i)if(present[i]) {
            lp_id id=native_id(s->previous[i]);known(s,size_t(lp_idmap_find(s->index,&id)));
        }
        for(size_t i=0;i<n;++i)if(!present[i]) {
            lp_id id=native_id(s->previous[i]);
            Node& node=s->nodes[size_t(lp_idmap_find(s->index,&id))];
            if(node.state==recorded)continue;
            node.state=novel;
            for(size_t v=0;v<node.count;++v)queue(s,s->vertices[node.first+v].id);
        }
        s->front.erase(std::remove_if(s->front.begin(),s->front.end(),[&](const hash128_t& value){
            lp_id id=native_id(value);return s->nodes[size_t(lp_idmap_find(s->index,&id))].state!=pending;
        }),s->front.end());
    });
}
extern "C" canonical_stage_status_t canonical_stage_finish(canonical_stage_t* s) {
    return guarded(s,[&]{
        require(s->phase==1 && s->front.empty(),CANONICAL_STAGE_STATE);
        size_t bytes=42;
        for(size_t i=0;i<s->nodes.size();++i) {
            const Node& node=s->nodes[i];
            if(node.external || !(node.state==novel || (node.state==recorded && (node.entity_mask || node.path_mask))))continue;
            require(node.count<=(UINT32_MAX-9u)/32u,CANONICAL_STAGE_LIMIT);
            require(bytes<=s->maximum_copy_bytes && node.count<=(s->maximum_copy_bytes-bytes)/32u,CANONICAL_STAGE_LIMIT);
            bytes+=node.count*32u;
            const size_t fixed=node.count==1?162u:166u;
            require(fixed<=s->maximum_copy_bytes-bytes,CANONICAL_STAGE_LIMIT);bytes+=fixed;
            s->order.push_back(i);
        }
        std::sort(s->order.begin(),s->order.end(),[&](size_t a,size_t b){
            uint64_t x=lp_hilbert4(&s->nodes[a].value.c),y=lp_hilbert4(&s->nodes[b].value.c);
            return x==y?std::memcmp(s->nodes[a].value.id.b,s->nodes[b].value.id.b,16)<0:x<y;
        });
        Copy entity,path;
        lp_copy_start(&entity.value,SIZE_MAX,output,&s->entities);
        lp_copy_start(&path.value,SIZE_MAX,output,&s->paths);
        std::vector<lp_id> ids;std::vector<uint64_t> runs;std::vector<uint32_t> spare;std::vector<uint8_t> geometry;
        for(size_t i:s->order) {
            const Node& node=s->nodes[i];
            uint8_t point[37],mask[12];
            require(lp_ewkb_coord(&node.value.c,point,sizeof point)==sizeof point);
            lp_copy_row(&entity.value,5);lp_copy_id(&entity.value,&node.value.id);lp_copy_i16(&entity.value,node.value.tier);
            lp_copy_field(&entity.value,point,sizeof point);
            lp_copy_i64(&entity.value,lp_hilbert_key(lp_hilbert4(&node.value.c)));
            carrier(node.entity_mask,mask);lp_copy_field(&entity.value,mask,sizeof mask);
            ids.resize(node.count);runs.resize(node.count);spare.resize(node.count);
            for(size_t v=0;v<node.count;++v) {
                const lp_vertex& x=s->vertices[node.first+v];ids[v]=x.id;
                runs[v]=lp_m_full(x.run,x.said,x.outcome,x.position);spare[v]=x.spare;
            }
            size_t size=lp_ewkb_runs_spare(ids.data(),runs.data(),spare.data(),node.count,nullptr,0);
            require(size && size<=UINT32_MAX);geometry.resize(size);
            require(lp_ewkb_runs_spare(ids.data(),runs.data(),spare.data(),node.count,geometry.data(),size)==size);
            lp_copy_row(&path.value,5);lp_copy_id(&path.value,&node.value.id);lp_copy_i16(&path.value,node.value.tier);
            lp_copy_i64(&path.value,lp_hilbert_key(lp_hilbert4(&node.value.c)));
            lp_copy_field(&path.value,geometry.data(),uint32_t(size));
            carrier(node.path_mask,mask);lp_copy_field(&path.value,mask,sizeof mask);
        }
        require(lp_copy_end(&entity.value) && lp_copy_end(&path.value),CANONICAL_STAGE_LIMIT);
        require(s->entities.size()+s->paths.size()==bytes,CANONICAL_STAGE_CONFLICT);
        s->phase=2;
    });
}
extern "C" const uint8_t* canonical_stage_copy(const canonical_stage_t* s,canonical_stage_table_t table,size_t* bytes,size_t* rows) {
    if(bytes)*bytes=0;if(rows)*rows=0;
    if(!s || s->phase!=2 || s->error!=CANONICAL_STAGE_OK)return nullptr;
    const std::vector<uint8_t>* buffer=table==CANONICAL_STAGE_ENTITY?&s->entities
        :table==CANONICAL_STAGE_PHYSICALITY?&s->paths:nullptr;
    if(!buffer)return nullptr;
    if(bytes)*bytes=buffer->size();if(rows)*rows=s->order.size();return buffer->data();
}
extern "C" const char* canonical_stage_columns(canonical_stage_table_t table) {
    if(table==CANONICAL_STAGE_ENTITY)return "id,tier,coord,hilbert,mask";
    if(table==CANONICAL_STAGE_PHYSICALITY)return "entity,tier,hilbert,path,mask";
    return nullptr;
}
