# Installed Laplace monorepo client

`laplace-mono` runs the CLI from `SaltyPatron/Laplace`. It does not invoke
Laplace-Refactor or the modular Laplace-Engine executable. The command is usable
from any directory after installation; relative input paths resolve from the
caller's directory.

```powershell
laplace-mono --where
laplace-mono ingest document "E:\Documents\selected folder"
laplace-mono db-roundtrip "E:\Documents\selected folder\notes.md" --read-only
laplace-mono chess record "E:\Chess\archive.pgn"
```

The same commands work in a Linux shell with Linux input paths. Document ingestion
selects UTF-8 `.txt`, `.md` and `.markdown` files recursively, inventories other
formats as unsupported and excludes nested dependency/build directories. Markdown
is admitted as its written text; this provider does not interpret its markup.

`db-roundtrip --read-only` computes the expected content ID locally, reconstructs
that content from PostgreSQL and compares the bytes. It performs no ingestion.
Without `--read-only`, that command first records the input and then reconstructs
it. A successful reconstruction does not establish correct semantic analysis.

The client declaration selects a runtime, database, TLS trust, data paths and
credential source. `--where` displays the selected public configuration.
`LAPLACE_MONO_CLIENT_CONFIG` selects a different declaration; credentials belong
in the enrolled secret store, not the declaration. No build directory, compiler
environment or administrator login is needed for ordinary use.

The Windows installer is `deploy/client/Install-LaplaceClient.ps1`, with explicit
`-Configuration`, `-Destination` and `-CommandDirectory` paths. The Linux installer
is `deploy/client/install-client.py`; its `--help` describes the matching paths.
Host provisioning, extension installation and schema migration remain separate
administrator operations owned by this repository's setup scripts.

Ordinary ingestion uses the shared transactional bulk writer. It does not create
staging schemas or replace database partitions. PostgreSQL maintains pending GIN
entries for workload roles through autovacuum; those entries remain queryable.
The command reports deferred index maintenance rather than requiring index ownership.
