import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import {
  cloneElement, forwardRef, isValidElement, type AriaRole, type ButtonHTMLAttributes,
  type KeyboardEventHandler, type MouseEventHandler,
} from 'react';
import { cn } from '../../lib/cn';
import styles from './Button.module.css';

const buttonVariants = cva(styles.button, {
  variants: {
    variant: { primary: '', ghost: styles.ghost, nav: styles.nav },
    size: { md: '', sm: styles.sm, icon: styles.icon },
    active: { true: styles.navActive, false: '' },
  },
  defaultVariants: { variant: 'primary', size: 'md', active: false },
});

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean;
    loading?: boolean;
    /** Keep focus/tooltips while preventing activation, including on slotted links. */
    visuallyDisabled?: boolean;
  };

type ChildEvents = {
  href?: string;
  role?: AriaRole;
  tabIndex?: number;
  onClickCapture?: MouseEventHandler<HTMLElement>;
  onAuxClickCapture?: MouseEventHandler<HTMLElement>;
  onKeyDownCapture?: KeyboardEventHandler<HTMLElement>;
};

function stop(event: { preventDefault(): void; stopPropagation(): void }) {
  event.preventDefault();
  event.stopPropagation();
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { className, variant, size, active, asChild = false, loading = false,
    visuallyDisabled = false, disabled, type = 'button', children,
    onClickCapture, onAuxClickCapture, onKeyDownCapture, 'aria-current': ariaCurrent,
    'aria-disabled': ariaDisabled, ...props }, ref,
) {
  const Comp = asChild ? Slot : 'button';
  const isDisabled = !!(disabled || loading || visuallyDisabled);
  // Radix composes child handlers first. Guard the child's capture handlers too,
  // so a disabled slotted action cannot run before the Slot's own guard.
  const content = asChild && isDisabled && isValidElement<ChildEvents>(children)
    ? cloneElement(children, {
      // Without removing a native link destination, middle-click/context-menu
      // navigation can bypass click handlers. Enabled links keep their real href.
      ...(children.type === 'a' ? {
        href: undefined, role: children.props.role ?? 'link',
        tabIndex: visuallyDisabled ? (children.props.tabIndex ?? 0) : -1,
      } : {}),
      onClickCapture: stop,
      onAuxClickCapture: stop,
      onKeyDownCapture: (event) => {
        if (event.key === 'Enter' || event.key === ' ') stop(event);
        else children.props.onKeyDownCapture?.(event);
      },
    }) : children;

  return <Comp
    {...props}
    ref={ref}
    type={asChild ? undefined : type}
    className={cn(buttonVariants({ variant, size, active: variant === 'nav' && active }), loading && styles.loading, className)}
    disabled={asChild || visuallyDisabled ? undefined : isDisabled}
    aria-disabled={isDisabled || ariaDisabled || undefined}
    aria-busy={loading || undefined}
    aria-current={variant === 'nav' && active ? 'page' : ariaCurrent}
    onClickCapture={(event) => { if (isDisabled) stop(event); else onClickCapture?.(event); }}
    onAuxClickCapture={(event) => { if (isDisabled) stop(event); else onAuxClickCapture?.(event); }}
    onKeyDownCapture={(event) => {
      if (isDisabled && (event.key === 'Enter' || event.key === ' ')) stop(event);
      else onKeyDownCapture?.(event);
    }}
  >{content}</Comp>;
});

export const IconButton = forwardRef<HTMLButtonElement, ButtonProps>(function IconButton(props, ref) {
  return <Button ref={ref} size="icon" variant={props.variant ?? 'ghost'} {...props} />;
});

export { buttonVariants };
