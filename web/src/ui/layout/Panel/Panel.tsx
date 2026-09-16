import { createContext, useContext, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { FloatingFocusManager, useFloating } from '@floating-ui/react';
import { cn } from '../../lib/cn';
import { Button } from '../../primitives/Button';
import styles from './Panel.module.css';

const ExpandedPanelContext = createContext(false);

export interface PanelProps {
  title?: ReactNode;
  actions?: ReactNode;
  className?: string;
  /** Stretch to fill a flex parent; last content child grows into remaining space. */
  fill?: boolean;
  /** Expand in place without recreating the content, selection, or its requests. */
  expandable?: boolean;
  /** Accessible name when title is not plain text. */
  label?: string;
  children: ReactNode;
}

export function Panel({ title, actions, className, fill = false, expandable = fill, label, children }: PanelProps) {
  const id = useId();
  const [expanded, setExpanded] = useState(false);
  const insideExpanded = useContext(ExpandedPanelContext);
  const expandButton = useRef<HTMLButtonElement>(null);
  const { refs, context } = useFloating({ open: expanded, onOpenChange: setExpanded });
  const name = label ?? (typeof title === 'string' ? title : 'panel');
  const canExpand = expandable && !insideExpanded;
  const hasHead = title != null || actions != null || canExpand;

  useLayoutEffect(() => {
    if (!expanded) return;
    const panel = refs.floating.current;
    if (!panel) return;
    // The SAME section enters the browser top layer. No portal switch, remount,
    // route transition, query rerun, or loss of canvas/editor state occurs.
    if (typeof panel.showPopover === 'function') {
      panel.setAttribute('popover', 'manual');
      try { panel.showPopover(); } catch { panel.removeAttribute('popover'); }
    }
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      if (panel.hasAttribute('popover')) {
        try { panel.hidePopover(); } catch { /* May already be detached. */ }
        panel.removeAttribute('popover');
      }
      document.body.style.overflow = previousOverflow;
    };
  }, [expanded, refs.floating]);

  return (
    <FloatingFocusManager
      context={context}
      disabled={!expanded}
      initialFocus={refs.floating}
      returnFocus={expandButton}
      outsideElementsInert
    >
      <section
        ref={refs.setFloating}
        id={id}
        role={expanded ? 'dialog' : undefined}
        aria-modal={expanded || undefined}
        aria-labelledby={title != null ? `${id}-title` : undefined}
        aria-label={title == null ? name : undefined}
        tabIndex={expanded ? -1 : undefined}
        data-expanded={expanded || undefined}
        className={cn(styles.panel, fill && styles.fill, expanded && styles.expanded, className)}
        style={expanded ? {
          position: 'fixed', inset: 'clamp(0px, 1vw, 1rem)', margin: 0,
          width: 'auto', height: 'auto', maxWidth: 'none', maxHeight: 'none',
          minWidth: 0, minHeight: 0, overflow: 'auto', zIndex: 1000,
        } : undefined}
        onKeyDown={(event) => {
          if (!expanded || event.key !== 'Escape' || event.defaultPrevented) return;
          // A nested native dialog owns its Escape; other panels have no global handler.
          if (event.target instanceof Element && event.target.closest('dialog[open]')) return;
          event.preventDefault();
          event.stopPropagation();
          setExpanded(false);
        }}
      >
        {hasHead && (
          <header className={styles.head}>
            {title != null && <h3 id={`${id}-title`}>{title}</h3>}
            {(actions != null || canExpand) && (
              <div className={styles.actions}>
                {actions}
                {canExpand && (
                  <Button
                    ref={expandButton}
                    variant="ghost"
                    size="sm"
                    aria-expanded={expanded}
                    aria-controls={id}
                    aria-label={`${expanded ? 'Restore' : 'Expand'} ${name}`}
                    onClick={() => setExpanded((value) => !value)}
                  >
                    {expanded ? 'Restore' : 'Expand'}
                  </Button>
                )}
              </div>
            )}
          </header>
        )}
        <ExpandedPanelContext.Provider value={expanded || insideExpanded}>
          {children}
        </ExpandedPanelContext.Provider>
      </section>
    </FloatingFocusManager>
  );
}
