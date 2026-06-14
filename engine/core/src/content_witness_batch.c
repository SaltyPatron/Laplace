#include "laplace/core/content_witness_batch.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/text_decomposer.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

static void hash_canonical(const char* s, hash128_t* out) {
    hash128_blake3((const uint8_t*)s, strlen(s), out);
}

static hash128_t tier_type_id(uint8_t tier) {
    hash128_t id;
    switch (tier) {
        case 0: hash_canonical("substrate/type/Codepoint/v1", &id); break;
        case 1: hash_canonical("substrate/type/Grapheme/v1", &id); break;
        case 2: hash_canonical("substrate/type/Word/v1", &id); break;
        case 3: hash_canonical("substrate/type/Sentence/v1", &id); break;
        default: hash_canonical("substrate/type/Document/v1", &id); break;
    }
    return id;
}

static int codepoint_resolver(uint32_t atom, void* /*user*/,
                              hash128_t* out_id, double out_coord[4],
                              hilbert128_t* out_hb) {
    return codepoint_table_resolve_atom(atom, out_id, out_coord, out_hb);
}

static void physicality_id_compute(hash128_t entity_id, hash128_t source_id,
                                   const double coord[4], const double* traj, size_t traj_n,
                                   hash128_t* out) {
    size_t traj_bytes = traj_n * sizeof(double);
    size_t total = 16 + 16 + 2 + 32 + traj_bytes;
    uint8_t* buf = (uint8_t*)malloc(total);
    if (!buf) { hash128_zero(out); return; }
    size_t o = 0;
    memcpy(buf + o, &entity_id, 16); o += 16;
    memcpy(buf + o, &source_id, 16); o += 16;
    int16_t physicality_type = 1;
    memcpy(buf + o, &physicality_type, 2); o += 2;
    memcpy(buf + o, coord, 32); o += 32;
    if (traj_n > 0) {
        memcpy(buf + o, traj, traj_bytes);
        o += traj_bytes;
    }
    hash128_blake3(buf, o, out);
    free(buf);
}

/* The no-artificial-inflation law: a unary wrapper above the grapheme floor —
 * a node with tier > 1, exactly one child, covering the same text span as that
 * child — is NOT a unit of content; its child stands in for it everywhere
 * (id, trajectory reference, emission). collapse_idx descends to the
 * stand-in. Never below tier 1: bare codepoints are perfcache floor, not
 * content roots, so a grapheme keeps its single-codepoint composition. */
static uint32_t collapse_idx(const tier_tree_t* tree, uint32_t idx) {
    for (;;) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) break;
        if (node.tier <= 1 || node.child_count != 1) break;
        tier_node_view_t child;
        if (tier_tree_get_node(tree, node.first_child_idx, &child) != 0) break;
        if (child.text_range_off != node.text_range_off
            || child.text_range_len != node.text_range_len) break;
        idx = node.first_child_idx;
    }
    return idx;
}

/* The natural unit: the top of the tree with its unary wrappers collapsed. */
static uint32_t natural_unit_index(const tier_tree_t* tree) {
    size_t nc = tier_tree_node_count(tree);
    if (nc == 0) return 0;
    return collapse_idx(tree, (uint32_t)(nc - 1));
}

static int should_emit_compositional(const tier_tree_t* tree, uint32_t idx) {
    /* a collapsible wrapper never emits anywhere in the tree */
    if (collapse_idx(tree, idx) != idx) return 0;
    tier_node_view_t node;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    if (node.tier == 0) return 0;
    return 1;
}

/* Shared front half: decompose -> compose. Fail loud, not AV: the codepoint
 * floor must be loaded (perfcache) BEFORE the text decomposer runs --
 * segmentation itself reads the perfcache tables, so a post-decomposer guard
 * is unreachable in the unloaded case. */
static int content_tree_build(const uint8_t* utf8, size_t len, tier_tree_t** out_tree) {
    if (len == 0) return -2;
    if (!codepoint_table_is_loaded()) return -3;

    tier_tree_t* tree = NULL;
    if (laplace_text_decomposer_run(utf8, len, &tree) != 0 || !tree) return -2;
    if (hash_composer_run(tree, codepoint_resolver, NULL) != 0) {
        tier_tree_free(tree);
        return -2;
    }
    if (tier_tree_node_count(tree) == 0) {
        tier_tree_free(tree);
        return -2;
    }
    *out_tree = tree;
    return 0;
}

