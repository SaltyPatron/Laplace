import type { ReactNode } from 'react';

/**
 * Ordered, modular sidebar column. Render order == child order, so you rearrange
 * or extend the sidebar by editing the child list in ChessView — e.g. add
 *   <Panel title="My control"><MyThing /></Panel>
 * anywhere in the children and it slots in with the shared chrome. No layout code
 * to touch; this is just the column that holds the Panel-based UserControls.
 */
export function Sidebar({ children }: { children: ReactNode }) {
  return <aside className="chess-side">{children}</aside>;
}
