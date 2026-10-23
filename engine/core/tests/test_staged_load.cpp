#include <gtest/gtest.h>
#include "laplace/core/staged_load.h"
#include "laplace/core/text_identity.h"
#include "laplace/highway.h"
#include "laplace/tier0.h"

#include <array>
#include <cstring>
#include <map>
#include <memory>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>
#ifdef _WIN32
#include <process.h>
#else
#include <unistd.h>
#endif

namespace {
using Stage=std::unique_ptr<canonical_stage_t,decltype(&canonical_stage_free)>;
using Field=std::vector<uint8_t>;
using Row=std::array<Field,5>;
Stage stage(size_t bytes=1u<<20,const lp_highway* highway=nullptr,size_t nodes=10000) {
    return Stage(canonical_stage_new(nodes,100000,bytes,highway),canonical_stage_free);
}
uint64_t be(const uint8_t* p,size_t n) {
    uint64_t value=0;for(size_t i=0;i<n;++i)value=(value<<8)|p[i];return value;
}
std::string key(const hash128_t& id){return std::string(reinterpret_cast<const char*>(&id),16);}
hash128_t id(const Field& bytes) {
    if(bytes.size()!=16)throw std::runtime_error("ID width");
    hash128_t out;std::memcpy(&out,bytes.data(),16);return out;
}
canonical_stage_ref_t text(canonical_stage_t* s,const std::string& value) {
    canonical_stage_ref_t out{};
    EXPECT_EQ(CANONICAL_STAGE_OK,canonical_stage_text(s,reinterpret_cast<const uint8_t*>(value.data()),value.size(),&out));
    return out;
}
void novel(canonical_stage_t* s) {
    for(unsigned round=0;round<100;++round) {
        size_t n;const hash128_t* front=canonical_stage_frontier(s,&n);
        if(!n)return;
        ASSERT_NE(nullptr,front);
        std::set<std::string> distinct;
        for(size_t i=0;i<n;++i)ASSERT_TRUE(distinct.insert(key(front[i])).second);
        std::vector<uint8_t> absent(n);
        ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_answer(s,absent.data(),n));
    }
    FAIL()<<"The finite graph did not drain";
}
std::vector<Row> rows(canonical_stage_t* s,canonical_stage_table_t table) {
    size_t size,n;const uint8_t* bytes=canonical_stage_copy(s,table,&size,&n);
    if(!bytes || size<21 || std::memcmp(bytes,"PGCOPY\n\377\r\n\0",11)
        || be(bytes+11,8)!=0)throw std::runtime_error("COPY header");
    std::vector<Row> out;size_t at=19;
    while(at+2<=size && be(bytes+at,2)!=65535) {
        if(be(bytes+at,2)!=5)throw std::runtime_error("canonical column count");
        at+=2;Row row;
        for(auto& field:row) {
            if(at+4>size)throw std::runtime_error("COPY field header");
            size_t length=size_t(be(bytes+at,4));at+=4;
            if(length>size-at)throw std::runtime_error("COPY field body");
            field.assign(bytes+at,bytes+at+length);at+=length;
        }
        out.push_back(std::move(row));
    }
    EXPECT_EQ(size,at+2);EXPECT_EQ(65535u,be(bytes+at,2));EXPECT_EQ(n,out.size());return out;
}
using Paths=std::map<std::string,std::vector<lp_vertex>>;
Paths paths(const std::vector<Row>& rows) {
    Paths result;
    for(const Row& row:rows) {
        size_t nv=lp_path_vertices(row[3].data(),row[3].size(),nullptr,0);
        std::vector<lp_vertex> vertices(nv);
        EXPECT_EQ(nv,lp_path_vertices(row[3].data(),row[3].size(),vertices.data(),nv));
        EXPECT_TRUE(result.emplace(key(id(row[0])),std::move(vertices)).second);
    }
    return result;
}
std::string realize(const std::string& node,const Paths& graph,unsigned depth=0) {
    if(depth>100)throw std::runtime_error("cyclic COPY graph");
    lp_id identity;std::memcpy(identity.b,node.data(),16);
    auto* floor=static_cast<const lp_tier0_record*>(text_identity_scalar_floor());
    int64_t cp=lp_tier0_codepoint(floor,&identity);
    if(cp>=0) {uint8_t bytes[4];size_t n=lp_utf8_put(uint32_t(cp),bytes);return std::string(reinterpret_cast<char*>(bytes),n);}
    std::string out;
    for(const lp_vertex& child:graph.at(node))
        for(uint32_t run=0;run<child.run;++run)out+=realize(std::string(reinterpret_cast<const char*>(child.id.b),16),graph,depth+1);
    return out;
}
const lp_highway* bank_fixture() {
    static const lp_highway* highway=[] {
        const char* names[]={"claim","record"};lp_tier0_record record[2]{};
        auto* floor=static_cast<const lp_tier0_record*>(text_identity_scalar_floor());
        lp_text* context=lp_text_new(floor);std::vector<char> identifiers;
        for(unsigned i=0;i<2;++i) {
            lp_ref value=lp_text_decompose(context,reinterpret_cast<const uint8_t*>(names[i]),std::strlen(names[i]),nullptr,nullptr);
            record[i].id=value.id;std::memcpy(record[i].m,value.c.m,sizeof value.c.m);
            record[i].rank=i;record[i].pad=value.tier;record[i].hilbert=lp_hilbert4(&value.c);
            identifiers.insert(identifiers.end(),{1,0,0,0});
            identifiers.insert(identifiers.end(),names[i],names[i]+std::strlen(names[i])+1);
        }
        lp_text_free(context);
        const char layout[]="records\t2\nedges-count\t0\nlist\tkind\tkind\t0\t2\nbank\tkind\tkind\tkind\tphysicality\t2\t62\tflags\n";
        const char keys[]="kind\tclaim\t0\nkind\trecord\t1\n";
        const auto process=
#ifdef _WIN32
            _getpid();
#else
            getpid();
#endif
        std::string path=::testing::TempDir()+"canonical-stage-banks-"+std::to_string(process)+".bin";
        if(!lp_highway_write(path.c_str(),record,2,nullptr,0,layout,sizeof layout-1,keys,sizeof keys-1,identifiers.data(),identifiers.size()))
            throw std::runtime_error("write real bank cache");
        const lp_highway* result=lp_highway_map(path.c_str());
        if(!result)throw std::runtime_error("map real bank cache");
        std::remove(path.c_str());return result;
    }();
    return highway;
}
}

