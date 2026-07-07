import {
  autoUpdate,
  flip,
  FloatingPortal,
  offset,
  shift,
  useDismiss,
  useFloating,
  useFocus,
  useHover,
  useInteractions,
  useRole,
  type Placement,
} from '@floating-ui/react';
import {
  cloneElement,
  createContext,
  forwardRef,
  isValidElement,
  useContext,
  useId,
  useMemo,
  useState,
  type HTMLAttributes,
  type ReactElement,
  type ReactNode,
} from 'react';
import { mergeRefs } from '../../lib/mergeRefs';
import { useTooltipContext } from '../../providers/TooltipProvider';
import styles from './Tooltip.module.css';

interface TooltipContextValue {
  open: boolean;
  refs: ReturnType<typeof useFloating>['refs'];
  floatingStyles: ReturnType<typeof useFloating>['floatingStyles'];
  getReferenceProps: ReturnType<typeof useInteractions>['getReferenceProps'];
  getFloatingProps: ReturnType<typeof useInteractions>['getFloatingProps'];
  tooltipId: string;
}

const TooltipCtx = createContext<TooltipContextValue | null>(null);

function useTooltipInternal() {
  const ctx = useContext(TooltipCtx);
  if (!ctx) throw new Error('TooltipTrigger/TooltipContent must be inside Tooltip');
  return ctx;
}

export interface TooltipProps {
  children: ReactNode;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  placement?: Placement;
}

export function Tooltip({
  children,
  open: controlledOpen,
  onOpenChange,
  placement = 'top',
}: TooltipProps) {
  const { delay } = useTooltipContext();
  const [uncontrolledOpen, setUncontrolledOpen] = useState(false);
  const open = controlledOpen ?? uncontrolledOpen;
  const setOpen = (next: boolean) => {
    if (controlledOpen === undefined) setUncontrolledOpen(next);
    onOpenChange?.(next);
  };

  const tooltipId = useId();

  const { refs, floatingStyles, context } = useFloating({
    open,
    onOpenChange: setOpen,
    placement,
    whileElementsMounted: autoUpdate,
    middleware: [offset(6), flip(), shift({ padding: 8 })],
  });

  const hover = useHover(context, { move: false, delay: { open: delay, close: 0 } });
  const focus = useFocus(context);
  const dismiss = useDismiss(context);
  const role = useRole(context, { role: 'tooltip' });
  const { getReferenceProps, getFloatingProps } = useInteractions([hover, focus, dismiss, role]);

  const value = useMemo(
    () => ({ open, refs, floatingStyles, getReferenceProps, getFloatingProps, tooltipId }),
    [open, refs, floatingStyles, getReferenceProps, getFloatingProps, tooltipId],
  );

  return <TooltipCtx.Provider value={value}>{children}</TooltipCtx.Provider>;
}

export interface TooltipTriggerProps {
  asChild?: boolean;
  children: ReactElement;
}

export const TooltipTrigger = forwardRef<HTMLElement, TooltipTriggerProps>(function TooltipTrigger(
  { asChild = false, children },
  ref,
) {
  const { refs, getReferenceProps } = useTooltipInternal();

  if (asChild && isValidElement(children)) {
    const child = children as ReactElement<{ ref?: React.Ref<HTMLElement> }>;
    return cloneElement(child, {
      ...getReferenceProps(child.props),
      ref: mergeRefs(ref, refs.setReference, child.props.ref),
    });
  }

  return (
    <span ref={mergeRefs(ref, refs.setReference)} {...getReferenceProps()}>
      {children}
    </span>
  );
});

export interface TooltipContentProps extends HTMLAttributes<HTMLDivElement> {
  side?: Placement;
}

export const TooltipContent = forwardRef<HTMLDivElement, TooltipContentProps>(function TooltipContent(
  { className, children, ...props },
  ref,
) {
  const { open, refs, floatingStyles, getFloatingProps, tooltipId } = useTooltipInternal();

  if (!open) return null;

  return (
    <FloatingPortal id="ui-portal">
      <div
        ref={mergeRefs(ref, refs.setFloating)}
        id={tooltipId}
        style={floatingStyles}
        className={`${styles.content}${className ? ` ${className}` : ''}`}
        {...getFloatingProps(props)}
      >
        {children}
      </div>
    </FloatingPortal>
  );
});

export const tooltipSurfaceClass = styles.content;
