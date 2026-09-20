import { type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import { Button } from '../../primitives/Button';
import styles from './NavTabs.module.css';

export interface NavTab {
  id: string;
  label: ReactNode;
  active?: boolean;
  /** Real destinations remain copyable and support new-tab/middle-click navigation. */
  href?: string;
  onClick?: () => void;
  /** Warm a lazy destination on hover/focus without navigating. */
  onIntent?: () => void;
}

export interface NavTabsProps {
  tabs: NavTab[];
  className?: string;
  label?: string;
}

export function NavTabs({ tabs, className, label = 'Primary navigation' }: NavTabsProps) {
  return (
    <nav className={cn(styles.tabs, className)} aria-label={label}>
      {tabs.map((tab) => tab.href ? (
        <Button key={tab.id} asChild variant="nav" active={tab.active}>
          <a href={tab.href} onPointerEnter={tab.onIntent} onFocus={tab.onIntent} onClick={(event) => {
            if (!tab.onClick || event.defaultPrevented || event.button !== 0 ||
                event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            tab.onClick();
          }}>{tab.label}</a>
        </Button>
      ) : (
        <Button key={tab.id} type="button" variant="nav" active={tab.active}
          onPointerEnter={tab.onIntent} onFocus={tab.onIntent} onClick={tab.onClick}>
          {tab.label}
        </Button>
      ))}
    </nav>
  );
}
