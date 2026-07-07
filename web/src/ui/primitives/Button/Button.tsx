import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Button.module.css';

const buttonVariants = cva(styles.button, {
  variants: {
    variant: {
      primary: '',
      ghost: styles.ghost,
      nav: styles.nav,
    },
    size: {
      md: '',
      sm: styles.sm,
      icon: styles.icon,
    },
    active: {
      true: styles.navActive,
      false: '',
    },
  },
  defaultVariants: {
    variant: 'primary',
    size: 'md',
    active: false,
  },
});

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean;
    loading?: boolean;
    /** Use aria-disabled instead of native disabled (allows tooltip on disabled). */
    visuallyDisabled?: boolean;
  };

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  {
    className,
    variant,
    size,
    active,
    asChild = false,
    loading = false,
    visuallyDisabled = false,
    disabled,
    type = 'button',
    'aria-current': ariaCurrent,
    ...props
  },
  ref,
) {
  const Comp = asChild ? Slot : 'button';
  const isDisabled = disabled || loading || visuallyDisabled;

  return (
    <Comp
      ref={ref}
      type={asChild ? undefined : type}
      className={cn(buttonVariants({ variant, size, active: variant === 'nav' && active }), loading && styles.loading, className)}
      disabled={asChild ? undefined : visuallyDisabled ? undefined : isDisabled}
      aria-disabled={visuallyDisabled || loading ? true : undefined}
      aria-busy={loading || undefined}
      aria-current={variant === 'nav' && active ? 'page' : ariaCurrent}
      {...props}
    />
  );
});

/** Square icon-only button (PST piece tabs, help triggers). */
export const IconButton = forwardRef<HTMLButtonElement, ButtonProps>(function IconButton(
  props,
  ref,
) {
  return <Button ref={ref} size="icon" variant={props.variant ?? 'ghost'} {...props} />;
});

export { buttonVariants };
