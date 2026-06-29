namespace Laplace.SubstrateCRUD;

// Tier is a SINGLE AXIS: COMPOSITION DEPTH = max(child_tier) + 1.
// It is emergent from grammar_compose / Merkle construction.
// It never encodes semantic kind, category, or modality type.
//
// The same integer means the same DEPTH across all modalities.
// Each modality names its levels differently; the integers are shared.
//
// ── UAX29 text (the canonical reference) ───────────────────────────────────
//   0  Codepoint        — Unicode scalar value (U+0041 LATIN CAPITAL LETTER A)
//   1  Grapheme         — UAX29 grapheme cluster (the minimal user-perceived character)
//   2  Word             — UAX29 word-break token (dictionary lookup unit)
//   3  Sentence         — UAX29 sentence-break span (clause/utterance)
//   4  Document         — structured text artifact (paragraph, article, book)
//
// ── Chess modality ──────────────────────────────────────────────────────────
//   0  Ply              — half-move (one side's action; the atomic chess token)
//   1  Full move        — ply pair (white + black, the "grapheme" of chess)
//   2  Position         — board state (Merkle of move sequence; the chess "word")
//   3  Variation        — named opening / variation / game segment (chess "sentence")
//   4  Game             — complete PGN game record (chess "document")
//
// ── Code / structural modalities ────────────────────────────────────────────
//   0  Token            — lexer token (keyword, identifier, literal, operator)
//   1  Expression       — primary expression (atom, call, index, member access)
//   2  Statement        — declaration/statement (assignment, control flow, def)
//   3  Block/Function   — named scope (function body, class body, module block)
//   4  File/Repository  — source file or repository artifact
//
// ── Image / spatial modalities ──────────────────────────────────────────────
//   0  Pixel            — single colour sample
//   1  Patch            — fixed-size tile or keypoint neighbourhood
//   2  Object           — detected object region (bounding box + mask)
//   3  Scene            — the full spatial layout of objects in a frame
//   4  Collection       — video, album, dataset of frames

public static class EntityTier
{
    // Depth 0: atomic unit (codepoint / ply / token / pixel)
    public const byte Codepoint = 0;

    // Depth 1: minimal perceptible unit (grapheme / full-move / expression / patch)
    public const byte Grapheme  = 1;

    // Depth 2: primary semantic unit (word / position / statement / object)
    // Named entities whose content IS their text (POS tags, language codes,
    // relation type names, player names) are legitimately at this depth because
    // their id = blake3(utf8_content) and their utf8_content is a single token.
    public const byte Word      = 2;

    // Depth 3: compositional span (sentence / variation / block / scene)
    public const byte Sentence  = 3;

    // Depth 4: top-level artifact (document / game / file / collection)
    public const byte Document  = 4;
}
