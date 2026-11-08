#!/usr/bin/env python3
"""Codegen attestation law: manifests -> relation, POS, entity-type, language, deprel,
qualifier and trust-class laws (.c/.h), seed SQL, relation selector metadata. The POS and deprel
vocabularies are generated from their authorities (scripts/refresh-vocabulary-manifest.py)."""
from __future__ import annotations

import re
import sys
from pathlib import Path

from codegen_vocabulary_laws import (
    FIRMWARE_FIELDS,
    dense_labels,
    emit_deprel_law,
    emit_entity_type_law,
    emit_firmware_law,
    emit_language_law,
    emit_pos_law,
    emit_qualifier_law,
    emit_trust_class_law,
    read_qualifier_families,
    read_vocabulary,
)

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "engine/manifest"
OUT_CORE = ROOT / "engine/core"
CHECK_MODE = False
DRIFT: list[Path] = []
OUT_FAMILY_FRAG = ROOT / "extension/laplace_substrate/sql/generated/relation_family_ids.sql.in"
OUT_SET_FRAG = ROOT / "extension/laplace_substrate/sql/generated/relation_set_ids.sql.in"
OUT_QUALIFIER_FRAG = ROOT / "extension/laplace_substrate/sql/generated/qualifier_bits.sql.in"
TRAJECTORY_FACT = "TRAJECTORY"
_RANK_BANDS = [
    "mandate", "definitional", "taxonomic", "equivalence", "partitive", "causal",
    "oppositional", "associative", "tensor_calculation", "lexical_glue",
    "scalar_valued", "standards_structural", "probationary",
]


def _write_if_changed(path: Path, data: bytes) -> None:
    """Skip the write when bytes are identical (preserve mmap / ninja restat)."""
    try:
        if path.exists() and path.read_bytes() == data:
            return
    except OSError:
        pass
    if CHECK_MODE:
        DRIFT.append(path)
        return
    path.write_bytes(data)


def _write_text_if_changed(path: Path, text: str) -> None:
    _write_if_changed(path, text.encode("utf-8"))


def parse_simple_toml(path: Path) -> dict:
    """Minimal TOML parser for our manifest shape."""
    text = path.read_text(encoding="utf-8")
    data: dict = {"ranks": {}, "relation": [], "alias": [], "set": [], "dynamic": {}, "upos": {}, "tagsets": {}}
    section = None
    current: dict | None = None
    pending = None

    for raw in text.splitlines():
        quoted = escaped = False
        for offset, char in enumerate(raw):
            if escaped:
                escaped = False
            elif quoted and char == "\\":
                escaped = True
            elif char == '"':
                quoted = not quoted
            elif char == "#" and not quoted:
                raw = raw[:offset]
                break
        line = raw.strip()
        if pending is not None:
            pending += " " + line
            if "]" not in line:
                continue
            line, pending = pending, None
        elif "=" in line and line.split("=", 1)[1].strip().startswith("[") and "]" not in line.split("=", 1)[1]:
            pending = line
            continue
        if not line:
            continue
        if line.startswith("[") and line.endswith("]") and "=" not in line:
            inner = line.strip("[]")
            if inner.startswith(("relation", "alias", "set")):
                section = "relation" if inner.startswith("relation") else ("alias" if inner.startswith("alias") else "set")
                current = {}
                data[section].append(current)
            elif inner.startswith("dynamic."):
                section, current = "dynamic", {}
                data["dynamic"][inner.split(".", 1)[1]] = current
            elif inner == "ranks":
                section, current = "ranks", None
            elif inner == "upos":
                section, current = "upos", data["upos"]
            else:
                section, current = inner, data["tagsets"].setdefault(inner, {})
            continue
        if "=" not in line:
            continue
        k, v = [x.strip() for x in line.split("=", 1)]
        if v.startswith('"') and v.endswith('"'):
            val: object = v[1:-1]
        elif v == "null":
            val = None
        elif v in ("true", "false"):
            val = v == "true"
        elif v.startswith("[") and v.endswith("]"):
            val = re.findall(r'"([^"]*)"', v)
        else:
            try:
                val = float(v) if "." in v else int(v)
            except ValueError:
                val = v
        if section == "ranks":
            data["ranks"][k] = val
        elif current is not None:
            if k.startswith('"') and k.endswith('"'):
                k = k[1:-1]
            current[k] = val
    return data


def c_hash(hi: int, lo: int) -> str:
    return f"{{ .hi = 0x{hi:016x}ULL, .lo = 0x{lo:016x}ULL }}"


