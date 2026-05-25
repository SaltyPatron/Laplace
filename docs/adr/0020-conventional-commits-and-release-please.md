# ADR 0020: Conventional Commits + SemVer (release-please removed)

## Status

**Accepted** — 2026-05-21 (amended 2026-05-25: release-please removed)

> **Amendment (2026-05-25) — release-please removed.** The
> `release-please` GitHub Action is deleted (`.github/workflows/release-please.yml`,
> `release-please-config.json`, `.release-please-manifest.json`, and the
> auto-managed `CHANGELOG.md`). It failed on every push to `main`
> (`GitHub Actions is not permitted to create or approve pull requests` —
> the action needs Actions-PR-create permission that the repo disables) and
> bought nothing on a solo, pre-release project: there is no release-PR
> review audience and no published version train yet. **Conventional
> Commits + `commitlint` are retained** as the commit-message standard
> (commits remain searchable/categorizable and ready for automated
> versioning if it's reintroduced later). When the project reaches a
> release cadence, versioning + changelog can be revisited as a fresh
> decision — manual tags or a re-enabled action with the right permissions.

## Context

Project lacked: structured commit conventions, automated versioning, automated changelog. Pre-existing commits used informal prefixes (`chunk 0:`, `infra:`, `status:`) — not parseable by tooling. No release tags. No changelog file (until now).

## Decision

Adopt **Conventional Commits 1.0.0** for all commit messages from this point forward.

Format: `<type>(<scope>): <subject>` where:
- `type`: `feat | fix | docs | style | refactor | perf | test | chore | build | ci | revert`
- `scope` (optional): area (e.g., `engine`, `extension`, `app`, `ci`, `docs`, `sdlc`)
- `!` suffix on type or footer `BREAKING CHANGE:` for breaking changes
- subject in imperative mood, lowercase first word, no trailing period

~~Adopt **`release-please`** GitHub Action to auto-generate release PRs.~~
**Removed 2026-05-25 — see the amendment at the top of this ADR.** SemVer
remains the versioning scheme (feat→minor, fix→patch, breaking→major); how
versions get cut is deferred to a future decision once the project has a
release cadence.

Adopt **`commitlint`** via pre-commit hook to enforce the format locally.

## Consequences

- Versions follow SemVer; the `<type>` of each commit carries the bump
  intent (feat→minor, fix→patch, `!`/`BREAKING CHANGE`→major) ready for
  automated versioning whenever it's reintroduced.
- Commits become more searchable / categorizable (e.g., "all `feat:` commits since v0.1.0").
- Cost: every commit message must follow the format. Pre-commit catches early.

## References

- [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/)
- [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html)
