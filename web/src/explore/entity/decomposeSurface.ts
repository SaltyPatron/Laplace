// Which entities the Structure tab may decompose, and with what text.
//
// /v1/explore/decompose decomposes free text. An entity's display label is
// presentation, not content: a concept is labelled by the word that lexicalizes
// it, a record by its parts joined with ' · ', an unrealized id by
// '<type> · <short id>'. Decomposing such a label describes a different entity
// (the word 'break' instead of the concept it names). Only a text entity at or
// below sentence tier is labelled by its own surface, and even then the result
// is trusted only when the decomposed root is this entity's own id.

interface SurfaceEntity {
  id_hex: string;
  label: string;
  tier?: number | null;
  type?: string | null;
}

export const TEXT_SURFACE_TYPES: ReadonlySet<string> = new Set([
  'Byte',
  'Codepoint',
  'Grapheme',
  'Word',
  'Ngram',
  'Phrase',
  'Sentence',
  'Tatoeba_Sentence',
  'Text',
]);

const DISPLAY_SEPARATOR = ' \u00b7 ';

/** The entity's own text surface, or null when its label is not its content. */
export function decomposableSurface(entity: SurfaceEntity, preview?: SurfaceEntity | null): string | null {
  const type = entity.type ?? preview?.type ?? null;
  const tier = entity.tier ?? preview?.tier ?? null;
  const label = entity.label || preview?.label || '';
  if (!label || label.includes(DISPLAY_SEPARATOR)) return null;
  if (tier == null || tier > 3) return null;
  if (type == null || !TEXT_SURFACE_TYPES.has(type)) return null;
  return label;
}

export function canDecomposeTextSurface(entity: SurfaceEntity, preview?: SurfaceEntity | null): boolean {
  return decomposableSurface(entity, preview) !== null;
}

/** A decomposition describes the entity only when it recomposes to the entity's id. */
export function decompositionMatchesEntity(entityIdHex: string, rootIdHex: string | null | undefined): boolean {
  return !!rootIdHex && rootIdHex.toLowerCase() === entityIdHex.toLowerCase();
}
