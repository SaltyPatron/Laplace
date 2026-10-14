# Installed Laplace monorepo client

`laplace-mono` runs the CLI from `SaltyPatron/Laplace`. It does not invoke
Laplace-Refactor or the modular Laplace-Engine executable. The command is usable
from any directory after installation; relative input paths resolve from the
caller's directory.

## Authenticated service profiles

Version 2 profiles select an HTTPS API and a private API-key file. The client uses
the service's existing authentication, workspace membership, operation catalog,
and admission APIs. It does not read OpenBao database records, set a SQL connection,
or initialize the native engine locally. The selected installed CLI must include
API-profile support.

```json
{
  "version": 2,
  "default_profile": "development",
  "profiles": {
    "development": {
      "runtime": {
        "kind": "iis-release",
        "directory": "E:/Data/Laplace/web"
      },
      "api": {
        "origin": "https://dev.hartonomous.com",
        "key_file": "C:/Users/your-account/.laplace/development.api-key"
      }
    }
  }
}
```

Use the runtime directory from the selected installation's declaration; the path
above illustrates the configuration shape. Linux profiles use runtime kind
`directory` and the directory containing the installed `Laplace.Cli`.
The key file contains an API key issued for the intended workspace. Protect that
file with the account's private file permissions; keep the key out of the JSON
declaration, command arguments, and source control. A version 2 profile rejects
`database` and `credential` fields. Both installers verify the selected service by
requesting its authenticated operation catalog.

```powershell
laplace-mono --where
laplace-mono ops
laplace-mono ops "explore.%"
laplace-mono op <catalog-operation-name> @arguments.json --max-rows 100
laplace-mono ingest document "E:\Documents\selected folder" --verify
laplace-mono content put "E:\Documents\source.cs" --code
laplace-mono content get <file-id>
laplace-mono content export <file-id> "E:\Documents\restored.md"
laplace-mono chat "Explain this relationship"
```

`op` passes typed JSON arguments to a named installed operation; the service
enforces its catalog and permissions. `--timeout-seconds` selects the service
operation timeout, with zero retaining its documented unbounded behavior.
Service errors return a nonzero exit status. The client neither retries writes
automatically nor follows redirects carrying credentials.

Document admission sends UTF-8 text files through the workspace content API,
retaining relative paths and observed modification times. Directory selection uses
the existing vendored/build-path exclusion rules and does not follow directory
links. `--verify` reconstructs each admitted file through the authenticated API
and compares its bytes before printing that file's receipt. A later failure does
not undo earlier completed files; their printed receipts identify those admissions.
`content export` creates a new destination and refuses to overwrite an existing
file. `content put --code` uses the server's registered grammar for the file.

Changing a profile to version 2 does not revoke an earlier database enrollment or
alter the server's database selection. Revoke obsolete database access through its
own installer/credential owner after the API installation is accepted. Never move
a service's database credential into the API-key file.

## Operator database profiles

The following version 1 configuration and commands describe direct database
operation. They remain for existing operator installations; they are not the
version 2 human API-client path.

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
