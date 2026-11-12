import { lazy } from 'react';

const GamePage = lazy(() => import('../chess/db/GamePage').then(module => ({ default: module.GamePage })));
const Taxonomy = lazy(() => import('./TaxonomyView').then(module => ({ default: module.TaxonomyView })));
const Matchup = lazy(() => import('../explore/matchup/MatchupView').then(module => ({ default: module.MatchupView })));
const RelatedStructure = lazy(() => import('../explore/mesh/MeshView').then(module => ({ default: module.MeshView })));

/** Finite renderer registrations consume declared type or an explicit requested
 * projection. Domain services validate their own canonical references. */
export const presentations = [
  { id: 'knowledge', label: 'Knowledge', types: [] as string[], render: null },
  { id: 'classification', label: 'Classification', types: [] as string[], render: (id: string) => <Taxonomy id={id} /> },
  { id: 'comparison', label: 'Comparison', types: [] as string[], render: (id: string) => <Matchup selectedId={id} embedded /> },
  { id: 'mesh', label: 'Related structure', types: [] as string[], render: (id: string) => <RelatedStructure selectedId={id} embedded /> },
  { id: 'chess-game', label: 'Game', types: ['Chess_Game'], render: (id: string) => <GamePage selectedId={id} embedded /> },
];
