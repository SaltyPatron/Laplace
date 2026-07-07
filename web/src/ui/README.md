# Laplace UI (`@ui`)

Tiered component kit for the Laplace web SPA. Feature folders (`chat/`, `chess/`, `explore/`) import from `@ui` only — never the reverse.

## Tier model

| Tier | Contents | Import rule |
|------|----------|-------------|
| 0 Mantle | `theme.css`, `layers.css`, `TooltipProvider`, `cn` | Loaded once in `main.tsx` |
| 1 Primitives | Button, Input, Text, Badge, Chip, Link | No composite imports |
| 2 Interaction | Tooltip, Popover, Toggle, SegmentedControl, Checkbox, SliderField, Label | May import Tier 1 |
| 3 Composites | Field, LookupRow, Banner, Alert, Modal, Table | May import Tiers 1–2 |
| 4 Layout | Panel, Sidebar, Stack, AppHeader, TenantField, NavTabs | May import Tiers 1–3 |
| 5 Domain | ConsensusBadge, GatePrompt, … | Feature widgets on `@ui` (future) |
| 6 Views | ChatView, ChessView, … | Thin composers |

## Public API

Import from the barrel only:

```tsx
import { Button, Field, Tooltip, Panel } from '@ui';
```

Deep imports (`@ui/primitives/Button`) are reserved for Storybook and `@ui` internals.

## Styling

- Design tokens live in `theme.css` (primitive → semantic → component).
- Legacy global rules remain in `styles.css`, loaded inside `@layer legacy` via `layers.css`.
- New components use CSS modules in `@layer components` with token variables only.

## Tooltips

Wrap the app in `TooltipProvider` (see `main.tsx`). Use compound Tooltip API — not native `title=`. For disabled controls that need explanation, use `visuallyDisabled` on `Button`.

Run `npm run storybook` for the living catalog with a11y addon checks.
