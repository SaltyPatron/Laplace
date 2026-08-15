#!/usr/bin/env python3
"""Core composition throughput — the number CLAUDE.md 8 asserts and nothing measured.

Measures the CORE, not a read path and not the ingest pipeline: UTF-8 in,
content_tree_build out (UAX #29 segmentation + merkle ids + glome placement for
every node). No database, no COPY, no network. That is the operation the
">=500k tokens/sec floor on commodity CPU" claim is about, and until now the
repo had no harness for it -- `just --list | grep bench` and
`ninja -t targets all | grep bench` both come back empty.

Reports codepoints/s because that is what the core consumes, and a
BPE-equivalent at 4 chars/token so the figure is comparable to how model
throughput is quoted. Single-threaded by construction: content_tree_build is
lock-free and per-call, so a core count multiplies this, and saying "per core"
keeps the number honest.

Usage: python3 scripts/bench-compose.py [corpus_dir] [--repeats N]
"""
import ctypes, os, sys, time, glob

CORE = os.environ.get("LAPLACE_CORE", "/opt/laplace/lib/liblaplace_core.so")
T0 = os.environ.get("LAPLACE_T0",
                    "/opt/laplace/share/laplace/laplace_t0_perfcache_17.0.0.bin")

lib = ctypes.CDLL(CORE)
lib.codepoint_table_load_perfcache.argtypes = [ctypes.c_char_p]
lib.codepoint_table_load_perfcache.restype = ctypes.c_int
lib.codepoint_table_is_loaded.restype = ctypes.c_int
lib.content_witness_tree_build.argtypes = [ctypes.c_char_p, ctypes.c_size_t,
                                           ctypes.POINTER(ctypes.c_void_p)]
lib.content_witness_tree_build.restype = ctypes.c_int
lib.tier_tree_free.argtypes = [ctypes.c_void_p]
lib.tier_tree_node_count.argtypes = [ctypes.c_void_p]
lib.tier_tree_node_count.restype = ctypes.c_size_t


SKIP_DIRS = ("/build/", "/.git/", "/external/", "/node_modules/", "/bin/", "/obj/")
CORPUS_CAP_BYTES = 48 << 20   # 48 MB


def load_corpus(root, cap=CORPUS_CAP_BYTES):
    """Real prose and code, not synthetic text -- segmentation cost depends on
    what the text actually is, and a corpus of one repeated character would
    measure the wrong thing (pi's million digits compose as ONE word).

    BOUNDED AND FIRST-PARTY. Globbing everything on disk pulled 1.5 GB across
    13,489 files, dominated by vendored external/ source: too slow to repeat,
    and not this project's text. Sorted so the selection is the same corpus on
    every run -- a throughput figure that silently changes its own input is
    not a benchmark."""
    paths = []
    for pat in ("**/*.md", "**/*.txt", "**/*.cs", "**/*.c", "**/*.h"):
        for p in glob.glob(os.path.join(root, pat), recursive=True):
            if any(s in p for s in SKIP_DIRS):
                continue
            paths.append(p)
    docs, total = [], 0
    for p in sorted(paths):
        try:
            b = open(p, "rb").read()
        except OSError:
            continue
        if not b:
            continue
        docs.append(b)
        total += len(b)
        if total >= cap:
            break
    return docs


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(
        os.path.abspath(__file__)))
    repeats = 3
    if "--repeats" in sys.argv:
        repeats = int(sys.argv[sys.argv.index("--repeats") + 1])

    if lib.codepoint_table_load_perfcache(T0.encode()) != 0 or not lib.codepoint_table_is_loaded():
        sys.exit(f"bench-compose: could not load T0 perfcache at {T0}")

    docs = load_corpus(root)
    if not docs:
        sys.exit(f"bench-compose: no corpus under {root}")
    total_bytes = sum(len(d) for d in docs)
    total_cps = sum(len(d.decode("utf-8", "replace")) for d in docs)

    print(f"corpus      : {len(docs):,} documents, {total_bytes/1e6:.1f} MB, "
          f"{total_cps:,} codepoints")
    print(f"core        : {CORE}")

    best = None
    for r in range(repeats):
        nodes = 0
        failed = 0
        t0 = time.perf_counter()
        for d in docs:
            tree = ctypes.c_void_p()
            if lib.content_witness_tree_build(d, len(d), ctypes.byref(tree)) != 0:
                failed += 1
                continue
            nodes += lib.tier_tree_node_count(tree)
            lib.tier_tree_free(tree)
        el = time.perf_counter() - t0
        cps = total_cps / el
        print(f"  run {r+1}: {el:7.3f} s   {cps/1e3:9.1f}k codepoints/s   "
              f"{cps/4/1e3:8.1f}k BPE-equiv tok/s   {nodes:,} nodes"
              + (f"   [{failed} rejected]" if failed else ""))
        if best is None or el < best[0]:
            best = (el, cps, nodes)

    el, cps, nodes = best
    print()
    print(f"BEST, single-threaded, no DB:")
    print(f"  {cps/1e3:,.1f}k codepoints/s   {cps/4/1e3:,.1f}k BPE-equiv tokens/s")
    print(f"  {nodes/el/1e3:,.1f}k tier-tree nodes/s   ({nodes:,} nodes built)")


if __name__ == "__main__":
    main()
