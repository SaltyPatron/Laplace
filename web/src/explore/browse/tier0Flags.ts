/**
 * Unicode General Category and Script classification aligned with Tier-0 UCD metadata
 * (`tier0.flags`). Works identically with server-returned `flags`/`general_category`/`script`
 * fields or deterministic inspection of Unicode scalar values.
 */

const GENERAL_CATEGORY_LABELS: Record<string, string> = {
  Lu: 'Lu · Uppercase Letter',
  Ll: 'Ll · Lowercase Letter',
  Lt: 'Lt · Titlecase Letter',
  Lm: 'Lm · Modifier Letter',
  Lo: 'Lo · Other Letter',
  Mn: 'Mn · Nonspacing Mark',
  Mc: 'Mc · Spacing Mark',
  Me: 'Me · Enclosing Mark',
  Nd: 'Nd · Decimal Number',
  Nl: 'Nl · Letter Number',
  No: 'No · Other Number',
  Pc: 'Pc · Connector Punctuation',
  Pd: 'Pd · Dash Punctuation',
  Ps: 'Ps · Open Punctuation',
  Pe: 'Pe · Close Punctuation',
  Pi: 'Pi · Initial Punctuation',
  Pf: 'Pf · Final Punctuation',
  Po: 'Po · Other Punctuation',
  Sm: 'Sm · Math Symbol',
  Sc: 'Sc · Currency Symbol',
  Sk: 'Sk · Modifier Symbol',
  So: 'So · Other Symbol',
  Zs: 'Zs · Space Separator',
  Zl: 'Zl · Line Separator',
  Zp: 'Zp · Paragraph Separator',
  Cc: 'Cc · Control',
  Cf: 'Cf · Format',
  Cs: 'Cs · Surrogate',
  Co: 'Co · Private Use',
  Cn: 'Cn · Unassigned',
};

const GC_CODES = Object.keys(GENERAL_CATEGORY_LABELS);

const SCRIPTS = [
  'Latin',
  'Common',
  'Han',
  'Hiragana',
  'Katakana',
  'Hangul',
  'Greek',
  'Cyrillic',
  'Arabic',
  'Hebrew',
  'Devanagari',
  'Bengali',
  'Tamil',
  'Telugu',
  'Kannada',
  'Malayalam',
  'Thai',
  'Lao',
  'Tibetan',
  'Georgian',
  'Armenian',
  'Ethiopic',
  'Cherokee',
  'Runic',
  'Inherited',
] as const;

const GC_MATCHERS = GC_CODES.map((code) => ({
  code,
  re: new RegExp(`^\\p{General_Category=${code}}$`, 'u'),
}));

const SCRIPT_MATCHERS = SCRIPTS.map((script) => ({
  script,
  re: new RegExp(`^\\p{Script=${script}}$`, 'u'),
}));

export function classifyCodepointGeneralCategory(cp: number): string {
  if (!Number.isInteger(cp) || cp < 0 || cp > 0x10ffff) return 'Cn';
  if (cp >= 0xd800 && cp <= 0xdfff) return 'Cs';
  const ch = String.fromCodePoint(cp);
  for (const { code, re } of GC_MATCHERS) {
    if (re.test(ch)) return code;
  }
  return 'Cn';
}

export function classifyCodepointScript(cp: number): string {
  if (!Number.isInteger(cp) || cp < 0 || cp > 0x10ffff) return 'Unknown';
  if (cp >= 0xd800 && cp <= 0xdfff) return 'Unknown';
  const ch = String.fromCodePoint(cp);
  for (const { script, re } of SCRIPT_MATCHERS) {
    if (re.test(ch)) return script;
  }
  return 'Other';
}

export function formatGeneralCategory(code: string | null | undefined): string | null {
  if (!code) return null;
  return GENERAL_CATEGORY_LABELS[code] ?? code;
}

export interface Tier0FlagSummary {
  generalCategory: string | null;
  script: string | null;
  isWhiteSpace: boolean;
  isDigit: boolean;
  isLetter: boolean;
  isSymbol: boolean;
  rawHex: string | null;
}

/**
 * Inspects either server-supplied `general_category` / `script` / `flags` or the
 * underlying Unicode scalars of a surface label. Never invents categories when
 * no text or codepoint is present.
 */
export function inspectUnicodeFacets(input: {
  text?: string | null;
  codepoint?: number | null;
  tier?: number | null;
  flags?: number | string | null;
  generalCategory?: string | null;
  script?: string | null;
}): Tier0FlagSummary {
  const rawHex = input.flags != null && input.flags !== ''
    ? `0x${typeof input.flags === 'string' && input.flags.startsWith('0x') ? input.flags.slice(2) : Number(input.flags).toString(16)}`
    : null;

  const cps = input.codepoint != null
    ? [input.codepoint]
    : Array.from(input.text ?? '').map((c) => c.codePointAt(0) ?? -1).filter((cp) => cp >= 0);

  if (cps.length === 0) {
    return {
      generalCategory: formatGeneralCategory(input.generalCategory),
      script: input.script ?? null,
      isWhiteSpace: false,
      isDigit: false,
      isLetter: false,
      isSymbol: false,
      rawHex,
    };
  }

  let gc = formatGeneralCategory(input.generalCategory);
  if (!gc) {
    if (cps.length === 1 || input.tier === 0) {
      gc = formatGeneralCategory(classifyCodepointGeneralCategory(cps[0]));
    } else {
      const codes = new Set(cps.map((cp) => classifyCodepointGeneralCategory(cp)));
      const majors = new Set([...codes].map((c) => c[0]));
      if (codes.size === 1) {
        gc = formatGeneralCategory([...codes][0]);
      } else if (majors.size === 1) {
        const m = [...majors][0];
        gc = m === 'L' ? 'L · Letter span'
          : m === 'N' ? 'N · Numeric span'
          : m === 'P' ? 'P · Punctuation'
          : m === 'S' ? 'S · Symbol'
          : m === 'M' ? 'M · Mark'
          : m === 'Z' ? 'Z · Separator'
          : 'Mixed';
      } else if (majors.has('L')) {
        gc = 'Mixed · Letter + context';
      } else {
        gc = 'Mixed';
      }
    }
  }

  let script = input.script ?? null;
  if (!script) {
    const scripts = new Set(
      cps.map((cp) => classifyCodepointScript(cp)).filter((s) => s !== 'Common' && s !== 'Inherited'),
    );
    if (scripts.size === 0) {
      script = classifyCodepointScript(cps[0]);
    } else if (scripts.size === 1) {
      script = [...scripts][0];
    } else {
      script = 'Multi-script';
    }
  }

  const firstGc = classifyCodepointGeneralCategory(cps[0]);
  return {
    generalCategory: gc,
    script,
    isWhiteSpace: firstGc.startsWith('Z'),
    isDigit: firstGc === 'Nd',
    isLetter: firstGc.startsWith('L'),
    isSymbol: firstGc.startsWith('S'),
    rawHex,
  };
}
