import { type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import { Button } from '../../primitives/Button';
import styles from './NavTabs.module.css';

export interface NavTab {
  id: string;
  label: ReactNode;
  active?: boolean;
  onClick: () => void;
}

export interface NavTabsProps {
  tabs: NavTab[];
  className?: string;
}

export function NavTabs({ tabs, className }: NavTabsProps) {
  return (
    <div className={cn(styles.tabs, className)} role="navigation">
      {tabs.map((tab) => (
        <Button
          key={tab.id}
          type="button"
          variant="nav"
          active={tab.active}
          onClick={tab.onClick}
        >
          {tab.label}
        </Button>
      ))}
    </div>
  );
}