def emit_relation_law(rel: dict) -> None:
    bad = [r["canonical"] for r in rel["relation"] if not re.fullmatch(r"[A-Za-z0-9_]+", r["canonical"])]
    if bad:
        raise SystemExit(f"relation labels must be single words [A-Za-z0-9_]+: {bad}")
    relations, aliases, ranks = rel["relation"], rel["alias"], rel["ranks"]
    canon_names = sorted({r["canonical"] for r in relations})
    name_to_idx = {n: i for i, n in enumerate(canon_names)}
    rels_by_name = {r["canonical"]: r for r in relations}
    qualifier_families = {f["name"]: f for f in read_qualifier_families(MANIFEST / "qualifiers.toml")}

    def qualifier_bit(spec: str, relation: str, where: str) -> int:
        family, _, value = spec.partition("/")
        f = qualifier_families.get(family)
        if f is None or value not in f["values"]:
            raise SystemExit(f"{where}: undeclared qualifier {spec}")
        if f["relations"] is not None and relation not in f["relations"]:
            raise SystemExit(f"{where}: qualifier family {family} does not qualify {relation}")
        return f["base"] + f["values"].index(value)

    retired = {r["canonical"]: r["retired"] for r in relations if r.get("retired")}
    for name, successor in sorted(retired.items()):
        if successor == TRAJECTORY_FACT:
            if rels_by_name[name].get("retired_flip"):
                raise SystemExit(f"relation {name} retires to trajectories and cannot flip")
            continue
        if successor not in name_to_idx:
            raise SystemExit(f"relation {name} is retired to {successor}, which is not declared")
        if successor in retired:
            raise SystemExit(f"relation {name} is retired to {successor}, which is itself retired")
    for r in relations:
        if not r.get("retired"):
            for field in ("parent", "family_root"):
                if r.get(field) in retired:
                    raise SystemExit(f"relation {r['canonical']} has retired {field} {r[field]}")
        if (r.get("retired_flip") or r.get("retired_qualifier")) and not r.get("retired"):
            raise SystemExit(f"relation {r['canonical']} declares a retired reading without retired")
        if r.get("retired_qualifier") and r.get("retired") == TRAJECTORY_FACT:
            raise SystemExit(f"relation {r['canonical']} retires to trajectories and cannot qualify")

    alias_entries = []
    for a in sorted(aliases, key=lambda x: x["surface"]):
        if not a.get("surface"):
            raise SystemExit("relation alias surface must not be empty")
        canon = a["canonical"]
        if canon not in name_to_idx:
            continue
        if canon in retired:
            raise SystemExit(f"alias {a['surface']} resolves to retired relation {canon}; point it at {retired[canon]}")
        q = qualifier_bit(a["qualifier"], canon, f"alias {a['surface']}") if a.get("qualifier") else -1
        alias_entries.append((a["surface"], name_to_idx[canon], a["flip"], q))
    alias_entries.sort(key=lambda e: e[0])

    sets = rel.get("set", [])
    retired_names = {r["canonical"] for r in relations if r.get("retired")}
    known = (set(canon_names) - retired_names) | {a.get("surface") for a in rel.get("alias", []) if a.get("surface")}
    alias_to_canon = {a["surface"]: a["canonical"] for a in rel.get("alias", []) if a.get("surface")}
    set_members: dict[str, list[str]] = {}
    for s in sets:
        nm = s.get("name")
        if not nm:
            raise SystemExit("codegen: a [[set]] has no name")
        members = s.get("members") or []
        if not members:
            raise SystemExit(f"codegen: set '{nm}' declares no members")
        stale = sorted(m for m in members if m in retired_names)
        if stale:
            raise SystemExit(f"codegen: set '{nm}' names retired relations: " + ", ".join(stale))
        unknown = [m for m in members if m not in known]
        if unknown:
            raise SystemExit(f"codegen: set '{nm}' names relations that are not in the manifest: " + ", ".join(sorted(unknown)))
        if nm in set_members:
            raise SystemExit(f"codegen: set '{nm}' declared twice")
        set_members[nm] = sorted(members)

    header = OUT_CORE / "include/laplace/core/relation_law.h"
    _write_text_if_changed(header, """#pragma once\n
#include <stddef.h>\n#include <stdint.h>
\n#include "laplace/core/hash128.h"
\n#ifdef __cplusplus
extern "C" {\n#endif
\ntypedef enum {
    LAPLACE_REL_SYMMETRY_ASYMMETRIC = 0,\n    LAPLACE_REL_SYMMETRY_SYMMETRIC   = 1,
} laplace_rel_symmetry_t;\n
/* A retired relation keeps its bit and type id so admitted evidence stays readable;\n * resolving it for emission returns this code. Its meaning is carried by the
 * successor relation plus the claim's qualifiers. */\n#define LAPLACE_REL_RETIRED (-3)
\ntypedef struct {
    const char*     canonical;\n    hash128_t       type_id;
    double          rank;\n    laplace_rel_symmetry_t symmetry;
    int16_t         parent_idx;\n    int16_t         family_root_idx;
    uint8_t         flip;\n    int16_t         successor_idx;   /* -1 live; -2 retired to trajectories; otherwise the
                                        relation that replaced it */\n} laplace_relation_def_t;
\ntypedef struct {
    const char* surface;\n    int16_t     canon_idx;
    uint8_t     flip;\n    int16_t     qualifier_bit;  /* the claim qualifier this surface states; -1 none */
} laplace_relation_alias_t;\n
extern const laplace_relation_def_t* laplace_relation_table;\nextern const size_t laplace_relation_table_count;
\nextern const laplace_relation_alias_t* laplace_relation_alias_table;
extern const size_t laplace_relation_alias_table_count;\nsize_t laplace_relation_alias_count(void);
const laplace_relation_alias_t* laplace_relation_alias_at(size_t index);\n
int laplace_relation_type_id(const char* canonical_name, hash128_t* out_type_id);\nint laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,\n                                     uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_lookup(const hash128_t* type_id, const laplace_relation_def_t** out_def);\nint laplace_relation_in_family(const hash128_t* type_id, const char* family_root, int* out);
/* 1 when type_id names a retired manifest relation (out_successor, when given, receives\n * its successor's canonical name); 0 when it is live or not a manifest relation. */
int laplace_relation_retired(const hash128_t* type_id, const char** out_successor);\n/* The claim qualifier a relation surface states (qualifiers.toml bit of the attestation
 * mask): "holo_member" is HAS_PART read backwards with meronymy/member. -1 when the surface\n * states none. Every emitter that resolves a surface ORs this bit into the claim's mask. */
int laplace_relation_surface_qualifier(const char* surface);\n/* 0 with *out_ids and *out_count set to the declared [[set]]'s cached type_id slice;
 * -1 when set_name is not declared in relation_types.toml. */\nint laplace_relation_set_ids(const char* set_name, const hash128_t** out_ids, size_t* out_count);
\n
#ifdef __cplusplus\n}
#endif\n""")

    lines = [
        '#include "laplace/core/relation_law.h"', "", "#include <stdio.h>", "#include <string.h>", "",
        '#include "laplace/core/hash128.h"', '#include "laplace/core/content_witness_batch.h"',
        '#include "laplace/core/text_identity.h"', "", "static laplace_relation_def_t k_relations[] = {",
    ]
    rel_entries = []
    for name in canon_names:
        r = next(x for x in relations if x["canonical"] == name)
        sym = "LAPLACE_REL_SYMMETRY_SYMMETRIC" if r["symmetry"] == "symmetric" else "LAPLACE_REL_SYMMETRY_ASYMMETRIC"
        parent = r.get("parent")
        parent_idx = str(name_to_idx[parent]) if parent and parent in name_to_idx else "-1"
        fr = r.get("family_root") or name
        fr_idx = str(name_to_idx[fr]) if fr in name_to_idx else str(name_to_idx[name])
        rank_val = ranks.get(r["rank"], 0.09)
        succ_idx = ("-2" if retired[name] == TRAJECTORY_FACT else str(name_to_idx[retired[name]])) if name in retired else "-1"
        rel_entries.append(f'{{ "{name}", {{0}}, {rank_val}, {sym}, {parent_idx}, {fr_idx}, 0, {succ_idx} }},')
    for i in range(0, len(rel_entries), 2):
        lines.append("    " + " ".join(rel_entries[i:i + 2]))
    lines += ["};", "", "static const laplace_relation_alias_t k_alias_storage[] = {"]
    lines += [f'    {{ "{surf}", {idx}, {1 if flip else 0}, {q} }},' for surf, idx, flip, q in alias_entries]
    lines += ["};", ""]

    set_canon_indices: list[int] = []
    set_slices: list[tuple[str, int, int]] = []
    for nm in sorted(set_members):
        offset = len(set_canon_indices)
        for m in set_members[nm]:
            set_canon_indices.append(name_to_idx[m] if m in name_to_idx else name_to_idx[alias_to_canon[m]])
        set_slices.append((nm, offset, len(set_members[nm])))
    lines.append(f"static const int16_t k_relation_set_member_idx[{max(len(set_canon_indices), 1)}] = {{")
    lines.append("    " + (", ".join(str(idx) for idx in set_canon_indices) if set_canon_indices else "0"))
    lines.append("};")
    lines.append(f"static hash128_t k_relation_set_ids_cache[{max(len(set_canon_indices), 1)}];")
    lines.append("typedef struct { const char* name; uint16_t offset; uint16_t count; } rel_set_slice_t;")
    lines.append(f"static const rel_set_slice_t k_relation_sets[{max(len(set_slices), 1)}] = {{")
    lines += [f'    {{ "{nm}", {offset}u, {count}u }},' for nm, offset, count in set_slices]
    lines += ["};", f"static const size_t k_relation_set_count = {len(set_slices)}u;", ""]

    rel_count = len(canon_names)
    surface_count = len(alias_entries) + rel_count
    surface_bucket_size = 1
    while surface_bucket_size < surface_count * 4:
        surface_bucket_size <<= 1
    surface_bucket_mask = surface_bucket_size - 1

    impl = f"""\nconst laplace_relation_def_t* laplace_relation_table = k_relations;
const size_t laplace_relation_table_count = {len(canon_names)};
const laplace_relation_alias_t* laplace_relation_alias_table = k_alias_storage;
const size_t laplace_relation_alias_table_count = {len(alias_entries)};
size_t laplace_relation_alias_count(void) {{ return laplace_relation_alias_table_count; }}\nconst laplace_relation_alias_t* laplace_relation_alias_at(size_t index) {{
    return index < laplace_relation_alias_table_count ? laplace_relation_alias_table + index : NULL;\n}}
\nstatic int cmp_str(const char* a, const char* b) {{
    return strcmp(a, b);\n}}
\nstatic int type_id_from_canonical(const char* canonical_name, hash128_t* out_type_id) {{
    if (!canonical_name || !out_type_id) return -1;\n    /* A relation's id is the content id of its label: the same entity that text is
     * anywhere else. Governed labels are single words ([A-Za-z0-9_]+); a dynamic label\n     * with other characters composes through the loaded Tier-0 content spine. */
    size_t len = strlen(canonical_name);\n    if (hash128_label_content_id(canonical_name, len, out_type_id) == 0) return 0;
    return laplace_content_root_id((const uint8_t*)canonical_name, len, out_type_id) == 0 ? 0 : -1;\n}}

static hash128_t k_relation_type_id_cache[{len(canon_names)}];
\n/* Reverse index: type_id -> table ordinal, O(1). The type_id is a full BLAKE3 digest, so its low
 * 64 bits are already a uniform hash; we bucket on (lo & mask) with linear probing. Built once in\n * relation_ids_ensure() under the same init guard as the id cache, and lazily augmented with
 * Native text_id hashes in relation_text_ids_ensure() once the scalar floor is loaded.\n * -1 == empty slot. */
typedef struct {{\n    hash128_t key;
    int16_t   idx;\n}} rel_id_slot_t;
\n#define LAPLACE_REL_BUCKET_SIZE 4096u
#define LAPLACE_REL_BUCKET_MASK 4095u\nstatic rel_id_slot_t k_relation_bucket[LAPLACE_REL_BUCKET_SIZE];
\nstatic void rel_id_bucket_insert(const hash128_t* id, int16_t idx) {{
    size_t b = (size_t)(id->lo & LAPLACE_REL_BUCKET_MASK);\n    for (size_t probes = 0; probes < LAPLACE_REL_BUCKET_SIZE; ++probes) {{
        if (k_relation_bucket[b].idx < 0) {{\n            k_relation_bucket[b].key = *id;
            k_relation_bucket[b].idx = idx;\n            return;
        }}\n        if (hash128_equals(&k_relation_bucket[b].key, id)) return;
        b = (b + 1) & LAPLACE_REL_BUCKET_MASK;\n    }}
}}\n
/* String-keyed buckets for the surface->idx and canonical->idx lookups.
 * These were linear scans of the 23-alias + {len(canon_names)}-canonical tables run once
 * per attestation built through the categorical builders — hundreds of\n * millions of strcmps per seed recomputing a constant. FNV-1a over the
 * surface with linear probing, built under the same init guard as the id\n * bucket. Two tables because the domains differ: resolve_surface matches
 * aliases first then canonicals; relation_type_id must match canonicals\n * ONLY (an alias string falls through to the blake3 path there). */
typedef struct {{\n    const char* surface;
    int16_t     idx;\n    uint8_t     flip;
    int16_t     qualifier_bit;\n}} rel_surface_slot_t;

#define LAPLACE_REL_SURFACE_BUCKET_SIZE {surface_bucket_size}u
#define LAPLACE_REL_SURFACE_BUCKET_MASK {surface_bucket_mask}u
static rel_surface_slot_t k_relation_surface_bucket[LAPLACE_REL_SURFACE_BUCKET_SIZE];\nstatic rel_surface_slot_t k_relation_canonical_bucket[LAPLACE_REL_BUCKET_SIZE];
\nstatic uint64_t rel_surface_hash(const char* s) {{
    uint64_t h = 1469598103934665603ull;\n    for (const unsigned char* p = (const unsigned char*)s; *p; ++p) {{
        h ^= (uint64_t)*p;\n        h *= 1099511628211ull;
    }}\n    return h;
}}\n
static void rel_slot_insert(rel_surface_slot_t* tab, size_t mask,\n                            const char* surface, int16_t idx, uint8_t flip,
                            int16_t qualifier_bit, int keep_existing) {{\n    size_t b = (size_t)(rel_surface_hash(surface) & mask);
    for (;;) {{\n        if (tab[b].surface == NULL) {{
            tab[b].surface = surface;\n            tab[b].idx = idx;
            tab[b].flip = flip;\n            tab[b].qualifier_bit = qualifier_bit;
            return;\n        }}
        if (keep_existing && strcmp(tab[b].surface, surface) == 0)\n            return; /* alias inserted first keeps priority */
        b = (b + 1) & mask;\n    }}
}}\n
static int rel_slot_lookup(const rel_surface_slot_t* tab, size_t mask,\n                           const char* surface, int16_t* out_idx, uint8_t* out_flip) {{
    size_t b = (size_t)(rel_surface_hash(surface) & mask);\n    for (size_t probes = 0; probes <= mask; ++probes) {{
        if (tab[b].surface == NULL) return -1;\n        if (strcmp(tab[b].surface, surface) == 0) {{
            *out_idx = tab[b].idx;\n            if (out_flip) *out_flip = tab[b].flip;
            return 0;\n        }}
        b = (b + 1) & mask;\n    }}
    return -1;\n}}
\n#ifdef _WIN32
#include <windows.h>\nstatic volatile LONG g_relation_ids_state = 0;
static int ids_try_begin(void) {{ return InterlockedCompareExchange(&g_relation_ids_state, 1, 0) == 0; }}\nstatic void ids_mark_ready(void) {{ InterlockedExchange(&g_relation_ids_state, 2); }}
static int ids_ready(void) {{ return InterlockedCompareExchange(&g_relation_ids_state, 2, 2) == 2; }}\nstatic volatile LONG g_relation_text_ids_state = 0;
static int text_ids_try_begin(void) {{ return InterlockedCompareExchange(&g_relation_text_ids_state, 1, 0) == 0; }}\nstatic void text_ids_mark_ready(void) {{ InterlockedExchange(&g_relation_text_ids_state, 2); }}
static int text_ids_ready(void) {{ return InterlockedCompareExchange(&g_relation_text_ids_state, 2, 2) == 2; }}\n#else
static volatile int g_relation_ids_state = 0;\nstatic int ids_try_begin(void) {{ int expected = 0; return __atomic_compare_exchange_n(&g_relation_ids_state, &expected, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }}
static void ids_mark_ready(void) {{ __atomic_store_n(&g_relation_ids_state, 2, __ATOMIC_RELEASE); }}\nstatic int ids_ready(void) {{ return __atomic_load_n(&g_relation_ids_state, __ATOMIC_ACQUIRE) == 2; }}
static volatile int g_relation_text_ids_state = 0;\nstatic int text_ids_try_begin(void) {{ int expected = 0; return __atomic_compare_exchange_n(&g_relation_text_ids_state, &expected, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }}
static void text_ids_mark_ready(void) {{ __atomic_store_n(&g_relation_text_ids_state, 2, __ATOMIC_RELEASE); }}\nstatic int text_ids_ready(void) {{ return __atomic_load_n(&g_relation_text_ids_state, __ATOMIC_ACQUIRE) == 2; }}
#endif\n
static void relation_ids_ensure(void) {{\n    if (ids_ready()) return;
    if (ids_try_begin()) {{\n        for (size_t b = 0; b < LAPLACE_REL_BUCKET_SIZE; ++b) {{
            hash128_zero(&k_relation_bucket[b].key);\n            k_relation_bucket[b].idx = -1;
        }}\n        for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
            type_id_from_canonical(laplace_relation_table[i].canonical, &k_relation_type_id_cache[i]);\n            k_relations[i].type_id = k_relation_type_id_cache[i];
            rel_id_bucket_insert(&k_relation_type_id_cache[i], (int16_t)i);\n        }}
        for (size_t i = 0; i < laplace_relation_alias_table_count; ++i) {{\n            hash128_t alias_id;
            if (type_id_from_canonical(laplace_relation_alias_table[i].surface, &alias_id) == 0)\n                rel_id_bucket_insert(&alias_id, laplace_relation_alias_table[i].canon_idx);
        }}
        for (size_t i = 0; i < {len(set_canon_indices)}u; ++i)
            k_relation_set_ids_cache[i] = k_relation_type_id_cache[k_relation_set_member_idx[i]];\n        for (size_t b = 0; b < LAPLACE_REL_SURFACE_BUCKET_SIZE; ++b)
            k_relation_surface_bucket[b].surface = NULL;\n        for (size_t b = 0; b < LAPLACE_REL_BUCKET_SIZE; ++b)
            k_relation_canonical_bucket[b].surface = NULL;\n        /* aliases first: resolve_surface gives alias hits priority */
        for (size_t i = 0; i < laplace_relation_alias_table_count; ++i)\n            rel_slot_insert(k_relation_surface_bucket, LAPLACE_REL_SURFACE_BUCKET_MASK,
                            laplace_relation_alias_table[i].surface,\n                            laplace_relation_alias_table[i].canon_idx,
                            laplace_relation_alias_table[i].flip,\n                            laplace_relation_alias_table[i].qualifier_bit, 0);
        for (size_t i = 0; i < laplace_relation_table_count; ++i) {{\n            rel_slot_insert(k_relation_surface_bucket, LAPLACE_REL_SURFACE_BUCKET_MASK,
                            laplace_relation_table[i].canonical, (int16_t)i, 0, -1, 1);\n            rel_slot_insert(k_relation_canonical_bucket, LAPLACE_REL_BUCKET_MASK,
                            laplace_relation_table[i].canonical, (int16_t)i, 0, -1, 0);\n        }}
        ids_mark_ready();\n    }} else {{
        while (!ids_ready()) {{ }}\n    }}
}}\n
static void relation_text_ids_ensure(void) {{\n    if (text_ids_ready()) return;
    if (text_identity_scalar_floor() == NULL) return;\n    if (text_ids_try_begin()) {{
        for (size_t i = 0; i < laplace_relation_table_count; ++i) {{\n            hash128_t text_id;
            if (hash128_text(laplace_relation_table[i].canonical,\n                             strlen(laplace_relation_table[i].canonical), &text_id) == 0)
                rel_id_bucket_insert(&text_id, (int16_t)i);\n        }}
        for (size_t i = 0; i < laplace_relation_alias_table_count; ++i) {{\n            hash128_t text_id;
            if (hash128_text(laplace_relation_alias_table[i].surface,\n                             strlen(laplace_relation_alias_table[i].surface), &text_id) == 0)
                rel_id_bucket_insert(&text_id, laplace_relation_alias_table[i].canon_idx);\n        }}
        text_ids_mark_ready();\n    }} else {{
        while (!text_ids_ready()) {{ }}\n    }}
}}\n
static int table_entry_type_id(size_t idx, hash128_t* out_type_id) {{\n    if (idx >= laplace_relation_table_count || !out_type_id) return -1;
    relation_ids_ensure();\n    *out_type_id = k_relation_type_id_cache[idx];
    return 0;\n}}
\nint laplace_relation_type_id(const char* canonical_name, hash128_t* out_type_id) {{
    if (!canonical_name || !out_type_id) return -1;\n    relation_ids_ensure();
    {{\n        int16_t idx = -1;
        if (rel_slot_lookup(k_relation_canonical_bucket, LAPLACE_REL_BUCKET_MASK,\n                            canonical_name, &idx, NULL) == 0)
            return table_entry_type_id((size_t)idx, out_type_id);\n    }}
    return type_id_from_canonical(canonical_name, out_type_id) == 0 ? 1 : -1;\n}}
\nint laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,\n                                     uint8_t* out_flip, hash128_t* out_parent_id) {{
    if (!surface || !out_type_id) return -1;\n    uint8_t flip = 0;
    int16_t idx = -1;\n    relation_ids_ensure();
    rel_slot_lookup(k_relation_surface_bucket, LAPLACE_REL_SURFACE_BUCKET_MASK,\n                    surface, &idx, &flip);
    if (idx < 0) {{\n        int rc = laplace_relation_type_id(surface, out_type_id);
        if (rc < 0) return rc;
        if (out_rank) *out_rank = {ranks.get('probationary', 0.09)};
        if (out_symmetry) *out_symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;\n        if (out_flip) *out_flip = 0;
        if (out_parent_id) hash128_zero(out_parent_id);\n        return 1;
    }}\n    const laplace_relation_def_t* def = &laplace_relation_table[idx];
    if (def->successor_idx != -1) return LAPLACE_REL_RETIRED;\n    if (table_entry_type_id((size_t)idx, out_type_id) != 0) return -1;
    if (out_rank) *out_rank = def->rank;\n    if (out_symmetry) *out_symmetry = def->symmetry;
    if (out_flip) *out_flip = flip;\n    if (out_parent_id) {{
        if (def->parent_idx >= 0)\n            table_entry_type_id((size_t)def->parent_idx, out_parent_id);
        else\n            hash128_zero(out_parent_id);
    }}\n    return 0;
}}\n
int laplace_relation_lookup(const hash128_t* type_id, const laplace_relation_def_t** out_def) {{\n    if (!type_id || !out_def) return -1;
    relation_ids_ensure();\n    relation_text_ids_ensure();
    size_t b = (size_t)(type_id->lo & LAPLACE_REL_BUCKET_MASK);\n    for (size_t probes = 0; probes < LAPLACE_REL_BUCKET_SIZE; ++probes) {{
        int16_t idx = k_relation_bucket[b].idx;\n        if (idx < 0) return -1;                 /* empty slot => not present */
        if (hash128_equals(type_id, &k_relation_bucket[b].key)) {{\n            *out_def = &laplace_relation_table[idx];
            return 0;\n        }}
        b = (b + 1) & LAPLACE_REL_BUCKET_MASK;\n    }}
    return -1;\n}}
\nint laplace_relation_surface_qualifier(const char* surface) {{
    if (!surface) return -1;\n    relation_ids_ensure();
    size_t b = (size_t)(rel_surface_hash(surface) & LAPLACE_REL_SURFACE_BUCKET_MASK);\n    for (size_t probes = 0; probes <= LAPLACE_REL_SURFACE_BUCKET_MASK; ++probes) {{
        const rel_surface_slot_t* slot = &k_relation_surface_bucket[b];\n        if (slot->surface == NULL) return -1;
        if (strcmp(slot->surface, surface) == 0) return slot->qualifier_bit;\n        b = (b + 1) & LAPLACE_REL_SURFACE_BUCKET_MASK;
    }}\n    return -1;
}}\n
int laplace_relation_retired(const hash128_t* type_id, const char** out_successor) {{\n    const laplace_relation_def_t* def = NULL;
    if (out_successor) *out_successor = NULL;\n    if (!type_id || laplace_relation_lookup(type_id, &def) != 0 || !def
        || def->successor_idx == -1)\n        return 0;
    if (out_successor)\n        *out_successor = def->successor_idx >= 0
            ? laplace_relation_table[def->successor_idx].canonical : "TRAJECTORY";\n    return 1;
}}\n
static int family_contains(int16_t idx, int16_t root_idx) {{\n    if (idx < 0) return 0;
    if (idx == root_idx) return 1;\n    int16_t cur = idx;
    for (int guard = 0; guard < 64; ++guard) {{\n        const laplace_relation_def_t* d = &laplace_relation_table[cur];
        if (d->family_root_idx == root_idx) return 1;\n        if (d->parent_idx < 0) return 0;
        if (d->parent_idx == root_idx) return 1;\n        cur = d->parent_idx;
    }}\n    return 0;
}}\n
int laplace_relation_in_family(const hash128_t* type_id, const char* family_root, int* out) {{\n    if (!type_id || !family_root || !out) return -1;
    *out = 0;\n    int16_t root_idx = -1;
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {{\n        if (cmp_str(laplace_relation_table[i].canonical, family_root) == 0) {{
            root_idx = (int16_t)i;\n            break;
        }}\n    }}
    if (root_idx < 0) return -1;\n    hash128_t entry_id;
    if (table_entry_type_id((size_t)root_idx, &entry_id) == 0\n        && hash128_equals(type_id, &entry_id)) {{
        *out = 1;\n        return 0;
    }}\n    const laplace_relation_def_t* def = NULL;
    if (laplace_relation_lookup(type_id, &def) == 0 && def) {{\n        *out = family_contains((int16_t)(def - laplace_relation_table), root_idx);
        return 0;\n    }}
    return 1;\n}}
\nint laplace_relation_set_ids(const char* set_name, const hash128_t** out_ids, size_t* out_count) {{
    if (!set_name || !out_ids || !out_count) return -1;\n    relation_ids_ensure();
    for (size_t i = 0; i < k_relation_set_count; ++i) {{\n        if (strcmp(k_relation_sets[i].name, set_name) == 0) {{
            *out_ids = &k_relation_set_ids_cache[k_relation_sets[i].offset];\n            *out_count = k_relation_sets[i].count;
            return 0;\n        }}
    }}\n    *out_ids = NULL;
    *out_count = 0;\n    return -1;
}}\n"""
    (OUT_CORE / "src/generated/relation_law.c").parent.mkdir(parents=True, exist_ok=True)
    _write_text_if_changed(OUT_CORE / "src/generated/relation_law.c", "\n".join(lines) + impl)

    by_name = {x["canonical"]: x for x in relations}
    def in_family(name, root):
        if name == root:
            return True
        cur = name
        for _ in range(64):
            d = by_name.get(cur)
            if d is None:
                return False
            if (d.get("family_root") or cur) == root:
                return True
            par = d.get("parent")
            if not par:
                return False
            if par == root:
                return True
            cur = par
        return False
    roots = sorted({(x.get("family_root") or x["canonical"]) for x in relations}
                   | {x.get("parent") for x in relations if x.get("parent")} | set(canon_names))
    fam_members = {r: [n for n in canon_names if in_family(n, r)] for r in roots}
    fam_lines = [
        "-- GENERATED by scripts/codegen-attestation-law.py - do not edit.",
        "-- Foldable family membership: see the note in the generator.",
        "CREATE OR REPLACE FUNCTION consensus.relation_family_ids(p_family text)",
        "    RETURNS bytea[]",
        "    LANGUAGE sql IMMUTABLE PARALLEL SAFE",
        "BEGIN ATOMIC",
        "    SELECT CASE p_family",
    ]
    for root in sorted(r for r, m in fam_members.items() if m):
        members = ", ".join(f"laplace.relation_type_id('{m}')" for m in sorted(fam_members[root]))
        fam_lines.append(f"        WHEN '{root}' THEN ARRAY[{members}]")
    fam_lines += ["        ELSE NULL::bytea[]", "    END;", "END;"]
    OUT_FAMILY_FRAG.parent.mkdir(parents=True, exist_ok=True)
    _write_text_if_changed(OUT_FAMILY_FRAG, "\n".join(fam_lines) + "\n")

    set_lines = [
        "-- GENERATED by scripts/codegen-attestation-law.py - do not edit.",
        "-- Foldable DECLARED relation sets: see the note in the generator.",
        "CREATE OR REPLACE FUNCTION consensus.relation_set_ids(p_set text)",
        "    RETURNS bytea[]",
        "    LANGUAGE sql IMMUTABLE PARALLEL SAFE",
        "BEGIN ATOMIC",
        "    SELECT CASE p_set",
    ]
    for nm in sorted(set_members):
        elems = ", ".join(f"laplace.relation_type_id('{m}')" for m in sorted(set_members[nm]))
        set_lines.append(f"        WHEN '{nm}' THEN ARRAY[{elems}]")
    set_lines += ["        ELSE NULL::bytea[]", "    END;", "END;"]
    _write_text_if_changed(OUT_SET_FRAG, "\n".join(set_lines) + "\n")


