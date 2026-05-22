# ADR 0020: Conventional Commits + release-please + SemVer

## Status

**Accepted** — 2026-05-21

## Context

Project lacked: structured commit conventions, automated versioning, automated changelog. Pre-existing commits used informal prefixes (`chunk 0:`, `infra:`, `status:`) — not parseable by tooling. No release tags. No changelog file (until now).

## Decision

Adopt **Conventional Commits 1.0.0** for all commit messages from this point forward.

Format: `<type>(<scope>): <subject>` where:
- `type`: `feat | fix | docs | style | refactor | perf | test | chore | build | ci | revert`
- `scope` (optional): area (e.g., `engine`, `extension`, `app`, `ci`, `docs`, `sdlc`)
- `!` suffix on type or footer `BREAKING CHANGE:` for breaking changes
- subject in imperative mood, lowercase first word, no trailing period

Adopt **`release-please`** GitHub Action to auto-generate release PRs:
- Triggers on push to main
- Aggregates commits since last release
- Bumps version per SemVer (feat→minor, fix→patch, breaking→major)
- Updates `CHANGELOG.md` per Keep-a-Changelog format
- Opens a PR with the version bump + changelog update
- Merging the PR triggers a GitHub Release with auto-generated notes

Adopt **`commitlint`** via pre-commit hook to enforce the format locally.

## Consequences

- Changelog auto-maintained from commit history; no manual editing.
- Versions bump deterministically; SemVer is enforced.
- GitHub Releases get auto-generated notes.
- Commits become more searchable / categorizable (e.g., "all `feat:` commits since v0.1.0").
- Cost: every commit message must follow the format. Pre-commit catches early.

## References

- [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/)
- [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
- [release-please](https://github.com/googleapis/release-please)
- [CHANGELOG.md](../../CHANGELOG.md)
