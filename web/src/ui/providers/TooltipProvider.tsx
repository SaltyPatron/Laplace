import {
  createContext,
  useContext,
  useMemo,
  useState,
  type ReactNode,
} from 'react';

export interface TooltipProviderProps {
  children: ReactNode;
  /** Delay before opening (ms). Default 400. */
  delay?: number;
}

interface TooltipContextValue {
  delay: number;
  openId: string | null;
  setOpenId: (id: string | null) => void;
}

const TooltipContext = createContext<TooltipContextValue | null>(null);

export function TooltipProvider({ children, delay = 400 }: TooltipProviderProps) {
  const [openId, setOpenId] = useState<string | null>(null);
  const value = useMemo(() => ({ delay, openId, setOpenId }), [delay, openId]);

  return (
    <TooltipContext.Provider value={value}>
      {children}
      <div id="ui-portal" />
    </TooltipContext.Provider>
  );
}

export function useTooltipContext() {
  const ctx = useContext(TooltipContext);
  if (!ctx) throw new Error('Tooltip components must be used within TooltipProvider');
  return ctx;
}