import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';

/** Host routing for any sectioned workspace; preserves unrelated query parameters. */
export function useSectionParam<T extends string>(name: string, allowed: readonly T[], fallback: T) {
  const [params, setParams] = useSearchParams();
  const requested = params.get(name);
  const section = allowed.find((value) => value === requested) ?? fallback;
  const select = useCallback((value: T) => {
    if (!allowed.includes(value)) return;
    setParams((previous) => {
      const next = new URLSearchParams(previous);
      if (value === fallback) next.delete(name);
      else next.set(name, value);
      return next;
    });
  }, [name, allowed, fallback, setParams]);
  return [section, select] as const;
}
