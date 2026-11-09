#ifndef LAPLACE_CORE_PHYSICALITY_DESCRIPTOR_ADMISSION_INTERNAL_H
#define LAPLACE_CORE_PHYSICALITY_DESCRIPTOR_ADMISSION_INTERNAL_H

#include "physicality_descriptor_provider.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <memory_resource>
#include <new>
#include <unordered_map>
#include <unordered_set>
#include <vector>

struct Status { physicality_descriptor_status_t value; };
struct AllocationRefusal final : std::bad_alloc {
    uint32_t kind;
    size_t requested, retained, peak, limit;
    AllocationRefusal(uint32_t reason, size_t request, size_t current,
        size_t high_water, size_t ceiling)
        : kind(reason), requested(request), retained(current),
          peak(high_water), limit(ceiling) {}
};

class Memory final : public std::pmr::memory_resource {
public:
    explicit Memory(size_t limit) : limit_(limit) {}
    size_t used() const { return used_; }
    size_t peak() const { return peak_; }
    size_t remaining() const { return limit_ - used_; }
    void claim(size_t bytes) {
        if (bytes > remaining())
            throw AllocationRefusal(PHYSICALITY_MATERIALIZATION_REFUSAL_MEMORY_GRANT,
                bytes, used_, peak_, limit_);
        used_ += bytes;
        peak_ = std::max(peak_, used_);
    }
    void release(size_t bytes) { used_ -= bytes; }
private:
    void* do_allocate(size_t bytes, size_t alignment) override {
        claim(bytes);
        try { return ::operator new(bytes, std::align_val_t(alignment)); }
        catch (const std::bad_alloc&) {
            release(bytes);
            throw AllocationRefusal(PHYSICALITY_MATERIALIZATION_REFUSAL_MEMORY_ALLOCATOR,
                bytes, used_, peak_, limit_);
        }
    }
    void do_deallocate(void* pointer, size_t bytes, size_t alignment) override {
        ::operator delete(pointer, std::align_val_t(alignment));
        release(bytes);
    }
    bool do_is_equal(const std::pmr::memory_resource& other) const noexcept override {
        return this == &other;
    }
    size_t limit_;
    size_t used_ = 0;
    size_t peak_ = 0;
};

struct IdHash {
    size_t operator()(const hash128_t& id) const noexcept {
        uint64_t x = id.lo ^ id.hi;
        x ^= x >> 33;
        x *= UINT64_C(0xff51afd7ed558ccd);
        return static_cast<size_t>(x ^ (x >> 33));
    }
};
struct IdEqual {
    bool operator()(const hash128_t& a, const hash128_t& b) const noexcept {
        return hash128_equals(&a, &b) != 0;
    }
};
template<class T> using IdMap = std::pmr::unordered_map<hash128_t,T,IdHash,IdEqual>;
using IdSet = std::pmr::unordered_set<hash128_t,IdHash,IdEqual>;

template<class T, void (*Destroy)(T*)> struct External {
    T* value = nullptr;
    Memory* memory;
    size_t bytes = 0;
    explicit External(Memory& owner) : memory(&owner) {}
    void account(size_t retained, size_t peak) {
        if (peak < retained) throw std::bad_alloc();
        memory->claim(peak);
        memory->release(peak - retained);
        bytes = retained;
    }
    ~External() { Destroy(value); memory->release(bytes); }
};

struct Geometry {
    hash128_t id{};
    std::array<double,4> coord{};
    hilbert128_t hilbert{};
};

inline Geometry geometry(const tier_node_view_t& node) {
    Geometry value;
    value.id = node.id;
    std::copy_n(node.coord, 4, value.coord.data());
    value.hilbert = node.hilbert;
    return value;
}

struct Selected {
    Geometry physicality;
    size_t body_index = SIZE_MAX;
    bool byte_basis = false;
    bool terminal_body_verified = false;
};

struct OutputNode {
    Geometry geometry;
    size_t first_child;
    size_t child_count;
    int16_t type;
};

struct View {
    hash128_t id{};
    uint32_t state = PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE;
    size_t missing_first = 0;
    size_t missing_count = 0;
};

inline void require(bool condition, physicality_descriptor_status_t status = PHYSICALITY_DESCRIPTOR_INVALID_BODY) {
    if (!condition) throw Status{status};
}

struct physicality_descriptor_materialization {
    Memory memory;
    std::pmr::vector<hash128_t> pending;
    std::pmr::vector<hash128_t> missing;
    std::pmr::vector<physicality_descriptor_admitted_form_t> forms;
    std::pmr::vector<physicality_descriptor_form_observation_t> observations;
    intent_stage_t* stage = nullptr;
    size_t stage_bytes = 0;
    explicit physicality_descriptor_materialization(size_t ceiling)
        : memory(ceiling - sizeof(*this)), pending(&memory), missing(&memory), forms(&memory),
          observations(&memory) {}
    ~physicality_descriptor_materialization() { intent_stage_free(stage); }
};

physicality_descriptor_status_t laplace_phys_desc_materialize_impl(
    physicality_descriptor_materialization& result,
    const physicality_descriptor_capture_t* source,
    const physicality_descriptor_vocabulary_t& vocabulary,
    const intent_stage_t* const* current_stages, size_t stage_count,
    const intent_stage_t* const* admitted_stages, size_t admitted_stage_count,
    const hash128_t* missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* sources, size_t source_count,
    int64_t generated_at,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_materialization_diagnostics_t* diagnostics);

#endif
