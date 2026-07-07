// Tier 0
export { TooltipProvider, type TooltipProviderProps } from './providers/TooltipProvider';
export { cn } from './lib/cn';
export { useControllableState } from './hooks/useControllableState';

// Tier 1 — Primitives
export { Button, IconButton, buttonVariants, type ButtonProps } from './primitives/Button';
export { Input, type InputProps } from './primitives/Input';
export { TextArea, type TextAreaProps } from './primitives/TextArea';
export { Select, type SelectProps } from './primitives/Select';
export { Text, Muted, ErrorText, LoadingText } from './primitives/Text';
export { Badge, badgeVariants, type BadgeProps } from './primitives/Badge';
export { Chip, chipVariants, type ChipProps } from './primitives/Chip';
export { Link, type LinkProps } from './primitives/Link';

// Tier 2 — Interaction
export {
  Tooltip,
  TooltipTrigger,
  TooltipContent,
  tooltipSurfaceClass,
  type TooltipProps,
  type TooltipTriggerProps,
  type TooltipContentProps,
} from './primitives/Tooltip';
export { Popover, PopoverTrigger, PopoverContent, type PopoverProps } from './primitives/Popover';
export { Toggle, type ToggleProps } from './primitives/Toggle';
export { SegmentedControl, type SegmentedControlProps } from './primitives/SegmentedControl';
export { Checkbox, type CheckboxProps } from './primitives/Checkbox';
export { SliderField, type SliderFieldProps } from './primitives/SliderField';
export { Label, type LabelProps } from './primitives/Label';

// Tier 3 — Composites
export { Field, type FieldProps } from './composites/Field';
export { FormRow, type FormRowProps } from './composites/FormRow';
export { LookupRow, type LookupRowProps } from './composites/LookupRow';
export { Banner, type BannerProps } from './composites/Banner';
export { Alert, type AlertProps } from './composites/Alert';
export { ConsensusBadge, type ConsensusBadgeProps } from './composites/ConsensusBadge';
export { Modal, type ModalProps } from './composites/Modal';
export { Table, TableScroll, Th, Td } from './composites/Table';

// Tier 4 — Layout
export { Panel, type PanelProps } from './layout/Panel';
export { Sidebar } from './layout/Sidebar';
export { Stack, type StackProps } from './layout/Stack';
export { AppHeader, type AppHeaderProps } from './layout/AppHeader';
export { TenantField, type TenantFieldProps } from './layout/TenantField';
export { NavTabs, type NavTab, type NavTabsProps } from './layout/NavTabs';
