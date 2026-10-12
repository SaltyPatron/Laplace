import { matchPath, useLocation } from 'react-router-dom';

export function decodeRouteText(encoded: string): string {
  try { return decodeURIComponent(encoded); }
  catch { return encoded; }
}

/** Match the encoded pathname before decoding text. Router params can already
 * turn a literal "%2F" into "/" while handling encoded path separators. */
export function routeText(pattern: string, pathname: string, name: string): string {
  const encoded = matchPath(pattern, pathname)?.params[name] ?? '';
  return decodeRouteText(encoded);
}

export function useRouteText(pattern: string, name: string): string {
  return routeText(pattern, useLocation().pathname, name);
}
