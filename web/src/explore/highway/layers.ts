/**
 * The language highway, as a league table of divisions.
 *
 * Every entry is grounded in relation types that actually exist in
 * `laplace.canonical_names` — nothing here is aspirational vocabulary. A layer
 * whose read is not yet exposed over the API says so in `readGap` rather than
 * rendering an empty table that reads as "no data witnessed".
 *
 * `band` is the salience band that carries the layer's edges, and is what the
 * standings table reads via `/v1/query/leaders?bands=…`. Layers whose relations
 * are spread across bands (frames) or which are not relation-shaped at all
 * (the highway mask) carry no band and show their roster a different way.
 */
export interface HighwayLayer {
  /** URL segment. */
  slug: string;
  /** Division name. */
  name: string;
  /** Short type tag, as the mesh landing uses. */
  tag: string;
  /** What this layer is, in one sentence. */
  blurb: string;
  /** Relation types that constitute the layer. Verified present. */
  relations: string[];
  /** Salience band carrying these edges, when they sit in one. */
  band?: number;
  /** The standardized read: how you query this layer, exactly. */
  read: string;
  /**
   * What this layer contributes to inference — the question the detail page
   * exists to answer. Kept as prose until there is a metric behind it.
   */
  contributes: string;
  /** Set when the API cannot yet serve this layer's roster. */
  readGap?: string;
}

export const HIGHWAY_LAYERS: HighwayLayer[] = [
  {
    slug: 'iso',
    name: 'ISO 639 language axis',
    tag: 'language',
    blurb:
      'The language axis. Every 639-1/-2/-3 code for one language converges to a single language entity; a macrolanguage holds its members.',
    relations: [
      'HAS_LANGUAGE', 'IS_LANGUAGE_CODE', 'HAS_LANGUAGE_TYPE', 'HAS_LANGUAGE_SCOPE',
      'HAS_ISO639_1_CODE', 'HAS_ISO639_2_CODE', 'HAS_ISO639_2B_CODE', 'HAS_ISO639_2T_CODE',
      'MEMBER_OF_MACROLANGUAGE',
    ],
    band: 11,
    read: '/v1/query { topic, shape: "languages" } — which languages witness a concept',
    contributes:
      'Fixes the language of every surface, so the same concept witnessed in two languages folds to one entity instead of two.',
  },
  {
    slug: 'ili',
    name: 'ILI concept anchor',
    tag: 'synset',
    blurb:
      'The Collaborative Interlingual Index. The master hub a synset is addressed by, and where every language and source converge.',
    relations: ['HAS_SYNSET_KEY'],
    band: 3,
    read: '/v1/query { topic, shape: "translate", lang } — cross-lingual surfaces through the ILI hub',
    contributes:
      'The dedup floor above the codepoint floor: without it every source carries its own copy of a concept and nothing agrees with anything.',
  },
  {
    slug: 'sense',
    name: 'Sense and lemma',
    tag: 'sense',
    blurb:
      'A single reading of a word, binding one surface to one concept. The layer that decides which meaning a token is carrying.',
    relations: ['HAS_SENSE', 'HAS_SENSE_OF', 'IS_SENSE_OF', 'IS_LEMMA_OF', 'IS_SYNONYM_OF'],
    band: 3,
    read: '/v1/query { topic, shape: "synonyms" } — equivalence-band surfaces',
    contributes:
      'Disambiguation happens here or not at all. This is the layer that currently picks "hulk" for "whale".',
  },
  {
    slug: 'pos',
    name: 'Part of speech',
    tag: 'pos',
    blurb:
      'Universal and treebank-specific parts of speech, from WordNet and from Universal Dependencies.',
    relations: ['HAS_POS', 'HAS_UPOS', 'HAS_XPOS', 'UD_UPOS', 'UD_XPOS'],
    band: 9,
    read: '/v1/query { topic, shape: "related", relation_type: "HAS_POS" }',
    contributes:
      'Constrains what a surface can do syntactically — the cheapest filter on which senses are even reachable.',
  },
  {
    slug: 'frames',
    name: 'Frames, classes and roles',
    tag: 'frame',
    blurb:
      'The verb side of meaning: a scene a concept evokes, the roles it opens, and the VerbNet class and PropBank roleset behind it.',
    relations: [
      'EVOKES_FRAME', 'HAS_FRAME', 'HAS_FRAME_ELEMENT', 'IS_FRAME_OF', 'HAS_VERB_FRAME',
      'HAS_SEMANTIC_ROLE', 'HAS_THEMATIC_ROLE', 'IS_FILLED_BY', 'ROLE_CORRESPONDS_TO',
      'MEMBER_OF_VERBNET_CLASS', 'HAS_VALENCE_PATTERN',
    ],
    read: '/v1/query { topic, shape: "related", relation_type: "EVOKES_FRAME" }',
    contributes:
      'Turns a bag of concepts into a proposition with argument slots — the structure a prediction has to fill.',
  },
  {
    slug: 'taxonomy',
    name: 'Taxonomy',
    tag: 'is-a',
    blurb:
      'The IS_A ladder and its hypernym/hyponym inverses. How a concept generalises upward and specialises downward.',
    relations: [
      'IS_A', 'HAS_HYPERNYM', 'HAS_HYPONYM', 'IS_HYPERNYM_OF', 'IS_HYPONYM_OF',
      'IS_INSTANCE_OF', 'HAS_INSTANCE',
    ],
    band: 2,
    read: '/v1/query { topic, topic2, shape: "is_a" } — the witnessed IS_A chain between two topics',
    contributes:
      'Lets an answer generalise past what was literally witnessed, which is the whole of inference that is not lookup.',
  },
  {
    slug: 'definition',
    name: 'Definitional',
    tag: 'gloss',
    blurb: 'Witnessed glosses, disambiguated by context id. The highest-rated band in the substrate.',
    relations: ['HAS_DEFINITION', 'HAS_EXAMPLE'],
    band: 1,
    read: '/v1/query { topic, shape: "define" }',
    contributes:
      'The layer every other read falls back to, and the only one currently answering well end to end.',
  },
  {
    slug: 'highway-mask',
    name: 'Highway mask',
    tag: 'mask',
    blurb:
      'The per-entity highway bits — which lanes of the highway an entity participates in, stored as an entity attribute rather than as edges.',
    relations: [],
    read: 'entities.has_highway / entities.highway_bits (GIN)',
    contributes:
      'The routing mask a read consults before it walks anything; see docs/decisions/0001-highway-bit-order.md.',
    readGap:
      'Not exposed over the API. The bits live on laplace.entities (has_highway, highway_bits_gin, ~2.24M rows) with no endpoint that reads or filters them, so this division has no roster to show yet.',
  },
  {
    slug: 'deprel',
    name: 'Dependency relations',
    tag: 'deprel',
    blurb:
      'Universal Dependencies syntactic relations — the head/dependent structure of a sentence.',
    relations: [],
    read: '—',
    contributes:
      'Would carry sentence-internal structure, which is what turns a sentence from a bag of words into a parse.',
    readGap:
      'Not present in the substrate. The UD decomposer contributes UD_UPOS and UD_XPOS, but no dependency-relation type exists in laplace.canonical_names — there is no DEPREL, and nothing records head/dependent arcs.',
  },
];

export function findLayer(slug: string | undefined): HighwayLayer | undefined {
  return HIGHWAY_LAYERS.find((l) => l.slug === slug);
}
