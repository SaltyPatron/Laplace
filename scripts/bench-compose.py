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
            with open(p, "rb") as fh:
                b = fh.read()
        except OSError:
            continue
        if not b:
            continue
        docs.append(b)
        total += len(b)
        if total >= cap:
            break
    return docs


def _refuse_if_ingest_advancing():
    """Refuse to benchmark while an ingest is advancing.

    This harness touches NO database -- its own docstring says so -- so the substrate
    measurement lane's lock is the wrong instrument here. CPU contention is the right
    one: content_tree_build is single-threaded by construction, and a 12-core box
    running a 14-wide ingest does not give it a core to itself.

    MEASURED 2026-08-15, the general case this is an instance of:
    generation.compose_batch returned 81,701 ms to 316,998 ms for near-identical code
    with an ingest active -- a 3.9x spread that produced causal claims from single runs.
    A throughput floor quoted off a loaded box is the same error with a different unit.

    LIVENESS IS A COUNTER THAT MOVES, not the LPLK beacon: measured 2026-08-16, a
    demonstrably writing ConceptNet run held zero advisory locks for the database, so
    beacon-absence proves nothing. Set LAPLACE_BENCH_ALLOW_BUSY=1 to override
    deliberately; an unanswerable probe is NOT treated as busy here, because this
    harness must still run on a machine with no substrate at all.
    """
    if os.environ.get("LAPLACE_BENCH_ALLOW_BUSY") == "1":
        return
    import subprocess
    q = ("SELECT coalesce(sum(input_units_done),0) FROM laplace.ingest_run_journal "
         "WHERE status = 'running'")
    def sample(sql=q):
        try:
            out = subprocess.run(
                ["psql", "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
                 "-U", os.environ.get("PGUSER", "laplace_admin"),
                 "-d", os.environ.get("PGDATABASE", "laplace"), "-tAc", sql],
                capture_output=True, text=True, timeout=15)
            return int(out.stdout.strip()) if out.returncode == 0 and out.stdout.strip().isdigit() else None
        except Exception:
            return None
    a = sample()
    if a is None or a == 0:
        return                      # no substrate, or nothing running: proceed

    # CONTINUOUS EVIDENCE BEATS A SAMPLED COUNTER. MEASURED 2026-08-16: with a
    # ConceptNet seed live, input_units_done sat frozen at 1,430,350 for 16 SECONDS
    # while 12 backends ran consensus.upsert / COPY laplace.* without pause.
    # ProgressInterval is a 5 s rate-limit FLOOR on journal writes, not a cadence, and
    # the decomposer reports in batch-sized jumps -- so a short window lands inside one
    # and calls a live ingest idle. This harness's first version did exactly that and
    # benchmarked the core at 1,646.4k codepoints/s on a box running a 14-wide ingest.
    busy = sample(
        "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() "
        "AND state = 'active' AND pid <> pg_backend_pid() AND query ~* "
        "'consensus\\.upsert|COPY laplace\\.|highway_mask_deposit|attestations_exist|physicalities_exist'")
    if busy:
        sys.exit(
            f"bench-compose: refusing — {busy} backend(s) are executing ingest work right now. "
            "content_tree_build is single-threaded and this box is not idle; the number would "
            "measure the load, not the core. Set LAPLACE_BENCH_ALLOW_BUSY=1 to override.")

    time.sleep(6)
    b = sample()
    if b is not None and b > a:
        sys.exit(
            f"bench-compose: refusing — an ingest is ADVANCING ({a} -> {b} units). "
            "Set LAPLACE_BENCH_ALLOW_BUSY=1 to override.")


def main():
    _refuse_if_ingest_advancing()
    # Parse positionally-independent: `--repeats N` alone must work, and the old
    # form took sys.argv[1] as the corpus unconditionally, so it set root to
    # "--repeats" and then found no corpus.
    repeats = 3
    root = None
    argv = sys.argv[1:]
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--repeats":
            if i + 1 >= len(argv):
                sys.exit("bench-compose: --repeats needs a value")
            try:
                repeats = int(argv[i + 1])
            except ValueError:
                sys.exit(f"bench-compose: --repeats expects an integer, got {argv[i + 1]!r}")
            if repeats < 1:
                sys.exit("bench-compose: --repeats must be >= 1")
            i += 2
            continue
        if a.startswith("-"):
            sys.exit(f"bench-compose: unknown option {a!r}")
        if root is not None:
            sys.exit("bench-compose: corpus_dir given more than once")
        root = a
        i += 1
    if root is None:
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

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
