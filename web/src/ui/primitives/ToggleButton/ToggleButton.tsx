import { forwardRef } from 'react';
import { UnstyledButton, type UnstyledButtonProps } from '../UnstyledButton';

export type ToggleButtonProps = Omit<UnstyledButtonProps, 'aria-pressed'> & {
  /** On/off state, exposed to assistive technology as aria-pressed. */
  pressed: boolean;
};

/** A two-state button (filter chip, letter index, follow toggle). The caller's className owns the look. */
export const ToggleButton = forwardRef<HTMLButtonElement, ToggleButtonProps>(function ToggleButton(
  { pressed, ...props },
  ref,
) {
  return <UnstyledButton ref={ref} aria-pressed={pressed} {...props} />;
});
