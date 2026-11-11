import { forwardRef, type HTMLAttributes } from 'react';
import { UnstyledButton, type UnstyledButtonProps } from '../UnstyledButton';

export type TabListProps = Omit<HTMLAttributes<HTMLDivElement>, 'role'>;

/** Container for Tab buttons (role="tablist"). Give it an aria-label. */
export const TabList = forwardRef<HTMLDivElement, TabListProps>(function TabList(props, ref) {
  return <div ref={ref} role="tablist" {...props} />;
});

export type TabProps = Omit<UnstyledButtonProps, 'role' | 'aria-selected'> & {
  /** Whether this tab's panel is the one shown; exposed as aria-selected. */
  selected: boolean;
};

/** One tab (role="tab"). Pair it with aria-controls / a role="tabpanel" region. */
export const Tab = forwardRef<HTMLButtonElement, TabProps>(function Tab({ selected, ...props }, ref) {
  return <UnstyledButton ref={ref} role="tab" aria-selected={selected} {...props} />;
});
