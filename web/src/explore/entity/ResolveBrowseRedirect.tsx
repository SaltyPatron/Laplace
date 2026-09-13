import { useEffect } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { LoadingText } from '@ui';

const ENTITY_HEX = /^[0-9a-f]{32}$/i;

/**
 * Compatibility redirect for historical /explore/resolve/:ref links.
 *
 * A surface string is not an entity merely because its deterministic content id can
 * be calculated.  Exact canonical ids may open an entity directly; all other input
 * returns to Browse so decomposition, admitted members and stored containment/name
 * candidates decide what actually exists in the substrate.
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
