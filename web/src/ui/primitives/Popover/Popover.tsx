import {
  autoUpdate,
  flip,
  FloatingPortal,
  offset,
  shift,
  useClick,
  useDismiss,
  useFloating,
  useInteractions,
  useRole,
  type Placement,
} from '@floating-ui/react';import {
  createContext,
  forwardRef,
  useContext,
  useId,
  useMemo,
  useState,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type ReactNode,
} from 'react';
import { mergeRefs } from '../../lib/mergeRefs';
import styles from './Popover.module.css';

interface PopoverContextValue {
  open: boolean;
  setOpen: (open: boolean) => void;
  refs: ReturnType<typeof useFloating>['refs'];
  floatingStyles: ReturnType<typeof useFloating>['floatingStyles'];
  getReferenceProps: ReturnType<typeof useInteractions>['getReferenceProps'];
  getFloatingProps: ReturnType<typeof useInteractions>['getFloatingProps'];
  labelId: string;
}

const PopoverCtx = createContext<PopoverContextValue | null>(null);

function usePopoverInternal() {
  const ctx = useContext(PopoverCtx);
  if (!ctx) throw new Error('Popover parts must be inside Popover');
  return ctx;
}

export interface PopoverProps {
  children: ReactNode;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  placement?: Placement;
}

/** Interactive panel stub — click to open, Escape to dismiss. Not a tooltip. */
export function Popover({
  children,
  open: controlledOpen,
  onOpenChange,
  placement = 'bottom',
}: PopoverProps) {
  const [uncontrolledOpen, setUncontrolledOpen] = useState(false);
  const open = controlledOpen ?? uncontrolledOpen;
  const setOpen = (next: boolean) => {
    if (controlledOpen === undefined) setUncontrolledOpen(next);
    onOpenChange?.(next);
  };
  const labelId = useId();

  const { refs, floatingStyles, context } = useFloating({
    open,
    onOpenChange: setOpen,
    placement,
    whileElementsMounted: autoUpdate,
    middleware: [offset(8), flip(), shift({ padding: 8 })],
  });

  const click = useClick(context);
  const dismiss = useDismiss(context);
  const role = useRole(context, { role: 'dialog' });
  const { getReferenceProps, getFloatingProps } = useInteractions([click, dismiss, role]);

  const value = useMemo(
    () => ({ open, setOpen, refs, floatingStyles, getReferenceProps, getFloatingProps, labelId }),
    [open, setOpen, refs, floatingStyles, getReferenceProps, getFloatingProps, labelId],
  );

  return <PopoverCtx.Provider value={value}>{children}</PopoverCtx.Provider>;
}

export const PopoverTrigger = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement>>(
  function PopoverTrigger({ children, ...props }, ref) {
    const { refs, getReferenceProps } = usePopoverInternal();
    return (
      <button ref={mergeRefs(ref, refs.setReference)} type="button" {...getReferenceProps(props)}>
        {children}
      </button>
    );
  },
);

export const PopoverContent = forwardRef<HTMLDivElement, HTMLAttributes<HTMLDivElement>>(
  function PopoverContent({ className, children, ...props }, ref) {
    const { open, refs, floatingStyles, getFloatingProps, labelId } = usePopoverInternal();
    if (!open) return null;

    return (
      <FloatingPortal id="ui-portal">
        <div
          ref={mergeRefs(ref, refs.setFloating)}
          id={labelId}
          style={floatingStyles}
          className={`${styles.content}${className ? ` ${className}` : ''}`}
          {...getFloatingProps(props)}
        >
          {children}
        </div>
      </FloatingPortal>
    );
  },
);
