# SQL corpus auditor

`scripts/sql-audit.py` inventories the repository's SQL as one corpus. It finds
exact and structurally similar implementations, extracts SQL embedded in source
strings, and emits ranked static review candidates. The tool complements the
measured investigations in `docs/sql-refactor-tasklist.md` and the installed
function cascade in `docs/sql-cascade.md`; it does not replace either PostgreSQL's
parser or execution-plan measurement.

## Run it

From the repository root:

```bash
python3 scripts/sql-audit.py \
  --json build/sql-audit/findings.json \
  --markdown build/sql-audit/report.md
```

The Markdown report is the review queue. The JSON file is the complete,
machine-readable result for further clustering, dashboards, or remediation
scripts. Both paths live under `build/` so normal audit runs do not dirty the
working tree.

Run the regression suite independently:

```bash
python3 scripts/test-sql-audit.py
```

CI runs the regression suite and a fast high-severity ratchet:

```bash
python3 scripts/sql-audit.py --skip-near-clones --fail-on high
```

The ratchet intentionally does not fail on the existing medium/low review queue.
Near-clone detection is the most expensive stage and cannot currently produce a
high-severity result, so CI skips it while full local audits retain it.

## What is analyzed

The default discovery pass includes `.sql`, `.sql.in`, `.psql`, and `.pgsql`
files. It also lexes SQL strings from C, C++, C#, Python, JavaScript, and
TypeScript sources. Build outputs, dependencies, archived workflows, Git
worktrees, and the auditor's synthetic test fixtures are excluded by
`scripts/sql-audit-config.json`.

PostgreSQL dollar-quoted bodies are one lexical token for top-level statement
splitting, then are recursively normalized and split into body-level audit units.
Consequently, semicolons in PL/pgSQL do not corrupt the file inventory, while
duplicated queries inside two different functions can still be matched.

Each statement has two fingerprints:

- Exact normalization removes comments and formatting and canonicalizes
  unquoted keywords and identifiers.
- Structural normalization additionally replaces literals, numeric constants,
  and parameters. Five-token shingles and Jaccard similarity find functions or
  queries that are the same shape with limited predicate, alias, projection,
  ordering, or literal drift.

Exact and near-clone output is deliberately evidence, not an automatic rewrite.
Repeated test fixtures, upgrade drops, and seed-data statements can be valid.
Production clusters should be checked for caller-contract and result parity
before selecting a canonical implementation.

## Static rule families

The initial rules cover high-signal correctness, security, planner, and
maintainability candidates:

- unsafe NULL predicate comparison and nullable `NOT IN` semantics;
- `SECURITY DEFINER` without a fixed function `search_path`;
- set-returning functions that inherit PostgreSQL's 1,000-row estimate;
- numeric default caps that can silently truncate a result contract;
- `LIMIT` without visible ordering;
- whole-relation `UPDATE` or `DELETE` candidates;
- implicit SQL-function volatility;
- wildcard projection, duplicate-eliminating `UNION`, existence-by-`count(*)`,
  `ORDER BY random()`, pattern predicates, and materialization fences;
- repository-specific measured fan-out primitives and functions wrapping known
  partition/index keys.

Test and generated-code findings remain visible but are downgraded one severity.
Repository-specific primitives live in `scripts/sql-audit-config.json`; the list
must reflect current measurements. A repaired hot primitive should be removed
rather than leaving stale folklore in the gate.

## Baselines and enforcement

Finding and clone identifiers are content-derived. To freeze a reviewed queue:

```bash
python3 scripts/sql-audit.py --write-baseline build/sql-audit/baseline.json
python3 scripts/sql-audit.py \
  --baseline build/sql-audit/baseline.json \
  --fail-on medium
```

Only unbaselined results at or above the chosen severity fail. Do not create a
repository baseline merely to make a red gate green: first classify each result
as a defect, intentional duplication, measured exception, or rule false
positive. Prefer a narrow configuration/rule correction for false positives.

## Limits of static evidence

The scanner is lexical, not a PostgreSQL grammar or catalog model. It cannot
prove cardinality, indexability, partition pruning, lock behavior, volatility,
or semantic equivalence. Performance remediation still requires representative
fixtures, result fingerprints, and `EXPLAIN (ANALYZE, BUFFERS, WAL, SETTINGS)`.
The corpus report answers where to measure and which repeated class to sweep; it
must not be used to justify blind query rewrites.
