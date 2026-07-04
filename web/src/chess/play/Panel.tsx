import type { ReactNode } from 'react';

export interface PanelProps {
  /** Optional title bar text/node. Omit for a chrome-only container. */
  title?: ReactNode;
  /** Optional right-aligned controls in the title bar (buttons, toggles, tabs). */
  actions?: ReactNode;
  /** Extra class(es) for per-panel styling. */
  className?: string;
  children: ReactNode;
}

/**
 * Reusable sidebar "UserControl" shell: consistent border/padding + an optional
 * title bar with right-aligned actions. Every sidebar module renders through this
 * so they share one chrome and the sidebar is composed, not hand-assembled.
 *
 * Drop a custom control into the sidebar by rendering a <Panel title="…">…</Panel>
 * (or any Panel-based component) as a child of <Sidebar>.
 */
export function Panel({ title, actions, className, children }: PanelProps) {
  const hasHead = title != null || actions != null;
  return (
    <section className={`panel${className ? ` ${className}` : ''}`}>
      {hasHead && (
        <header className="panel-head">
          {title != null && <h3>{title}</h3>}
          {actions != null && <div className="panel-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}
