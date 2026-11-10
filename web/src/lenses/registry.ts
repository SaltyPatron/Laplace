/**
 * Lens registry. A lens renders one facet of any entity, anywhere (page, drawer, comparison, citation).
 * Type-specific lenses (chess board, player record, code, glyph) register here with `appliesTo`.
 */
import type { EntityRef, FacetName } from '../data/contract';

export interface LensDef {
  id: string;
  label: string;
  /** Plain-language question the lens answers. */
  question: string;
  facet: FacetName;
  /** Empty-state wording when the facet returns nothing. */
  empty: string;
  appliesTo?: (entity: EntityRef) => boolean;
  /** Group strands by kind (relation) inside the lens. */
  groupByKind?: boolean;
}

export const LENSES: LensDef[] = [
  { id: 'means', label: 'Means', question: 'What sources say about it, and how sure they are.', facet: 'means', groupByKind: true,
    empty: 'No source has attested anything about this yet.' },
  { id: 'appears-in', label: 'Appears in', question: 'Where it is used: the sentences, documents and structures that contain it.', facet: 'appears-in', groupByKind: false,
    empty: 'Nothing recorded contains this yet.' },
  { id: 'made-of', label: 'Made of', question: 'What it is built from, in order.', facet: 'made-of', groupByKind: false,
    empty: 'This is a single building block; it has no parts.' },
  { id: 'next-to', label: 'Next to', question: 'What comes before and after it where it is used.', facet: 'next-to', groupByKind: true,
    empty: 'No neighbouring content recorded.' },
  { id: 'occurs-with', label: 'Occurs with', question: 'What shares the same sentences and documents with it.', facet: 'occurs-with', groupByKind: false,
    empty: 'Nothing co-occurs with this yet.' },
  { id: 'shape', label: 'Shape', question: 'Content with a similar composition. Shape is not meaning.', facet: 'shape', groupByKind: false,
    empty: 'No shape-similar content found.' },
];

export const lensesFor = (entity: EntityRef | null) => LENSES.filter((lens) => !lens.appliesTo || (entity && lens.appliesTo(entity)));
