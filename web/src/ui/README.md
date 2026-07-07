# Laplace UI (`@ui`)

Tiered component kit for the Laplace web SPA. Feature folders (`chat/`, `chess/`, `explore/`) import from `@ui` only — never the reverse.

## Tier model

| Tier | Contents | Import rule |
|------|----------|-------------|
| 0 Mantle | `theme.css`, `layers.css`, `TooltipProvider`, `cn` | Loaded once in `main.tsx` |
| 1 Primitives | Button, Input, Text, Badge, Chip, Link | No composite imports |
| 2 Interaction | Tooltip, Popover, Toggle, SegmentedControl, Checkbox, SliderField, Label | May import Tier 1 |
| 3 Composites | Field, FormRow, LookupRow, Banner, Alert, Modal, Table | May import Tiers 1–2 |
| 4 Layout | Panel, Sidebar, Stack, AppHeader, TenantField, NavTabs | May import Tiers 1–3 |
| 5 Domain | ConsensusBadge, GatePrompt, … | Feature widgets on `@ui` (future) |
| 6 Views | ChatView, ChessView, … | Thin composers |

## Public API

Import from the barrel only:

```tsx
import { Button, Field, FormRow, Tooltip, Panel } from '@ui';
```

Deep imports (`@ui/primitives/Button`) are reserved for Storybook and `@ui` internals.

## Styling — greenfield only

- Design tokens live in `theme.css` (primitive → semantic → component).
- `layers.css` declares the cascade order only (`reset`, `tokens`, `base`, `components`, `utilities`). **There is no legacy layer.**
- Every surface uses CSS modules: `@ui/**/*.module.css` for kit components, feature-local `*.module.css` co-located with views.
- `styles.css` is gone. Do not add global feature CSS to `theme.css` — only tokens, base reset, and utilities belong there.

### Feature CSS modules

| Area | Module |
|------|--------|
| App shell | `App.module.css` |
| Chat | `ChatView.module.css`, `ReceiptPanel.module.css` |
| Billing | `BillingView.module.css` |
| Chess play | `ChessView.module.css`, `chess/play/*.module.css` |
| Explore | `ExploreView.module.css`, `EvidenceTable.module.css`, glome/graph modules |

## Tooltips & popovers

Wrap the app in `TooltipProvider` (see `main.tsx`). Use compound Tooltip / Popover APIs — not native `title=`. For disabled controls that need explanation, use `visuallyDisabled` on `Button`.

Run `npm run storybook` for the living catalog with a11y addon checks.
