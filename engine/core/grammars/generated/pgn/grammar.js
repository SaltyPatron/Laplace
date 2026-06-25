// Tree-sitter grammar for chess PGN (Portable Game Notation).
// Authored from scratch for Laplace (no third-party grammar code).
//
// Targets the chess.com export format: multiple games per file, each with a
// tag-pair section, a movetext section with move numbers / SAN moves /
// comments (including [%clk ...] command bodies) / NAGs / recursive
// variations, terminated by a game-result token.

module.exports = grammar({
  name: 'pgn',

  // Whitespace (spaces, tabs, newlines, CR) separates tokens everywhere and is
  // not semantically meaningful between movetext tokens, so it is "extra".
  extras: $ => [/[ \t\r\n]/],

  // The full SAN token (e.g. Nxg5, exd5, O-O-O, e8=Q+, Qf7#) is a single
  // contiguous token, and a NAG ($1) shares no prefix issues — no conflicts to
  // declare.
  conflicts: $ => [],

  rules: {
    // A file is a sequence of games. Allow zero games for empty input.
    series_of_games: $ => repeat($.game),

    // Every game ends with a mandatory game_result token, which is what makes
    // game boundaries unambiguous (the next '[' after a result starts a new
    // game; an optional terminator would make tags-of-next-game vs
    // tags-of-this-game undecidable).
    game: $ => choice(
      // A game with a tag-pair section (chess.com always emits one).
      seq(
        repeat1($.tag_pair),
        optional($.movetext),
        $.game_result,
      ),
      // Tolerate a bare movetext game with no tags.
      seq(
        $.movetext,
        $.game_result,
      ),
      // Tolerate a tags-only / result-only game.
      seq(
        repeat1($.tag_pair),
        $.game_result,
      ),
    ),

    // [Symbol "string"]
    tag_pair: $ => seq(
      '[',
      field('name', $.tag_name),
      field('value', $.tag_value),
      ']',
    ),

    tag_name: $ => /[A-Za-z0-9][A-Za-z0-9_+#=:\-]*/,

    // The quoted string value. Supports escaped quote (\") and backslash (\\).
    tag_value: $ => seq(
      '"',
      optional($._string_body),
      '"',
    ),

    // Movetext: move numbers, SAN moves, comments, NAGs, annotations, and
    // variations. The terminating result lives on `game`, not here.
    movetext: $ => repeat1(choice(
      $.move_number,
      $.san_move,
      $.comment,
      $.nag,
      $.annotation,
      $.variation,
    )),

    // Standalone annotation glyph (when an exporter space-separates it from the
    // move, e.g. "Nc6 !?"). The attached form stays part of the san_move token.
    annotation: $ => token(/[!?]{1,2}/),

    // Move number indicator: "1." for white, "1..." for black continuations.
    // chess.com also wraps these, but they are still single tokens.
    move_number: $ => token(seq(/[0-9]+/, choice('.', '...'))),

    // SAN move. Captured as one whole token including optional check (+),
    // mate (#), promotion (=Q), and annotation suffixes (!, ?, !!, ??, !?, ?!).
    // Castling accepts O-O / O-O-O and the 0-0 / 0-0-0 digit variants.
    san_move: $ => token(choice(
      // Castling (must come first so it is not mis-lexed as a pawn move).
      seq(
        choice('O-O-O', 'O-O', '0-0-0', '0-0'),
        optional(/[+#]/),
        optional(/[!?]{1,2}/),
      ),
      // Normal move: optional piece, optional disambiguation, optional capture,
      // destination square, optional promotion, optional check/mate, optional
      // annotation.
      seq(
        /[KQRBN]?/,                 // piece (omitted for pawns)
        /[a-h]?/,                   // disambiguation file or pawn-capture file
        /[1-8]?/,                   // disambiguation rank
        /x?/,                       // capture
        /[a-h][1-8]/,               // destination square
        /(=[QRBN])?/,               // promotion
        /[+#]?/,                    // check / mate
        /([!?]{1,2})?/,             // annotation suffix
      ),
    )),

    // Comment: { arbitrary text, including [%clk 0:09:56.2] }. No nesting in
    // PGN; the body is everything up to the closing brace.
    comment: $ => seq(
      '{',
      optional($._comment_body),
      '}',
    ),

    // Numeric Annotation Glyph, e.g. $1, $19.
    nag: $ => token(seq('$', /[0-9]+/)),

    // Recursive variation: ( movetext-like sequence ).
    variation: $ => seq(
      '(',
      repeat1(choice(
        $.move_number,
        $.san_move,
        $.comment,
        $.nag,
        $.annotation,
        $.variation,
      )),
      ')',
    ),

    // Game termination markers.
    game_result: $ => token(choice('1-0', '0-1', '1/2-1/2', '*')),

    // --- lexical tokens -----------------------------------------------------

    // String body between the quotes: any char except a bare " or \, or an
    // escape sequence \" / \\.
    _string_body: $ => repeat1(choice(
      /[^"\\]/,
      /\\./,
    )),

    // Comment body: any run of characters that is not the closing brace.
    _comment_body: $ => /[^}]+/,
  },
});
