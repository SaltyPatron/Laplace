







module.exports = grammar({
  name: 'pgn',

  
  
  extras: $ => [/[ \t\r\n]/],

  
  
  
  conflicts: $ => [],

  rules: {
    
    series_of_games: $ => repeat($.game),

    
    
    
    
    game: $ => choice(
      
      seq(
        repeat1($.tag_pair),
        optional($.movetext),
        $.game_result,
      ),
      
      seq(
        $.movetext,
        $.game_result,
      ),
      
      seq(
        repeat1($.tag_pair),
        $.game_result,
      ),
    ),

    
    tag_pair: $ => seq(
      '[',
      field('name', $.tag_name),
      field('value', $.tag_value),
      ']',
    ),

    tag_name: $ => /[A-Za-z0-9][A-Za-z0-9_+#=:\-]*/,

    
    tag_value: $ => seq(
      '"',
      optional($._string_body),
      '"',
    ),

    
    
    movetext: $ => repeat1(choice(
      $.move_number,
      $.san_move,
      $.comment,
      $.nag,
      $.annotation,
      $.variation,
    )),

    
    
    annotation: $ => token(/[!?]{1,2}/),

    
    
    move_number: $ => token(seq(/[0-9]+/, choice('.', '...'))),

    
    
    
    san_move: $ => token(choice(
      
      seq(
        choice('O-O-O', 'O-O', '0-0-0', '0-0'),
        optional(/[+#]/),
        optional(/[!?]{1,2}/),
      ),
      
      
      
      seq(
        /[KQRBN]?/,                 
        /[a-h]?/,                   
        /[1-8]?/,                   
        /x?/,                       
        /[a-h][1-8]/,               
        /(=[QRBN])?/,               
        /[+#]?/,                    
        /([!?]{1,2})?/,             
      ),
    )),

    
    
    comment: $ => seq(
      '{',
      optional($._comment_body),
      '}',
    ),

    
    nag: $ => token(seq('$', /[0-9]+/)),

    
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

    
    game_result: $ => token(choice('1-0', '0-1', '1/2-1/2', '*')),

    

    
    
    _string_body: $ => repeat1(choice(
      /[^"\\]/,
      /\\./,
    )),

    
    _comment_body: $ => /[^}]+/,
  },
});
