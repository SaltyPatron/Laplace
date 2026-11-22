import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Toggles in-place fullscreen for an existing DOM container via Fullscreen API
 * with a fixed-viewport CSS fallback, keeping the subtree (e.g. a WebGL Canvas)
 * identically mounted without resetting camera, WebGL context, or selection state.
 */
export function useInPlaceFullscreen<T extends HTMLElement = HTMLDivElement>() {
  const containerRef = useRef<T>(null);
  const [nativeFullscreen, setNativeFullscreen] = useState(false);
  const [cssFullscreen, setCssFullscreen] = useState(false);
  const isFullscreen = nativeFullscreen || cssFullscreen;

  useEffect(() => {
    const onFullscreenChange = () => {
      setNativeFullscreen(document.fullscreenElement === containerRef.current);
    };
    document.addEventListener('fullscreenchange', onFullscreenChange);
    return () => document.removeEventListener('fullscreenchange', onFullscreenChange);
  }, []);

  useEffect(() => {
    if (!cssFullscreen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setCssFullscreen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [cssFullscreen]);

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement === containerRef.current) {
      void document.exitFullscreen?.().catch(() => undefined);
      setCssFullscreen(false);
      return;
    }
    if (cssFullscreen) {
      setCssFullscreen(false);
      return;
    }
    const el = containerRef.current;
    if (el?.requestFullscreen) {
      void el.requestFullscreen().catch(() => setCssFullscreen(true));
    } else {
      setCssFullscreen(true);
    }
  }, [cssFullscreen]);

  return { containerRef, isFullscreen, cssFullscreen, toggleFullscreen };
}