TEST(CanonicalStage, ActualUnicodeTextReconstructsFromPairedCopyRows) {
    Stage s=stage();ASSERT_NE(nullptr,s);
    const std::string input="banana banana\n\xf0\x9f\x9a\x80 Cafe\xcc\x81 aa";
    canonical_stage_ref_t root=text(s.get(),input);
    hash128_t expected;ASSERT_EQ(0,hash128_text(reinterpret_cast<const uint8_t*>(input.data()),input.size(),&expected));
    EXPECT_EQ(key(expected),key(root.id));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    size_t nf;const hash128_t* frontier=canonical_stage_frontier(s.get(),&nf);
    ASSERT_EQ(1u,nf);EXPECT_EQ(key(root.id),key(frontier[0]));
    novel(s.get());ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
    auto entities=rows(s.get(),CANONICAL_STAGE_ENTITY),physicality=rows(s.get(),CANONICAL_STAGE_PHYSICALITY);
    ASSERT_FALSE(entities.empty());ASSERT_EQ(entities.size(),physicality.size());
    for(size_t i=0;i<entities.size();++i) {
        EXPECT_EQ(entities[i][0],physicality[i][0]);EXPECT_EQ(entities[i][1],physicality[i][1]);
        EXPECT_EQ(entities[i][3],physicality[i][2]);EXPECT_GT(be(entities[i][1].data(),2),0u);
        EXPECT_EQ(37u,entities[i][2].size());EXPECT_EQ(12u,entities[i][4].size());
        EXPECT_EQ(64u,be(entities[i][4].data(),4));
    }
    EXPECT_EQ(input,realize(key(root.id),paths(physicality)));
}

TEST(CanonicalStage, PresentTrunkAmputatesAllDescendants) {
    Stage s=stage();auto root=text(s.get(),"repeated words below a known file");
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    uint8_t present=1;ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_answer(s.get(),&present,1));
    size_t n;canonical_stage_frontier(s.get(),&n);EXPECT_EQ(0u,n);
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
    EXPECT_TRUE(rows(s.get(),CANONICAL_STAGE_ENTITY).empty());
    EXPECT_TRUE(rows(s.get(),CANONICAL_STAGE_PHYSICALITY).empty());
}

