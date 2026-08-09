# Perfcache blob law

PostgreSQL is the system of record. A perfcache blob is a deterministic, derived,
one-way runtime acceleration structure. It is never independent truth, never the only
copy of testimony, and never a manual data store.

Every blob format defines:

- magic, version, byte order, record layout, and bounds;
- source database generation/fingerprint;
- relation/manifest/recipe compatibility inputs;
- complete-file checksum and, where needed, section checksums;
- deterministic ordering and duplicate handling;
- writer, loader, validation, and stale-file behavior;
- the canonical PostgreSQL operation that rebuilds or verifies it.

Loaders reject missing, truncated, corrupt, incompatible, or stale blobs explicitly.
They do not partially load and continue. Publication uses write-to-temporary, flush,
checksum, and atomic replace. Readers map immutable files and never mutate their source.

Cache contents preserve typed meaning. A point cache stores point facts; a trajectory
cache stores ordered manifests; a model-factor cache stores versioned factor records.
Packing values into one binary format does not erase their semantic classes.

Perfcache usage must preserve parity with the canonical database/native reference path.
Parity includes ids, ordering, scores, unknown behavior, and source scope. Performance
tests do not replace semantic parity tests.

The active roster and delivery progress of blobs belongs in code, generated
inventory, and GitHub issues—not this law.
