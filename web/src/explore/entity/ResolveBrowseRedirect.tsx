import { useEffect } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { changeWorld } from '../../world/context';
import { useRouteText } from '../../layout/useRouteText';
import { LoadingText } from '@ui';

const ENTITY_HEX = /^[0-9a-f]{32}$/i;

/**
 * Routes /explore/resolve/:ref. A 32-hex canonical id opens its entity page; any other
 * input goes to Browse, since a calculable content id does not make the string an admitted
 * entity — Browse resolves it through decomposition, admitted members and stored
 * containment and name candidates.
 */
export function ResolveBrowseRedirect() {
  const ref = useRouteText('/explore/resolve/:ref', 'ref');
  const nav = useNavigate();
  const { search } = useLocation();

  useEffect(() => {
    const surface = ref.trim();
    if (!surface) {
      nav('/explore', { replace: true });
      return;
    }
    if (ENTITY_HEX.test(surface)) {
      const params = new URLSearchParams(search);
      nav(`/explore?${changeWorld(params, { q: params.get('q') || surface.toLowerCase(), selected: surface.toLowerCase(), answer: null })}`, { replace: true });
      return;
    }
    const params = changeWorld(new URLSearchParams(search), { q: ref, selected: null, answer: null, offset: null });
    nav(`/explore?${params.toString()}`, { replace: true });
  }, [ref, search, nav]);

  return <LoadingText>Opening Browse…</LoadingText>;
}