TEST(CanonicalStage, HighwayMembershipDoesNotProveDatabaseContainment) {
    Stage s=stage(1u<<20,bank_fixture());ASSERT_NE(nullptr,s);
    auto root=text(s.get(),"claim");
    lp_id native_id;std::memcpy(native_id.b,&root.id,16);
    ASSERT_TRUE(lp_highway_contains(bank_fixture(),&native_id));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    size_t count;const hash128_t* frontier=canonical_stage_frontier(s.get(),&count);
    ASSERT_EQ(1u,count);ASSERT_NE(nullptr,frontier);
    EXPECT_EQ(key(root.id),key(frontier[0]));
    novel(s.get());ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
    auto entity=rows(s.get(),CANONICAL_STAGE_ENTITY);
    auto physicality=rows(s.get(),CANONICAL_STAGE_PHYSICALITY);
    ASSERT_FALSE(entity.empty());ASSERT_EQ(entity.size(),physicality.size());
    EXPECT_EQ("claim",realize(key(root.id),paths(physicality)));
}

TEST(CanonicalStage, RecordedHighwayReferenceStillPrunesItsSubtree) {
    Stage original=stage();auto root=text(original.get(),"claim");
    Stage recorded=stage(1u<<20,bank_fixture());
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_reference(recorded.get(),&root));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(recorded.get(),&root.id,1));
    size_t count;canonical_stage_frontier(recorded.get(),&count);EXPECT_EQ(0u,count);
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(recorded.get()));
    EXPECT_TRUE(rows(recorded.get(),CANONICAL_STAGE_ENTITY).empty());
    EXPECT_TRUE(rows(recorded.get(),CANONICAL_STAGE_PHYSICALITY).empty());
}

TEST(CanonicalStage, RecordedHighwayReferenceRejectsForeignCoordinates) {
    Stage original=stage();auto root=text(original.get(),"claim"),other=text(original.get(),"record");
    std::memcpy(root.coordinate,other.coordinate,sizeof root.coordinate);
    Stage recorded=stage(1u<<20,bank_fixture());
    EXPECT_EQ(CANONICAL_STAGE_CONFLICT,canonical_stage_reference(recorded.get(),&root));
}

TEST(CanonicalStage, StructuralSpinesAreNeverDatabaseFrontiers) {
    Stage s=stage();canonical_stage_ref_t words[]={text(s.get(),"alpha"),text(s.get(),"beta")},spine{},root{};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),words,2,6,0,0,1,&spine));
    canonical_stage_ref_t parts[]={spine,words[0]};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),parts,2,7,0,0,0,&root));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    uint8_t absent=0;ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_answer(s.get(),&absent,1));
    size_t n;const hash128_t* frontier=canonical_stage_frontier(s.get(),&n);ASSERT_EQ(2u,n);
    for(size_t i=0;i<n;++i)EXPECT_NE(key(spine.id),key(frontier[i]));
    novel(s.get());ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
    auto graph=paths(rows(s.get(),CANONICAL_STAGE_PHYSICALITY));
    EXPECT_EQ("alphabetaalpha",realize(key(root.id),graph));
}

TEST(CanonicalStage, SharedDescendantOfKnownRootIsNotProbedAgain) {
    Stage s=stage();auto shared=text(s.get(),"shared"),other=text(s.get(),"other");
    canonical_stage_ref_t a_children[]={shared,other},a{},b{},root{};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),a_children,2,6,0,0,0,&a));
    canonical_stage_ref_t b_children[]={shared,shared};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),b_children,2,6,0,0,0,&b));
    canonical_stage_ref_t roots[]={a,b};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),roots,2,7,0,0,0,&root));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    uint8_t absent=0;ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_answer(s.get(),&absent,1));
    size_t n;const hash128_t* front=canonical_stage_frontier(s.get(),&n);ASSERT_EQ(2u,n);
    uint8_t present[2];for(size_t i=0;i<n;++i)present[i]=key(front[i])==key(a.id);
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_answer(s.get(),present,2));
    canonical_stage_frontier(s.get(),&n);EXPECT_EQ(0u,n);
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
    EXPECT_EQ(2u,rows(s.get(),CANONICAL_STAGE_ENTITY).size());
}

TEST(CanonicalStage, KnownCarrierUpdatesRetainHighBitAndVertexTestimony) {
    Stage s=stage(1u<<20,bank_fixture());canonical_stage_ref_t children[]={text(s.get(),"alpha"),text(s.get(),"beta")};
    children[0].said=LP_SAID_CLAIM;children[0].position=17;
    children[0].outcome=uint8_t(lp_outcome_of(0.75,&children[0].spare));
    canonical_stage_ref_t root{};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),children,2,6,0,UINT64_C(1)<<62,0,&root));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),children,2,6,0,UINT64_C(1)<<63,0,&root));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    uint8_t yes=1;ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_answer(s.get(),&yes,1));
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
    auto physicality=rows(s.get(),CANONICAL_STAGE_PHYSICALITY);ASSERT_EQ(1u,physicality.size());
    EXPECT_EQ((Field{0,0,0,64,0,0,0,0,0,0,0,3}),physicality[0][4]);
    auto graph=paths(physicality);const lp_vertex& first=graph.at(key(root.id))[0];
    EXPECT_EQ(LP_SAID_CLAIM,first.said);EXPECT_EQ(17u,first.position);
    EXPECT_EQ(children[0].outcome,first.outcome);EXPECT_EQ(children[0].spare,first.spare);
}

