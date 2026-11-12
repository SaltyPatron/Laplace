/**
 * Lens registry. A lens renders one view of any entity, anywhere (page, drawer, comparison, citation).
 * Facet lenses render strands from the entity contract; visual lenses (graph, placement) render their own
 * component. Type-specific lenses (chess game, code, glyph) register here with `appliesTo`.
 */
import type { ComponentType } from 'react';
import type { EntityRef, FacetName } from '../data/contract';

interface LensBase {
  id: string;
  label: string;
  /** Plain-language question the lens answers. */
  question: string;
  appliesTo?: (entity: EntityRef) => boolean;
}
export interface FacetLens extends LensBase {
  kind: 'facet';
  facet: FacetName;
  /** Empty-state wording when the facet returns nothing. */
  empty: string;
  /** Group strands by kind (relation) inside the lens. */
  groupByKind?: boolean;
}
export interface VisualLens extends LensBase {
  kind: 'visual';
  /** Lazily loaded so heavy canvases stay out of the main bundle. */
  load: () => Promise<{ default: ComponentType<{ id: string }> }>;
}
export type LensDef = FacetLens | VisualLens;

export const LENSES: LensDef[] = [
  { kind: 'facet', id: 'means', label: 'Means', question: 'What sources say about it, and how sure they are.', facet: 'means', groupByKind: true,
    empty: 'No source has attested anything about this yet.' },
  { kind: 'facet', id: 'appears-in', label: 'Appears in', question: 'Where it is used: the sentences, documents and structures that contain it.', facet: 'appears-in', groupByKind: false,
    empty: 'Nothing recorded contains this yet.' },
  { kind: 'facet', id: 'made-of', label: 'Made of', question: 'What it is built from, in order.', facet: 'made-of', groupByKind: false,
    empty: 'This is a single building block; it has no parts.' },
  { kind: 'facet', id: 'next-to', label: 'Next to', question: 'What comes before and after it where it is used.', facet: 'next-to', groupByKind: true,
    empty: 'No neighbouring content recorded.' },
  { kind: 'facet', id: 'occurs-with', label: 'Occurs with', question: 'What shares the same sentences and documents with it.', facet: 'occurs-with', groupByKind: false,
    empty: 'Nothing co-occurs with this yet.' },
  { kind: 'visual', id: 'classification', label: 'Classification', question: 'What it is a kind of, and what kinds of it there are.',
    load: () => import('./visual/ClassificationLens') },
  { kind: 'visual', id: 'mesh', label: 'Related structure', question: 'The parent relations and members around it.',
    load: () => import('./visual/StructureLens') },
  { kind: 'visual', id: 'comparison', label: 'Compare', question: 'Put it side by side with another subject.',
    load: () => import('./visual/CompareLens') },
  { kind: 'visual', id: 'graph', label: 'Web', question: 'Everything connected to it, as a web you can walk. Thicker strands pull harder.',
    load: () => import('./visual/GraphLens') },
  { kind: 'facet', id: 'shape', label: 'Shape', question: 'Content with a similar composition. Shape is not meaning.', facet: 'shape', groupByKind: false,
    empty: 'No shape-similar content found.' },
  { kind: 'visual', id: 'placement', label: 'Placement', question: 'Where it and its parts sit in the 4-D space, and the path through its parts.',
    load: () => import('./visual/PlacementLens') },
];

LENSES.unshift(
  { kind: 'visual', id: 'chess-game', label: 'Game', question: 'The recorded game: headers and a replayable board.',
    appliesTo: (e) => e.kind === 'Chess_Game', load: () => import('./visual/ChessLenses').then((m) => ({ default: m.GameLens })) },
);

/** Lenses for this entity: universal ones, type lenses that apply, and any lens the address explicitly asks for. */
export const lensesFor = (entity: EntityRef | null, requested?: string) =>
  LENSES.filter((lens) => !lens.appliesTo || lens.id === requested || (entity && lens.appliesTo(entity)));

/** Old entity-page addresses (?detail=…) keep opening the matching lens. */
export const LEGACY_DETAIL: Record<string, string> = { overview: 'means', graph: 'graph', glome: 'placement', structure: 'made-of', links: 'means', provenance: 'means' };
