import { DIAL_DEFAULTS, type QueryDials } from '../query/types';

/** Addressed product context. Carrying a coordinate does not grant authority or
 * imply that a particular adapter implements its query predicate. */
export interface InformationWorld {
  observation: string;
  subject: string | null;
  comparison: string[];
  domain: string[];
  types: string[];
  relations: string[];
  sources: string[];
  evidence: string[];
  epoch: string | null;
  from: string | null;
  until: string | null;
  language: string | null;
  presentation: string | null;
  detail: string | null;
  ranking: string | null;
  grouping: string | null;
  bands: number[];
  dials: QueryDials;
  issues: string[];
}

const bounds: Partial<Record<keyof QueryDials, [number, number, boolean]>> = {
  depth: [1, 16, true], breadth: [1, 32, true], limit: [1, 500, true],
  steps: [1, 256, true], spread: [0, 1, false], max_stride: [1, 8, true],
};
export function readWorld(params: URLSearchParams): InformationWorld {
  const dials = { ...DIAL_DEFAULTS }, issues: string[] = [];
  for (const [key, [min, max, integral]] of Object.entries(bounds)) {
    const input = params.get(key); if (input === null) continue;
    const value = Number(input);
    if (!input.trim() || !Number.isFinite(value) || value < min || value > max || (integral && !Number.isInteger(value))) issues.push(`${key} must be ${integral ? 'an integer' : 'a number'} between ${min} and ${max}.`);
    else Object.assign(dials, { [key]: value });
  }
  dials.seed = params.get('seed') ?? '';
  if (dials.seed && !Number.isSafeInteger(Number(dials.seed))) issues.push('Seed must be an exactly representable integer.');
  for (const key of ['directed', 'use_geometry'] as const) {
    const value = params.get(key);
    if (value !== null && value !== 'true' && value !== 'false') issues.push(`${key} must be true or false.`);
    else if (value !== null) dials[key] = value === 'true';
  }
  const rawBands = params.getAll('band'), bands = rawBands.map(Number);
  if (rawBands.some(value => !value.trim()) || bands.some(value => !Number.isInteger(value) || value < 0 || value > 12)) issues.push('Band selectors must be integers from 0 through 12.');
  return { observation: params.get('q') ?? '', subject: params.get('selected'), comparison: [...params.getAll('other'), ...params.getAll('compare')],
    domain: params.getAll('world'), types: params.getAll('type'), relations: params.getAll('relation'), sources: params.getAll('source'),
    evidence: params.getAll('evidence'), epoch: params.get('epoch'), from: params.get('from'), until: params.get('until'), language: params.get('lang'),
    presentation: params.get('presentation'), detail: params.get('detail'), ranking: params.get('rank'), grouping: params.get('group'), bands, dials, issues };
}

/** A transition preserves peer coordinates and domain extensions by default. */
export function changeWorld(params: URLSearchParams, changes: Record<string, string | string[] | null>): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const [key, value] of Object.entries(changes)) {
    next.delete(key);
    if (Array.isArray(value)) value.forEach(item => next.append(key, item));
    else if (value !== null && value !== '') next.set(key, value);
  }
  return next;
}

export function selectWorldEntity(id: string, location: { pathname: string; search: string }, presentation?: string): string {
  if (['/explore', '/explore/warehouse', '/query'].includes(location.pathname)) {
    const current = new URLSearchParams(location.search);
    const previousPresentation = current.get('presentation');
    const next = changeWorld(current, { selected: id, answer: null, games_offset: null,
      presentation: presentation ?? (['classification', 'knowledge', 'mesh'].includes(previousPresentation ?? '') ? previousPresentation : null) });
    return `${location.pathname}?${next}`;
  }
  const next = changeWorld(location.pathname.startsWith('/explore/entity/') ? new URLSearchParams(location.search) : new URLSearchParams(),
    { selected: null, answer: null, games_offset: null, presentation: presentation ?? null });
  return `/explore/entity/${id}${next.size ? `?${next}` : ''}`;
}
