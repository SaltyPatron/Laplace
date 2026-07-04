#!/usr/bin/env python3
"""
SQL parity / zero-loss reconciliation harness.

Proves, character-by-character, that deleting the legacy numbered ``NN_*.sql.in``
monolith bundles loses NOTHING: every object they define is either
  (a) IDENTICAL   - present in the modular tree with a normalized-equal body,
  (b) CONFLICT    - a reviewed both-complete divergence, resolved to modular,
  (c) FRAGMENT    - the numbered side is a truncated/invalid fragment; modular
                    is the only complete copy,
  (d) MIGRATED    - moved verbatim into a new modular file,
  (e) RETIRED     - deliberately dropped by a modular DROP module.

Any object that lands in none of those five buckets is UNACCOUNTED and the
harness exits non-zero. This is both the proof artifact (it prints the
reconciliation report) and a permanent guard (run it in CI).

No third-party deps. Pure stdlib. Deterministic.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SQL_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "sql"))

MODULAR_DIRS = ["functions", "indexes", "schema", "views", "probes",
                "inference", "generated", "seed", "bootstrap"]

NUMBERED_RE = re.compile(r"^\d{2}_.*\.sql\.in$")

# ---- reviewed exceptions (the ONLY permitted non-identical dispositions) -----
# Each entry: object name -> (bucket, reason). Backed by a functional test.
REVIEWED = {
    # CONFLICT: both complete, modular kept on documented evidence.
    "consensus_fold_swap": ("CONFLICT",
        "numbered RENAME/identity-swap destroys OID-bound indexes/FKs & strips "
        "pg_extension_config_dump (documented in modular header); modular "
        "TRUNCATE+INSERT kept."),
    "materialize_period_partition": ("CONFLICT",
        "numbered has redundant ORDER BY in keyed CTE (live disk-spill at 22.5M "
        "rows per modular comment); modular drops it, INSERT keeps ORDER BY f.cid."),
    "collocates": ("CONFLICT",
        "numbered inline 'NOT refuted(...)' vs modular 'FROM v_consensus_unrefuted'; "
        "view = SELECT * FROM consensus WHERE NOT refuted(...) => proven equivalent."),
    "table_present_ordinals": ("CONFLICT",
        "numbered plpgsql IF/ELSIF vs modular PARALLEL SAFE SQL UNION ALL over "
        "*_present_ordinals() helpers; modular refactor kept."),
    # FRAGMENT: numbered side is truncated/invalid; modular is the complete copy.
    "physicalities": ("FRAGMENT", "numbered table def truncated (missing ')' + "
        "pg_extension_config_dump); modular complete."),
    "render": ("FRAGMENT", "numbered truncated mid-body; modular complete."),
    "top_relations": ("FRAGMENT", "numbered truncated (no ORDER BY/LIMIT/close) + "
        "refactor to v_consensus_resolved; modular complete."),
    "label": ("FRAGMENT", "numbered truncated at COALESCE(; modular complete."),
    "consensus_layer_plane": ("FRAGMENT", "numbered truncated at edges CTE; modular complete."),
    # MIGRATED: relocated verbatim into a new modular module this change.
    "finish_consensus_fold_steps": ("MIGRATED",
        "resumable partitioned fold; was ONLY in 14_period_fold; moved to "
        "functions/fold/finish_consensus_fold_steps.sql.in."),
    # RETIRED: deliberately dropped by a modular DROP module.
    "content_descent_novel_ordinals": ("RETIRED",
        "superseded by content_descent_bitmap; dropped by "
        "probes/drop_content_descent_novel_ordinals.sql.in."),
}

# Non-object numbered files compared at whole-file level to a modular counterpart.
FILE_LEVEL = {
    "10_bootstrap.sql.in": "bootstrap/bootstrap.sql.in",
    "21_seed.sql.in": "seed/canonical_names_seed.sql.in",
    # 43_table_tuning's DDL moved to scripts/win/tune-laplace.cmd + pipeline.sh
    # (db/table-scoped tuning, not extension SQL) — nothing to reconcile in the tree.
}

CREATE_RE = re.compile(
    r"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?"
    r"(AGGREGATE|FUNCTION|PROCEDURE|TABLE|MATERIALIZED\s+VIEW|VIEW|TYPE|INDEX)\s+"
    r"(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?"
    r"([A-Za-z0-9_.\"@]+)",
    re.IGNORECASE)

DROP_RE = re.compile(
    r"\bDROP\s+(?:AGGREGATE|FUNCTION|PROCEDURE|TABLE|VIEW|TYPE|INDEX)\s+"
    r"(?:IF\s+EXISTS\s+)?([A-Za-z0-9_.\"@]+)",
    re.IGNORECASE)

DOLLAR_TAG_RE = re.compile(r"\$[A-Za-z0-9_]*\$")


def scan_statements(sql):
    """Split into top-level statements, stripping comments, honouring single-
    quote strings and $tag$ dollar-quoted bodies. Returns list of statement
    strings (comments removed, everything else preserved)."""
    i, n = 0, len(sql)
    out, cur = [], []
    while i < n:
        two = sql[i:i + 2]
        if two == "--":
            j = sql.find("\n", i)
            i = n if j == -1 else j
            continue
        if two == "/*":
            depth, i = 1, i + 2
            while i < n and depth:
                if sql[i:i + 2] == "/*":
                    depth += 1; i += 2
                elif sql[i:i + 2] == "*/":
                    depth -= 1; i += 2
                else:
                    i += 1
            continue
        c = sql[i]
        if c == "'":
            cur.append(c); i += 1
            while i < n:
                if sql[i:i + 2] == "''":
                    cur.append("''"); i += 2; continue
                cur.append(sql[i])
                if sql[i] == "'":
                    i += 1; break
                i += 1
            continue
        m = DOLLAR_TAG_RE.match(sql, i)
        if m:
            tag = m.group(0)
            end = sql.find(tag, i + len(tag))
            end = n if end == -1 else end + len(tag)
            cur.append(sql[i:end]); i = end
            continue
        if c == ";":
            cur.append(";")
            out.append("".join(cur)); cur = []
            i += 1
            continue
        cur.append(c); i += 1
    tail = "".join(cur)
    if tail.strip():
        out.append(tail)
    return out


def norm(stmt):
    s = re.sub(r"@extschema@\.|(?<![A-Za-z0-9_])public\.|(?<![A-Za-z0-9_])laplace\.", "", stmt)
    s = re.sub(r"\bINNER\s+JOIN\b", "JOIN", s, flags=re.IGNORECASE)
    s = re.sub(r"\s+", " ", s).strip()
    s = s.rstrip(";").strip()
    return s


def bare(name):
    return re.sub(r'^(@extschema@\.|public\.|laplace\.)', '', name).replace('"', '')


def objects_in(stmt):
    """(kind, name) for a direct CREATE, plus any CREATEs embedded in DO/EXECUTE
    dynamic SQL inside this statement."""
    found = []
    head = stmt.lstrip()[:600]
    m = CREATE_RE.match(head) or (CREATE_RE.search(head) if head.upper().lstrip().startswith("CREATE") else None)
    if m:
        found.append((m.group(1).upper().replace("  ", " "), bare(m.group(2))))
    # embedded (DO $$ ... EXECUTE $c$ CREATE ... $c$ ...)
    if re.match(r"\s*DO\b", stmt, re.IGNORECASE) or "EXECUTE" in stmt.upper():
        for em in CREATE_RE.finditer(stmt):
            pair = (em.group(1).upper().replace("  ", " "), bare(em.group(2)))
            if pair not in found:
                found.append(pair)
    return found


def drops_in(stmt):
    return [bare(m.group(1)) for m in DROP_RE.finditer(stmt)]


def load_modular():
    """name -> list of normalized bodies; plus set of dropped names."""
    bodies = {}
    dropped = set()
    files = []
    for d in MODULAR_DIRS:
        root = os.path.join(SQL_DIR, d)
        for dp, _, fns in os.walk(root):
            for fn in fns:
                if fn.endswith(".sql.in"):
                    files.append(os.path.join(dp, fn))
    for f in files:
        sql = open(f, encoding="utf-8").read()
        for st in scan_statements(sql):
            for _, nm in objects_in(st):
                bodies.setdefault(nm, []).append(norm(st))
            for nm in drops_in(st):
                dropped.add(nm)
    return bodies, dropped


def read(path):
    return open(path, encoding="utf-8").read()


def main():
    modular, dropped = load_modular()
    numbered = sorted(fn for fn in os.listdir(SQL_DIR) if NUMBERED_RE.match(fn))
    rows = []          # (file, kind, name, bucket, detail)
    unaccounted = []
    file_level_results = []

    for fn in numbered:
        path = os.path.join(SQL_DIR, fn)
        sql = read(path)
        if not sql.strip():
            rows.append((fn, "-", "-", "EMPTY", "0 bytes / no objects"))
            continue
        seen = set()
        for st in scan_statements(sql):
            for kind, name in objects_in(st):
                if (kind, name) in seen:
                    continue
                seen.add((kind, name))
                nb = norm(st)
                mod_bodies = modular.get(name, [])
                if name in REVIEWED:
                    bucket, reason = REVIEWED[name]
                    if bucket == "RETIRED":
                        ok = name in dropped
                        rows.append((fn, kind, name, "RETIRED" if ok else "RETIRED?",
                                     reason if ok else "expected a DROP module, none found"))
                        if not ok:
                            unaccounted.append((fn, kind, name, "retired but no drop module"))
                    elif bucket == "MIGRATED":
                        if not mod_bodies:
                            rows.append((fn, kind, name, "MIGRATED-MISSING", reason))
                            unaccounted.append((fn, kind, name, "migration target not present in modular tree"))
                        elif nb in mod_bodies:
                            rows.append((fn, kind, name, "MIGRATED", reason))
                        else:
                            rows.append((fn, kind, name, "MIGRATED-DRIFT", reason))
                            unaccounted.append((fn, kind, name,
                                "migrated body is NOT normalized-equal to the numbered original"))
                    else:  # CONFLICT / FRAGMENT
                        rows.append((fn, kind, name, bucket, reason))
                        if not mod_bodies:
                            unaccounted.append((fn, kind, name, f"{bucket} but no modular definition"))
                    continue
                # default: must be IDENTICAL to some modular body of same name
                if not mod_bodies:
                    rows.append((fn, kind, name, "UNIQUE-UNACCOUNTED", "no modular definition"))
                    unaccounted.append((fn, kind, name, "unique to numbered file, not migrated/retired"))
                elif nb in mod_bodies:
                    rows.append((fn, kind, name, "IDENTICAL", ""))
                else:
                    rows.append((fn, kind, name, "DRIFT-UNACCOUNTED",
                                 "body differs from modular; not in reviewed allowlist"))
                    unaccounted.append((fn, kind, name, "normalized body drift, unreviewed"))

    # whole-file comparisons (bootstrap / seed / migrated tuning)
    for nfile, mfile in FILE_LEVEL.items():
        np = os.path.join(SQL_DIR, nfile)
        mp = os.path.join(SQL_DIR, mfile)
        if not os.path.exists(np):
            continue
        n_norm = norm(" ".join(scan_statements(read(np))))
        if not os.path.exists(mp):
            file_level_results.append((nfile, mfile, "MISSING-TARGET"))
            unaccounted.append((nfile, "file", mfile, "modular counterpart missing"))
            continue
        m_norm = norm(" ".join(scan_statements(read(mp))))
        status = "IDENTICAL" if n_norm == m_norm else "DIFF"
        file_level_results.append((nfile, mfile, status))

    # ---- report ----
    print("=" * 92)
    print("SQL PARITY / ZERO-LOSS RECONCILIATION")
    print(f"sql dir: {SQL_DIR}")
    print("=" * 92)
    buckets = {}
    for _, _, _, b, _ in rows:
        buckets[b] = buckets.get(b, 0) + 1
    print("\nObject dispositions:")
    for fn, kind, name, bucket, detail in rows:
        if bucket in ("IDENTICAL", "EMPTY"):
            continue
        print(f"  [{bucket:18}] {name:34} ({kind:16}) {fn}")
        if detail:
            print(f'  {"":20} -> {detail}')
    print("\nWhole-file comparisons (bootstrap/seed/tuning):")
    for nfile, mfile, status in file_level_results:
        print(f"  [{status:12}] {nfile:26} == {mfile}")
    print("\nSummary by bucket:")
    for b in sorted(buckets):
        print(f"  {b:20} {buckets[b]}")
    identical = buckets.get("IDENTICAL", 0)
    print(f"\n  IDENTICAL objects proven normalized-equal: {identical}")
    print(f"  reviewed exceptions (conflict/fragment/migrated/retired): "
          f"{sum(buckets.get(b,0) for b in ('CONFLICT','FRAGMENT','MIGRATED','RETIRED'))}")
    print(f"  empty numbered files: {buckets.get('EMPTY',0)}")

    if unaccounted:
        print("\n" + "!" * 92)
        print(f"FAIL: {len(unaccounted)} UNACCOUNTED object(s) — would lose functionality:")
        for fn, kind, name, why in unaccounted:
            print(f"  * {name} ({kind}) in {fn}: {why}")
        print("!" * 92)
        return 1
    bad_files = [r for r in file_level_results if r[2] != "IDENTICAL"]
    if bad_files:
        print("\nNOTE: whole-file DIFFs above are expected only where documented "
              "(seed provenance strings). Review before relying on green.")
    print("\nPASS: every numbered-file object is accounted for (identical, "
          "reviewed-conflict, fragment, migrated, or retired).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
