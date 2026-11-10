/**
 * The whole navigation in one place. Destinations are things a person wants to do, in their words.
 * Existing route addresses are kept so saved links keep working; `owns` decides which item is current.
 */
import type { IconName } from '../ui/icons';

export interface NavItem {
  id: string;
  label: string;
  path: string;
  icon: IconName;
  /** Path prefixes this item is current for. */
  owns: string[];
  description: string;
  children?: { id: string; label: string; path: string; owns: string[] }[];
  /** Shown only when the viewer has platform tools. */
  admin?: boolean;
}

export const NAV: NavItem[] = [
  {
    id: 'lookup', label: 'Look up', path: '/explore', icon: 'search',
    description: 'What something means, where it is used, and who says so.',
    owns: ['/explore', '/query', '/topic'],
    children: [
      { id: 'search', label: 'Search', path: '/explore', owns: ['/explore', '/query', '/topic', '/explore/entity', '/explore/notfound', '/explore/resolve'] },
      { id: 'sources', label: 'Sources', path: '/explore/sources', owns: ['/explore/sources', '/explore/warehouse', '/explore/source', '/explore/stage'] },
      { id: 'highway', label: 'Identifiers', path: '/explore/highway', owns: ['/explore/highway'] },
    ],
  },
  {
    id: 'compare', label: 'Compare', path: '/explore/matchup', icon: 'compare',
    description: 'Two subjects side by side: meaning, usage and shape.',
    owns: ['/explore/matchup', '/explore/mesh'],
  },
  {
    id: 'explore', label: 'Explore', path: '/explore/constellation', icon: 'globe',
    description: 'Maps of the substrate and the proofs behind it.',
    owns: ['/explore/constellation', '/unicode', '/proof', '/explore/walk', '/forward-proof'],
    children: [
      { id: 'constellation', label: 'Map', path: '/explore/constellation', owns: ['/explore/constellation'] },
      { id: 'unicode', label: 'Characters', path: '/unicode', owns: ['/unicode'] },
      { id: 'proof', label: 'Storage proof', path: '/proof', owns: ['/proof'] },
      { id: 'walk', label: 'Reasoning trace', path: '/explore/walk', owns: ['/explore/walk', '/forward-proof'] },
    ],
  },
  { id: 'ask', label: 'Ask', path: '/chat', icon: 'chat', description: 'Ask a question; every answer cites its sources.', owns: ['/chat'] },
  { id: 'data', label: 'My data', path: '/data', icon: 'upload', description: 'Add your own files and see them decomposed.', owns: ['/data'] },
  {
    id: 'chess', label: 'Chess', path: '/chess', icon: 'chess',
    description: 'Players, games, and playing against Laplace.',
    owns: ['/chess', '/play', '/lab'],
    children: [
      { id: 'players', label: 'Players', path: '/chess', owns: ['/chess/players'] },
      { id: 'games', label: 'Laplace games', path: '/chess/laplace', owns: ['/chess/laplace', '/chess/games'] },
      { id: 'play', label: 'Play', path: '/play', owns: ['/play'] },
      { id: 'lab', label: 'Lab', path: '/lab', owns: ['/lab'] },
    ],
  },
  {
    id: 'admin', label: 'Admin', path: '/operator', icon: 'settings', admin: true,
    description: 'Workspace, people, ingestion and platform operations.',
    owns: ['/operator', '/explore/audit', '/explore/architecture'],
    children: [
      { id: 'operator', label: 'Administration', path: '/operator', owns: ['/operator'] },
      { id: 'audit', label: 'Counts', path: '/explore/audit', owns: ['/explore/audit'] },
      { id: 'architecture', label: 'Architecture', path: '/explore/architecture', owns: ['/explore/architecture'] },
    ],
  },
];

const under = (pathname: string, prefix: string) => pathname === prefix || pathname.startsWith(`${prefix}/`);

/** The most specific owner wins, so /explore/matchup belongs to Compare rather than Look up. */
export function currentNav(pathname: string): { item: NavItem | null; child: NonNullable<NavItem['children']>[number] | null } {
  let best: { item: NavItem; length: number } | null = null;
  for (const item of NAV) for (const prefix of item.owns) {
    if (under(pathname, prefix) && (!best || prefix.length > best.length)) best = { item, length: prefix.length };
  }
  if (!best) return { item: null, child: null };
  let child: { c: NonNullable<NavItem['children']>[number]; length: number } | null = null;
  for (const c of best.item.children ?? []) for (const prefix of c.owns) {
    if (under(pathname, prefix) && (!child || prefix.length > child.length)) child = { c, length: prefix.length };
  }
  return { item: best.item, child: child?.c ?? null };
}
