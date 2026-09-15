#!/usr/bin/env python3
"""Apply the exact retained generation_corpus fixture repair; removed after use."""
from pathlib import Path
import subprocess

base = '927fdc19149861c1181598cff8ccf348485cf25a'
paths = ('extension/laplace_substrate/tests/sql/generation_corpus.sql',
         'extension/laplace_substrate/tests/expected/generation_corpus.out')
changes = [
    ('DO $membership_bitmap$\nDECLARE',
     '-- Keep this stress fixture independent of heap compression and planner tuning.\n'
     '-- Its savepoint also removes the load and restores every SET LOCAL below.\n'
     'SAVEPOINT membership_bitmap_fixture;\nDO $membership_bitmap$\nDECLARE'),
    ('BEGIN\n    SELECT public.ST_MakeLine(array_agg(public.laplace_mantissa_pack(needle,i,1,4) ORDER BY i)),',
     'BEGIN\n    ALTER TABLE laplace.physicalities ALTER COLUMN trajectory SET STORAGE PLAIN;\n'
     '    SELECT public.ST_MakeLine(array_agg(public.laplace_mantissa_pack(needle,i,1,4) ORDER BY i)),'),
    ("    PERFORM set_config('enable_seqscan','off',true);\n    EXECUTE 'EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) SELECT entity_id",
     "    PERFORM set_config('enable_seqscan','off',true);\n"
     "    PERFORM set_config('enable_indexscan','off',true);\n"
     "    PERFORM set_config('enable_indexonlyscan','off',true);\n"
     "    PERFORM set_config('enable_bitmapscan','on',true);\n"
     "    PERFORM set_config('max_parallel_workers_per_gather','0',true);\n"
     "    EXECUTE 'EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) SELECT entity_id"),
    ("RAISE EXCEPTION 'FAIL: membership recheck fixture did not exercise a lossy bitmap';",
     "RAISE EXCEPTION 'FAIL: membership recheck fixture did not exercise a lossy bitmap: %',plan;"),
    ("    PERFORM set_config('work_mem','4MB',true);\n    PERFORM set_config('enable_seqscan','on',true);\nEND\n$membership_bitmap$;",
     'END\n$membership_bitmap$;\nROLLBACK TO SAVEPOINT membership_bitmap_fixture;\nRELEASE SAVEPOINT membership_bitmap_fixture;'),
]
for name in paths:
    path = Path(name)
    original = subprocess.check_output(['git', 'show', f'{base}:{name}'])
    if path.read_bytes() != original:
        raise RuntimeError(f'preserve concurrent changes: {name}')
    text = original.decode()
    for before, after in changes:
        if text.count(before) != 1:
            raise RuntimeError(f'expected exactly one source location: {name}: {before}')
        text = text.replace(before, after, 1)
    path.write_text(text, encoding='utf-8')
path = Path('.github/workflows/laplace.yml')
text = path.read_text()
before = ('          python3 scripts/collect-recursive-proof-evidence.py "${args[@]}"\n'
          '          flock --exclusive --close /build/laplace/work/host-resource.lock \\\n'
          '            python3 scripts/inspect-recursive-proof-counterexamples.py \\\n'
          '              --collection "$evidence" --expected-source "$TARGET_SHA"')
after = ('          python3 scripts/collect-recursive-proof-evidence.py "${args[@]}"\n'
         '          # Bounded READ ONLY inspection has its own statement/lock deadlines.\n'
         '          # Do not queue a no-example result behind unrelated host-wide work.\n'
         '          python3 scripts/inspect-recursive-proof-counterexamples.py \\\n'
         '            --collection "$evidence" --expected-source "$TARGET_SHA"')
if text.count(before) != 1:
    raise RuntimeError('preserve concurrently changed product diagnostic')
path.write_text(text.replace(before, after, 1))
Path('.github/workflows/repo-hygiene.yml').write_bytes(subprocess.check_output([
    'git', 'cat-file', 'blob', '76aac11f558d58714682364952185f1ef873aed7']))
Path(__file__).unlink()
