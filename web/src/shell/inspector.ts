/**
 * The inspector drawer: any entity, opened from anywhere, without losing your place.
 * State lives in the URL (?peek=<id>) so it is shareable and works with back/forward.
 */
import { useCallback } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { create } from 'zustand';

export const PEEK_PARAM = 'peek';
const ID = /^[0-9a-f]{32}$/i;

/** Labels learned from wherever an entity was shown, so the drawer can title itself before its read returns. */
export const useEntityLabels = create<{ labels: Record<string, string>; remember: (id: string, label: string) => void }>((set) => ({
  labels: {},
  remember: (id, label) => set((state) => (state.labels[id] === label ? state : { labels: { ...state.labels, [id]: label } })),
}));

export function useInspector() {
  const location = useLocation();
  const navigate = useNavigate();
  const remember = useEntityLabels((state) => state.remember);
  const params = new URLSearchParams(location.search);
  const raw = params.get(PEEK_PARAM);
  const current = raw && ID.test(raw) ? raw.toLowerCase() : null;

  const open = useCallback((id: string, label?: string) => {
    if (!ID.test(id)) return;
    if (label) remember(id.toLowerCase(), label);
    const next = new URLSearchParams(location.search);
    next.set(PEEK_PARAM, id.toLowerCase());
    navigate({ pathname: location.pathname, search: `?${next}` }, { preventScrollReset: true });
  }, [location.pathname, location.search, navigate, remember]);

  const close = useCallback(() => {
    const next = new URLSearchParams(location.search);
    next.delete(PEEK_PARAM);
    const search = next.toString();
    navigate({ pathname: location.pathname, search: search ? `?${search}` : '' }, { preventScrollReset: true });
  }, [location.pathname, location.search, navigate]);

  /** Promote the drawer to a full page. */
  const pin = useCallback(() => {
    if (current) navigate(`/explore/entity/${current}`);
  }, [current, navigate]);

  return { current, open, close, pin };
}