def _rank_to_band(rank_key: str) -> int:
    try:
        return _RANK_BANDS.index(rank_key)
    except ValueError:
        return len(_RANK_BANDS) - 1


def emit_relation_mask_manifest(rel: dict) -> None:
    """Generate relation selector constants from relation_types.toml (ADR 0001 / GH #551)."""
    relations = rel["relation"]
    HIGHWAY_MASK_BITS = 256
    missing = [r["canonical"] for r in relations if "bit" not in r]
    if missing:
        raise SystemExit(
            "codegen-attestation-law: every [[relation]] requires an explicit bit = N "
            f"(ADR 0001 / GH #551). Missing on: {', '.join(missing[:8])}"
            + ("…" if len(missing) > 8 else "")
        )
    name_to_rel = {r["canonical"]: r for r in relations}
    bit_to_name: dict[int, str] = {}
    for r in relations:
        name, bit = r["canonical"], int(r["bit"])
        if bit < 0 or bit >= HIGHWAY_MASK_BITS:
            raise SystemExit(f"codegen-attestation-law: {name} bit={bit} out of range [0, {HIGHWAY_MASK_BITS})")
        if bit in bit_to_name:
            raise SystemExit(f"codegen-attestation-law: bit {bit} claimed by both {bit_to_name[bit]!r} and {name!r} — bits are append-only and unique")
        bit_to_name[bit] = name

    ordered = sorted(bit_to_name.items())
    N, N_BANDS = len(ordered), len(_RANK_BANDS)
    if N > HIGHWAY_MASK_BITS:
        raise SystemExit(f"codegen-attestation-law: {N} governed relations exceed the {HIGHWAY_MASK_BITS}-bit highway mask.")

    band_masks: list[bytearray] = [bytearray(32) for _ in range(N_BANDS)]
    for bit_pos, name in ordered:
        band = _rank_to_band(name_to_rel[name]["rank"])
        band_masks[band][bit_pos // 8] |= 1 << (bit_pos % 8)

    bucket_size = 1
    while bucket_size < N * 4:
        bucket_size <<= 1
    bucket_mask = bucket_size - 1

    lines = [
        "#pragma once", "",
        "/* Generated from engine/manifest/relation_types.toml — do not edit. */",
        "/* Bits are the explicit append-only registry (ADR 0001 / GH #551). */", "",
        f"#define LAPLACE_HIGHWAY_REL_COUNT   {N}u",
        f"#define LAPLACE_HIGHWAY_BAND_COUNT  {N_BANDS}u",
        f"#define HIGHWAY_BUCKET_SIZE         {bucket_size}u",
        f"#define HIGHWAY_BUCKET_MASK         {bucket_mask}u", "",
        "/* Bit position of each relation type in the 256-bit highway mask */",
    ]
    lines += [f"#define HIGHWAY_BIT_{name:<50} {bit_pos}u" for bit_pos, name in ordered]
    lines += ["", "/* Band index constants */"]
    lines += [f"#define HIGHWAY_BAND_{band_name.upper():<45} {band_idx}u" for band_idx, band_name in enumerate(_RANK_BANDS)]
    lines += ["", "#include <stdint.h>",
              "typedef struct { uint8_t bit, band; } highway_relation_selector;",
              "static const highway_relation_selector highway_relation_selectors[] = {"]
    lines += [f"    {{ {name_to_rel[name]['bit']}, {_rank_to_band(name_to_rel[name]['rank'])} }}, /* {name} */" for name in sorted(name_to_rel)]
    lines += ["};", "static const uint64_t highway_relation_band_masks[][4] = {"]
    for mask in band_masks:
        words = [int.from_bytes(mask[i:i+8], "little") for i in range(0, 32, 8)]
        lines.append("    { " + ", ".join(f"UINT64_C(0x{w:016x})" for w in words) + " },")
    lines += ["};"]
    _write_text_if_changed(OUT_CORE / "include/laplace/core/highway_manifest.h", "\n".join(lines) + "\n")


def main() -> int:
    global CHECK_MODE
    CHECK_MODE = "--check" in sys.argv[1:]
    rel = parse_simple_toml(MANIFEST / "relation_types.toml")
    emit_relation_law(rel)
    emit_pos_law()
    emit_entity_type_law(MANIFEST / "entity_types.toml")
    emit_language_law(MANIFEST / "languages.tsv")
    emit_deprel_law()
    emit_qualifier_law(MANIFEST / "qualifiers.toml", {r["canonical"] for r in rel["relation"]})
    emit_trust_class_law(MANIFEST / "trust_classes.toml")
    emit_firmware_law(MANIFEST / "firmware.toml")
    emit_relation_mask_manifest(rel)
    if CHECK_MODE:
        if DRIFT:
            print("generated outputs differ from engine/manifest", file=sys.stderr)
            for path in DRIFT:
                try:
                    path = path.relative_to(ROOT)
                except ValueError:
                    pass
                print(f"  {path}", file=sys.stderr)
            return 1
        print("codegen check ok")
        return 0
    print("codegen ok:", OUT_CORE / "src/generated/relation_law.c")
    return 0


if __name__ == "__main__":
    sys.exit(main())