TEST(CanonicalStage, ConflictingVertexMetadataFailsWithoutPublishingPartialOutput) {
    Stage s=stage();canonical_stage_ref_t children[]={text(s.get(),"alpha"),text(s.get(),"beta")},root{};
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_compose(s.get(),children,2,6,0,0,0,&root));
    const auto original=root;children[0].said=LP_SAID_CLAIM;
    EXPECT_EQ(CANONICAL_STAGE_CONFLICT,canonical_stage_compose(s.get(),children,2,6,0,0,0,&root));
    EXPECT_EQ(key(original.id),key(root.id));
    size_t bytes=99,n=99;EXPECT_EQ(nullptr,canonical_stage_copy(s.get(),CANONICAL_STAGE_ENTITY,&bytes,&n));
    EXPECT_EQ(0u,bytes);EXPECT_EQ(0u,n);
    canonical_stage_reset(s.get());EXPECT_EQ(CANONICAL_STAGE_OK,canonical_stage_status(s.get()));
    root=text(s.get(),"recovered");ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(s.get(),&root.id,1));
    novel(s.get());EXPECT_EQ(CANONICAL_STAGE_OK,canonical_stage_finish(s.get()));
}

TEST(CanonicalStage, BudgetAndMalformedInputRefuseBeforeCopyExposure) {
    Stage tiny=stage(42);auto root=text(tiny.get(),"aa");
    ASSERT_EQ(CANONICAL_STAGE_OK,canonical_stage_begin(tiny.get(),&root.id,1));novel(tiny.get());
    EXPECT_EQ(CANONICAL_STAGE_LIMIT,canonical_stage_finish(tiny.get()));
    size_t n;EXPECT_EQ(nullptr,canonical_stage_copy(tiny.get(),CANONICAL_STAGE_ENTITY,&n,nullptr));
    Stage narrow=stage(1u<<20,nullptr,1);canonical_stage_ref_t out{};
    const std::string sentence="several distinct words";
    EXPECT_EQ(CANONICAL_STAGE_LIMIT,canonical_stage_text(narrow.get(),reinterpret_cast<const uint8_t*>(sentence.data()),sentence.size(),&out));
    Stage invalid=stage();const uint8_t overlong[]={0xc0,0x80};
    EXPECT_EQ(CANONICAL_STAGE_INVALID,canonical_stage_text(invalid.get(),overlong,sizeof overlong,&out));
}

// Preserve the claim-arity protocol after retiring the client-side standing
// writer: absent objects contribute no vertex; an explicit zero ID does.
TEST(StagedLoad, ClaimIdentityUsesOnlyTheDeclaredVertices) {
    std::array<uint8_t, 48> bytes{};
    for (size_t i = 0; i < bytes.size(); ++i) bytes[i] = static_cast<uint8_t>(i);
    std::array<hash128_t, 3> vertices;
    std::memcpy(vertices.data(), bytes.data(), bytes.size());
    const uint8_t binary[16] = {0xe5,0x28,0xe9,0x57,0x98,0x03,0x7d,0xf4,0x10,0x54,0x3d,0x9f,0x31,0xe3,0x96,0xec};
    const uint8_t ternary[16] = {0x5e,0x99,0x02,0x52,0x4b,0x30,0xf4,0xf4,0xcf,0xd5,0xd2,0x18,0x0f,0xd3,0x57,0xbb};
    const uint8_t explicit_zero[16] = {0xbb,0x7f,0xea,0x38,0x58,0xe8,0x78,0x65,0x8d,0xa2,0x3d,0x1e,0xd4,0xbd,0x82,0x36};
    hash128_t actual;
    hash128_merkle(0, vertices.data(), 2, &actual);
    EXPECT_EQ(0, std::memcmp(&actual, binary, sizeof actual));
    hash128_merkle(0, vertices.data(), 3, &actual);
    EXPECT_EQ(0, std::memcmp(&actual, ternary, sizeof actual));
    vertices[2] = {};
    hash128_merkle(0, vertices.data(), 3, &actual);
    EXPECT_EQ(0, std::memcmp(&actual, explicit_zero, sizeof actual));
    EXPECT_NE(0, std::memcmp(&actual, binary, sizeof actual));
}
