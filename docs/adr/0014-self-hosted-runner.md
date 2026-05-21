# ADR 0014: Self-hosted GitHub Actions runner on hart-server

## Status

**Superseded by ADR 0019 (dedicated `laplace-runner` system account)** — 2026-05-21

## Context

CI for Laplace needs access to local resources (Intel oneAPI, PG18 + PostGIS, /vault/Data, /vault/models, .NET 10, etc.). GitHub-hosted runners can't access those. Self-hosted is required.

## Decision

Install GitHub Actions runner on hart-server under OS user `ahart`, registered with `SaltyPatron/Laplace`, labels: `self-hosted, linux, x64, laplace, oneapi, postgres-18, dotnet-10, avx2`. Service runs as `ahart` via systemd.

## Consequences

- CI has full access to oneAPI / PG / /vault data.
- Runner identity is conflated with Anthony's interactive user (`ahart`).
- Sudo for `make install` requires Anthony's password — blocking automated extension installs in CI.

## Why superseded

`ahart` is Anthony's interactive identity. Using it for the CI runner conflates roles and ties CI operations to Anthony's personal sudo. The runner should be a dedicated system account with bounded permissions. See [ADR 0019](0019-laplace-runner-system-account.md).

## References

- [RULES.md R8](../../RULES.md)
