import { create } from 'zustand';

export interface WalkPathNode {
  idHex: string;
  label: string;
}

interface ExploreState {
  quoteId: string;
  setQuoteId: (q: string) => void;
  walkPath: WalkPathNode[];
  setWalkPath: (path: WalkPathNode[]) => void;
  clearWalkPath: () => void;
  breadcrumb: { stage?: string; source?: string; entityLabel?: string; entityId?: string };
  setBreadcrumb: (b: ExploreState['breadcrumb']) => void;
}

export const useExploreStore = create<ExploreState>((set) => ({
  quoteId: '',
  setQuoteId: (quoteId) => set({ quoteId }),
  walkPath: [],
  setWalkPath: (walkPath) => set({ walkPath }),
  clearWalkPath: () => set({ walkPath: [] }),
  breadcrumb: {},
  setBreadcrumb: (breadcrumb) => set({ breadcrumb }),
}));
