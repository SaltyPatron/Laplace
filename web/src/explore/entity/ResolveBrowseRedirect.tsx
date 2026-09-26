import { useEffect } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { LoadingText } from '@ui';

const ENTITY_HEX = /^[0-9a-f]{32}$/i;

/**
 * Routes /explore/resolve/:ref. A 32-hex canonical id opens its entity page; any other
 * input goes to Browse, since a calculable content id does not make the string an admitted
 * entity — Browse resolves it through decomposition, admitted members and stored
 * containment and name candidates.
 */
export function ResolveBrowseRedirect() {
  const { ref = '' } = useParams();
  const nav = useNavigate();

  useEffect(() => {
    const surface = decodeURIComponent(ref).trim();
    if (!surface) {
      nav('/explore', { replace: true });
      return;
    }
    if (ENTITY_HEX.test(surface)) {
      nav(`/explore/entity/${surface.toLowerCase()}`, { replace: true });
      return;
    }
    const params = new URLSearchParams({ q: surface });
    nav(`/explore?${params.toString()}`, { replace: true });
  }, [ref, nav]);

  return <LoadingText>Opening Browse…</LoadingText>;
}