int laplace_content_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id) {
    if (!utf8 || !out_root_id) return -1;

    tier_tree_t* tree = NULL;
    int rc = content_tree_build(utf8, len, &tree);
    if (rc != 0) return rc;

    tier_node_view_t root;
    tier_tree_get_node(tree, natural_unit_index(tree), &root);
    *out_root_id = root.id;
    tier_tree_free(tree);
    return 0;
}

/* ── Record-once bank (the content-addressed / Merkle-DAG law) ────────────────
 * Same content = same id = recorded ONCE, then referenced. A sentence references
 * its words, a word its graphemes, a grapheme its codepoints — the reference is
 * the parent's trajectory (mantissa-packed child ids), which the emit loop below
 * already builds. This bank stops the witness from physically RE-recording a node
 * it already emitted this ingest, so a shared grapheme/word is ONE row, not one
 * per occurrence (the ConceptNet 100M-row balloon). Codepoints are tier 0 and are
 * never emitted at all — the perfcache banks those. Cross-ingest repeats are
 * caught by the writer's existence filter; this kills the in-run re-recording
 * that is the bulk.
 *
 * One decompose thread (LAPLACE_DECOMPOSE_WORKERS=1): unsynchronized. A racy
 * double-record would cost only a redundant row the writer drops on conflict,
 * never corruption. content_witness_reset() clears it at each ingest start. */
typedef struct { hash128_t* slots; size_t cap; size_t count; } emit_bank_t;
static emit_bank_t g_emit_bank;

static int emit_bank_slot_empty(const hash128_t* h) { return (h->hi | h->lo) == 0; }

/* 1 if id was already recorded this run, 0 if newly recorded. */
static int emit_bank_record(const hash128_t* id) {
    emit_bank_t* s = &g_emit_bank;
    if (s->cap == 0) {
        /* Pre-size for the whole source so the table does NOT realloc mid-run. The grow
         * path (calloc new + rehash + free old) is heap churn that was empirically the
         * trigger for the latent content-witness heap transient: a single staged content
         * row would silently vanish from a stage near a grow (the data-36 / sense-21
         * reseed ghosts) — drop count tracked the grow count, and a run that banked one
         * id (no grow) never lost a row. 2^22 slots = 67 MB holds ~3.1M distinct ids at
         * 0.75 load, covering every seed source; memory is not the constraint on this box.
         * The grow below remains as a correctness backstop for an unexpectedly huge source. */
        s->cap = (size_t)1 << 22;
        s->slots = (hash128_t*)calloc(s->cap, sizeof(hash128_t));
        if (!s->slots) { s->cap = 0; return 0; }  /* OOM: treat as novel; writer dedups */
    } else if ((s->count + 1) * 4 >= s->cap * 3) {  /* grow at 0.75 load */
        size_t ncap = s->cap << 1;
        hash128_t* ns = (hash128_t*)calloc(ncap, sizeof(hash128_t));
        if (ns) {
            size_t nmask = ncap - 1;
            for (size_t i = 0; i < s->cap; ++i) {
                if (emit_bank_slot_empty(&s->slots[i])) continue;
                size_t j = (size_t)s->slots[i].lo & nmask;
                while (!emit_bank_slot_empty(&ns[j])) j = (j + 1) & nmask;
                ns[j] = s->slots[i];
            }
            free(s->slots);
            s->slots = ns;
            s->cap = ncap;
        }
    }
    size_t mask = s->cap - 1;
    size_t j = (size_t)id->lo & mask;
    while (!emit_bank_slot_empty(&s->slots[j])) {
        if (hash128_equals(&s->slots[j], id)) return 1;
        j = (j + 1) & mask;
    }
    s->slots[j] = *id;
    s->count++;
    return 0;
}

