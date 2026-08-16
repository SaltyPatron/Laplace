# Operator portal guide — running, stopping, repairing, retracting

Operational how-to for the **Operator** tab (`/operator`, `web/src/admin/`) and
the operations behind it. External-agent routing has its own guide:
[external-agents.md](external-agents.md). Verify names against `ops.api()` and
`GET /v1/admin/ops/policy` if this drifts.

## There is no privilege boundary here

Say it before anything else, because the console's capability grew: every
operation on this page is reachable directly over HTTP without it. The tenant is
a free-text header any caller can set. With authentication stubbed, **anyone who
can reach the host can cancel a backend, rebuild an index, and retract a
source's testimony.** The page banner says the same thing. This is a convenience
over public endpoints, not an admin boundary, and it must not be mistaken for one.

## The five sections

| Section | Answers | Backed by |
|---|---|---|
| **Ingest** | did that ingest finish, and is a pipeline still gated on it? | `ops.ingest_runs`, `ops.ingest_run_close` |
| **Activity** | what is running right now, and how do I stop it? | `ops.activity`, `ops.cancel_backend`, `ops.terminate_backend` |
| **Ops** | run any installed operation by name | `ops.api` + `POST /v1/op` |
| **Repair** | what is damaged, and how do I fix it? | `ops.index_health`, `ops.reindex_invalid`, `ops.analyze_substrate`, `POST /v1/admin/maintenance/vacuum` |
| **Agents** | which external models are routable, and does one work? | `GET/PUT /v1/admin/agents*` |

## Why almost everything is an installed operation

`POST /v1/op` resolves a name against the live `ops.api()` catalog and refuses
anything outside it — a named call, never SQL text. Adding an operation to the
substrate makes it callable from the console, the CLI, the MCP `op` tool and psql
at once, with no rebuild and no restart of a stdio child nobody owns (GH #809).

Two things cannot be installed operations, and they are the only endpoints in
`EndpointMappings.Admin.cs`:

* **`agents.json`** is a file on the host, not a substrate relation.
* **`VACUUM`** is refused inside a transaction block, and a PL/pgSQL procedure
  body is always in one — it cannot be wrapped at any nesting.

## Writes are a second, deliberately short list

The catalog being an allow-list is not enough to make a mutation callable: every
op resolves onto a `default_transaction_read_only=on` datasource unless it is
also named in `InstalledOpInvoker.WritableOps`. Installing a write op does **not**
silently make it reachable over HTTP; someone adds it there on purpose.

`GET /v1/admin/ops/policy` serves that list, and the console badges from it rather
than guessing from the operation's name. The regex it replaced was wrong in both
directions — `ops.evict_source` matched nothing in it and is the most destructive
call on the surface.

Currently writable: `ingest_run_close`, `cancel_backend`, `terminate_backend`,
`reindex_invalid`, `analyze_substrate`, `evict_source`. Of those, **only
`evict_source` destroys stored testimony**, and it is the one entry flagged
destructive.

## Cancelling

`ops.ingest_run_close` edits the **journal row**. On its own that is worse than no
cancel at all: the pipeline gate goes green while the ingest keeps writing. Signal
the process first, then close the row.

* **Cancel** (`pg_cancel_backend`) ends the running statement and leaves the
  session to roll back cleanly. It is the recoverable move and needs no
  confirmation.
* **Terminate** (`pg_terminate_backend`) drops the connection and loses whatever
  transaction was open. It is confirmed, and is for a backend that ignored a
  cancel — an uninterruptible wait.

Both refuse three wrong targets: a pid on another database (the signal functions
are cluster-wide, so a typo reaches a neighbour), this session, and a pid that is
not running. Both capture `state`/`query` **before** signalling — reading after
races the backend's teardown, and "what did I just kill?" is the question an audit
asks. `signalled=false` means the role lacks `pg_signal_backend`.

`ops.activity` reports `restricted=true` when the role lacks `pg_read_all_stats`
and Postgres masked `state`/`query`. That is **not** the same as idle — an idle
session still reports `state='idle'` — and the console says so rather than letting
a masked substrate read as a quiet one.

## Repairing

`ops.index_health` returns one row per non-valid index; an **empty set is the
healthy answer**. `leaf_count` 0 on a partitioned parent is the 2026-08-13 shell
class: present in the catalog, unusable for reads, invisible to row counts.

`ops.reindex_invalid` rebuilds them, **one COMMIT per index**, so the run is
resumable and cancellable — cancel from Activity and every index already rebuilt
stays rebuilt. It is deliberately **not** `CONCURRENTLY`: that is barred inside a
procedure, and the targets are already invalid, so reads cannot use them and the
outage began before the repair did. Dry-run first; the plan goes to the server log,
readable as SQL through `ops.app_log`.

`ANALYZE` fixes the class of slowdown where the right index exists and the planner
will not choose it. `VACUUM FULL` rewrites the table under `ACCESS EXCLUSIVE` and
needs free disk equal to the table's size — the console confirms it separately.

## Retracting a source

`ops.evict_source` is lawful retraction, not a delete: it removes the source's
evidence, **refolds** every touched cell from the evidence that survives, deletes
cells left with zero witnesses (unattested is not attested-false — GH #688/#689),
queues the touched entities for highway-mask repair, and removes the lane's marker
entities so the hydrator re-opens every unit. Content entities are never deleted;
content is shared under the identity law.

It commits per batch, so it is resumable and interruptible. It is irreversible in
the sense that matters: re-deriving the testimony means re-running the lane.

## Long operations do not block on a timeout

A CALLed procedure gets `MaxProcedureTimeoutSeconds` (6h) rather than the 15s
read default, because a reindex of 28 partitioned parents is not a 15-second
question. They are bounded by being **cancellable**, not by a number: Activity
finds the pid, Cancel stops it, and every COMMIT already taken survives.

## Installing the operations

`ops.activity`, `ops.cancel_backend`, `ops.terminate_backend`,
`ops.reindex_invalid` and `ops.analyze_substrate` are new extension members, and
`ops.api` and `ops.index_health` changed shape. They reach a running substrate
through the normal extension upgrade (`manifest.upgrade` →
`ALTER EXTENSION laplace_substrate UPDATE`), not by editing the database. Until
that upgrade runs, the Activity and Repair tabs report the operation as absent
from `ops.api()` — which is the honest answer, not a UI fault.

Behaviour is pinned by `extension/laplace_substrate/tests/sql/ops_control.sql`
under pg_regress: the row shapes, the `kind` split between CALL and SELECT, and
every refusal.
