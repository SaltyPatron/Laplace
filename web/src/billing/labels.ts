/**
 * Plain-language names for billing identifiers. Service ids such as
 * `chat.completions` are wire identifiers; people read names. The raw id stays
 * available to callers (for a `title` attribute) but is never the visible label.
 */
const SERVICE_NAMES: Record<string, string> = {
  'chat.completions': 'Conversation',
  'explain.trace': 'Reasoning trace',
  'visualization.deep_export': 'Web expansion',
  'audit.deep_report': 'Audit report',
  inspect: 'Inspection',
};

/** Splits a dotted/underscored identifier into a sentence-case phrase. */
export function humanizeIdentifier(id: string): string {
  const words = id.split(/[._\-\s]+/).filter(Boolean).map((word) => word.toLowerCase());
  if (words.length === 0) return 'Unnamed service';
  const phrase = words.join(' ');
  return phrase.charAt(0).toUpperCase() + phrase.slice(1);
}

/**
 * The visible name of a metered service: the fixed plain name when known, then the
 * catalog's display name when it is not itself an identifier, then a humanized id.
 */
export function serviceLabel(id: string, catalogName?: string | null): string {
  const known = SERVICE_NAMES[id];
  if (known) return known;
  const named = catalogName?.trim();
  if (named && !/^[a-z0-9]+([._][a-z0-9_]+)+$/.test(named)) return named;
  return humanizeIdentifier(id);
}

/** The visible name of the current workspace; never the raw tenant id. */
export function workspaceLabel(displayName: string | null | undefined): string {
  const named = displayName?.trim();
  return named ? named : 'This workspace';
}
