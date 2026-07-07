import { forwardRef, type HTMLAttributes, type TableHTMLAttributes, type TdHTMLAttributes, type ThHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Table.module.css';

export const Table = forwardRef<HTMLTableElement, TableHTMLAttributes<HTMLTableElement>>(
  function Table({ className, ...props }, ref) {
    return <table ref={ref} className={cn(styles.table, className)} {...props} />;
  },
);

export const TableScroll = forwardRef<HTMLDivElement, HTMLAttributes<HTMLDivElement>>(
  function TableScroll({ className, children, ...props }, ref) {
    return (
      <div ref={ref} className={cn(styles.scroll, className)} {...props}>
        {children}
      </div>
    );
  },
);

export const Th = forwardRef<HTMLTableCellElement, ThHTMLAttributes<HTMLTableCellElement>>(
  function Th({ className, ...props }, ref) {
    return <th ref={ref} className={cn(styles.th, className)} {...props} />;
  },
);

export const Td = forwardRef<HTMLTableCellElement, TdHTMLAttributes<HTMLTableCellElement>>(
  function Td({ className, ...props }, ref) {
    return <td ref={ref} className={cn(styles.td, className)} {...props} />;
  },
);
