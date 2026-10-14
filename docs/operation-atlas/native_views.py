"""Reviewed text and composition branches; no native execution or acceptance claim."""
def add_native_views(add):
    add('engine-text','Modular Engine — text', '''start
if (Text argument absent?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; create text context;
:entity_named delegates to lp_text_parts;
:Receive trunk, up to 64 copied parts and full part count;
:Compute tier0 fingerprint;
:Print identity, tier, coordinate, Hilbert value and depth;
:Print inside/OUTSIDE wall result;
:Print full part count and at most 64 part details;
:Print fingerprint and timing; return 0;
stop''', ['Laplace-Engine/src/pull.c'], 'No database call in this handler. OUTSIDE is a printed result, not a nonzero exit here. Context/map failures remain nested. Text segmentation, factorization and byte decoding are linked in the Native views.', [])
    add('native-text','Native — text decomposition and parts', '''start
:Caller supplies initialized text context and original bytes;
:Save compose callback and sink; copy bytes for segmentation;
:Replace lone interior CR/LF with equal-length spaces in copy;
:Open ICU UTF8 text over copy; collect sentence, word and grapheme bounds;
:Tailor word boundaries using original joiner codepoints;
:Clear paragraph and sentence accumulators;
while (More sentence intervals?) is (yes)
 :Clear segment accumulator;
 while (More word intervals within sentence?) is (yes)
  :Clear grapheme accumulator;
  while (More grapheme intervals within word?) is (yes)
   :Clear codepoint accumulator;
   while (Original-byte cursor below interval end?) is (yes)
    :lp_utf8_next;
    if (Decode failed?) then (yes)
     :Use U+FFFD; advance one byte;
    endif
    :lp_ref_atom reads identity/coordinate from tier0;
   endwhile (no)
   :made(codepoints, tier 1);
  endwhile (no)
  :made(graphemes, tier 2);
 endwhile (no)
 :made(segments, tier 3);
 if (Final sentence or trailing non-replaced paragraph break?) then (yes)
  :made(sentences, tier 4); append paragraph; clear sentences;
 endif
endwhile (no)
:Flush remaining sentences to paragraph; close ICU text;
:made(paragraphs, tier 5); return trunk;
stop''', ['Laplace-Native/src/text.c','Laplace-Native/src/utf8.c','Laplace-Native/src/compose.c'], 'Original bytes drive atom decoding, but malformed bytes are replaced with U+FFFD, so the source comment promising byte-for-byte recomposition is not unconditional. ICU setup status in decomposition/bounds_of is not converted into a typed failure. grow aborts on allocation failure. made can collapse or factor levels; nominal tiers are not guaranteed final tiers.', [])
    add('native-made','Native — composition callback, factoring and parts', '''start
:made receives child references and nominal tier;
if (One child?) then (yes)
 :Return child unchanged;
 stop
endif
if (At least four children?) then (yes)
 :Ensure factoring scratch; lp_factor;
 if (Factored count differs?) then (yes)
  :Use factored child references;
  if (One child remains?) then (yes)
   :Return child unchanged;
   stop
  endif
 endif
endif
:Raise tier to at least lp_tier_above(children);
if (Compose callback supplied?) then (yes)
 :Call callback with sink, children and tier;
else (no)
 :lp_ref_compose;
endif
if (Parts tracking enabled?) then (yes)
 :Copy last composition children, count and identity;
endif
:Return composed reference;
stop
legend
lp_text_parts enables tracking before decomposition and disables it after.
If no tracked composition matches the returned trunk, it uses [trunk].
It copies min(total, capacity) children, but reports the full total.
endlegend''', ['Laplace-Native/src/text.c','Laplace-Native/src/compose.c'], 'This is a code helper, not an additional claimed product operation. Callback effects belong to the caller; a decomposition can invoke the callback repeatedly before later failure. The zero-child case reaches composition rather than the single-child shortcut.', [])
    add('native-factor','Native — repeated-block factorization', '''start
:Initialize input cursor and output count;
while (Children remain?) is (yes)
 :Search block lengths from two through half the remaining length;
 :Choose shortest block repeated twice, comparing ordered child IDs;
 :Exclude blocks containing only repetitions of one child;
 if (No repeated block?) then (yes)
  :Copy one reference; advance cursor;
 else (block found)
  :Count consecutive identical blocks;
  :Allocate block scratch, stack for at most 64 children;
  :Recursively factor one block;
  :Compose factored block using callback or lp_ref_compose;
  :Free heap scratch; clear block said field;
  :Append one block reference per repetition;
  :Advance input by block length times repeats;
 endif
endwhile (no)
:Return output reference count;
stop''', ['Laplace-Native/src/compose.c'], 'Factorization compares IDs, not full reference bytes. Simple identical-child runs remain for a separate run-encoding layer. This routine emits repeated block references, not a compressed run field. Recursion, allocator behavior and callback side effects need caller-specific accounting.', ['11.5','11.6'])
    add('native-utf8','Native — generalized UTF8 decoder', '''start
:Caller provides cursor below input length;
:Read leading byte;
if (ASCII?) then (yes)
 :Return codepoint; advance one; true;
 stop
endif
:Choose length from leading byte thresholds;
if (Invalid lead or insufficient remaining bytes?) then (yes)
 :Return false; cursor unchanged;
 stop
endif
:Initialize codepoint from leading payload;
while (More continuation bytes?) is (yes)
 if (Byte does not begin 10?) then (yes)
  :Return false; cursor unchanged;
  stop
 endif
 :Shift codepoint and append payload;
endwhile (no)
if (Codepoint at or above LP_NCP?) then (yes)
 :Return false; cursor unchanged;
 stop
endif
:Return codepoint; advance by selected length; true;
stop''', ['Laplace-Native/src/utf8.c'], 'Surrogates are accepted intentionally. No shortest-encoding check occurs in this function: overlong encodings below LP_NCP can be accepted. Callers differ: text decomposition replaces failures one byte at a time, whereas lp_ref_codepoints returns a zero reference with ok=false. No malformed-input execution was performed.', [])