/* 1 if id is already banked, without recording it. */
static int emit_bank_contains(const hash128_t* id) {
    emit_bank_t* s = &g_emit_bank;
    if (s->cap == 0) return 0;
    size_t mask = s->cap - 1;
    size_t j = (size_t)id->lo & mask;
    while (!emit_bank_slot_empty(&s->slots[j])) {
        if (hash128_equals(&s->slots[j], id)) return 1;
        j = (j + 1) & mask;
    }
    return 0;
}

void content_witness_reset(void) {
    free(g_emit_bank.slots);
    g_emit_bank.slots = NULL;
    g_emit_bank.cap = 0;
    g_emit_bank.count = 0;
}

int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id) {
    if (!stage || !utf8 || !source_id || !out_root_id) return -1;

    tier_tree_t* tree = NULL;
    {
        int rc = content_tree_build(utf8, len, &tree);
        if (rc != 0) {
            hash128_zero(out_root_id);
            return rc;
        }
    }

    size_t nc = tier_tree_node_count(tree);

    uint32_t root_idx = natural_unit_index(tree);
    tier_node_view_t root;
    tier_tree_get_node(tree, root_idx, &root);
    *out_root_id = root.id;

    /* Trunk short-circuit (O(tier), not O(content)): a unit whose root is already
     * banked this run is WHOLLY recorded — reference it, do not re-walk its subtree.
     * Re-ingesting John 3:16 inside the Bible hits this at the verse root. */
    if (emit_bank_contains(&root.id)) { tier_tree_free(tree); return 0; }

    int64_t now_us = INTENT_STAGE_PG_EPOCH_UNIX_US;

    for (uint32_t idx = 0; idx < (uint32_t)nc; ++idx) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) continue;
        if (node.tier == 0) continue;
        if (!should_emit_compositional(tree, idx)) continue;
        /* record-once: a node already emitted this run is referenced by its
         * parents' trajectories, never physically re-recorded. */
        if (emit_bank_record(&node.id)) continue;

        hash128_t type_id = tier_type_id(node.tier);
        if (intent_stage_add_entity(stage, &node.id, (int16_t)node.tier, &type_id, source_id) != 0) {
            tier_tree_free(tree);
            return -2;
        }

        double* traj = NULL;
        size_t m = node.child_count;
        if (m > 0) {
            hash128_t* child_ids = (hash128_t*)malloc(m * sizeof(hash128_t));
            uint64_t*  flags     = (uint64_t*)malloc(m * sizeof(uint64_t));
            if (!child_ids || !flags) {
                free(child_ids); free(flags); free(traj);
                tier_tree_free(tree);
                return -2;
            }
            for (uint32_t ci = 0; ci < m; ++ci) {
                /* references collapse with the wrapper: a parent's trajectory
                 * names the stand-in (the suppressed unary child's child),
                 * never an unemitted wrapper id */
                tier_node_view_t ch;
                tier_tree_get_node(tree, collapse_idx(tree, node.first_child_idx + ci), &ch);
                child_ids[ci] = ch.id;
                flags[ci] = laplace_vertex_flags(
                    ch.tier, ch.tier == 0 ? 1 : 0, ch.atom);
            }
            traj = (double*)malloc(m * 4 * sizeof(double));
            if (!traj || trajectory_build_flagged(child_ids, flags, m, traj) != 0) {
                free(child_ids); free(flags); free(traj);
                tier_tree_free(tree);
                return -2;
            }
            free(child_ids);
            free(flags);
        }

        hash128_t phys_id;
        /* UNITS: physicality_id_compute takes the trajectory length in DOUBLES (m*4);
         * intent_stage_add_physicality takes it in VERTICES (m). Passing m*4 there made
         * the COPY writer read 32*(m*4) bytes from a 32*m buffer — the transient
         * ContentWitnessBatchAdd AV, and 4x-vertex garbage trajectories when it survived. */
        physicality_id_compute(node.id, *source_id, node.coord, traj, m * 4, &phys_id);
        if (intent_stage_add_physicality(
                stage, &phys_id, &node.id, source_id, 1,
                node.coord, &node.hilbert, traj, (uint32_t)m,
                (int32_t)m, 1, 0.0, 1, 0, now_us) != 0) {
            free(traj);
            tier_tree_free(tree);
            return -2;
        }
        free(traj);
    }

    tier_tree_free(tree);
    return 0;
}
