# Contributing to `@ui`

## Adding a component

1. **Pick the tier** — primitives are single-purpose controls; composites combine them; layout is app chrome.
2. **Check for duplication** — extend an existing primitive before adding a near-duplicate.
3. **CSS modules + tokens** — no hardcoded hex in modules; use `var(--color-*)` from `theme.css`.
4. **Export from `index.ts`** — the barrel is the public contract.
5. **Storybook story** — default, disabled/error, keyboard focus; a11y addon should report zero violations.

## Tier 5 (domain) controls

Require a one-paragraph RFC in the PR: which primitives it wraps, which feature folders consume it, and why it is not a feature-local component.

## Breaking changes

Prop renames need a deprecation comment and at least one release before removal.

## Tests

- `npm run typecheck` — strict props, `VariantProps` on variant components.
- `npm run build` — CSS modules and `@layer` resolve in Vite.
- Storybook a11y addon on primitive stories.
- Pilot screens (Chess Lab, App header) for manual tooltip/toggle smoke.
