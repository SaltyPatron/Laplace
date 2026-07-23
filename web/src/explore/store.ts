import { create } from 'zustand';

export interface WalkPathNode {
  idHex: string;
  label: string;
}

/** One stop on the mesh drill-down, for the breadcrumb trail. */
export interface MeshCrumb {
  id: string;
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
  // The mesh drill path: pushing a node already in the trail truncates back to
  // it (you drilled up), so the breadcrumb never grows a cycle.
  meshTrail: MeshCrumb[];
  pushMeshCrumb: (c: MeshCrumb) => void;
  resetMeshTrail: () => void;
}

export const useExploreStore = create<ExploreState>((set) => ({
  quoteId: '',
  setQuoteId: (quoteId) => set({ quoteId }),
  walkPath: [],
  setWalkPath: (walkPath) => set({ walkPath }),
  clearWalkPath: () => set({ walkPath: [] }),
  breadcrumb: {},
  setBreadcrumb: (breadcrumb) => set({ breadcrumb }),
  meshTrail: [],
  pushMeshCrumb: (c) =>
    set((s) => {
      const at = s.meshTrail.findIndex((x) => x.id === c.id);
      if (at >= 0) return { meshTrail: s.meshTrail.slice(0, at + 1) };
      return { meshTrail: [...s.meshTrail, c] };
    }),
  resetMeshTrail: () => set({ meshTrail: [] }),
}));
