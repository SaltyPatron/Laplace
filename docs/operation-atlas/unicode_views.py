"""Unicode source contracts and observed implementation paths stay distinct."""
def add_unicode_views(add):
    modular='Laplace-Engine/src/tier0.c'
    native='Laplace/engine/core/src/unicode_seed.cpp'
    xml='Laplace/engine/core/src/ucd_xml.cpp'
    managed='Laplace/app/Laplace.Decomposers/Unicode/UnicodeDecomposer.cs'
    bridge='Laplace/app/Laplace.Core/Core/UnicodeSeed.cs'
    add('engine-unicode-collation','Modular Engine — Unicode property loading and total-order keys','''start
:Read DerivedGeneralCategory.txt into assigned flags excluding Cn/Cs;
:Read PropList.txt Unified_Ideograph and Blocks.txt core Han membership;
:Read UnicodeData.txt combining class and canonical decomposition mappings;
:Read allkeys.txt explicit single-codepoint entries and implicit ranges;
:Skip contractions and directives other than implicitweights;
while (Codepoint remains in fixed Unicode window?) is (yes)
 if (Explicit collation entry?) then (yes)
  :Use all its collation elements, including empty ignorable entry;
 elseif (Hangul syllable?) then (yes)
  :Concatenate leading, vowel and optional trailing jamo elements;
 elseif (Assigned in implicit range?) then (yes)
  :Build implicit pair relative to lowest range with same base;
 else (fallback)
  :Select core-Han / other-Han / catch-all implicit pair;
 endif
 :Build separate sequences of nonzero primary, secondary and tertiary weights;
 :Recursively canonically decompose; stably reorder nonstarters by combining class;
endwhile (done)
:Sort lexicographically by full L1, L2, L3, NFD and codepoint;
:Invert permutation into codepoint ranks;
:Pass ranks to fixed-population placement and record serialization;
stop''',[modular],'Entire tier0.c reviewed. This path does not read UCD XML or preserve its full property table. It ignores the allkeys version directive and performs no matching-release/conformance gate in this file. Parsing trusts range indices and fixed scratch/pool capacities; several realloc results are unchecked. Normalization input via UnicodeData.txt is permitted by Wiki 1.4, but is not a substitute for full XML/segmentation ingestion in 1.2/1.5. This is a source comparison, not tested UCA conformance.',['1.2','1.3','1.4','1.5','1.7'])
    add('monorepo-unicode-xml','Monorepo — UCD artifact decoding and XML event parser','''start
if (Path ends exactly in .zip?) then (yes)
 :Read whole archive; find end-of-central-directory;
 :Require nonzero entry count; read first central-directory entry;
 :Validate local header and payload bounds;
 if (Stored or deflated method?) then (yes)
  :Copy or inflate whole selected entry; require size and CRC;
 else (unsupported)
  :Return read failure;
  stop
 endif
else (plain)
 :Read whole XML file; reject empty bytes;
endif
while (More bytes?) is (yes)
 :Skip text until next tag;
 if (Comment, processing instruction or CDATA?) then (yes)
  :Skip to terminator; reject missing terminator;
 else (element)
  :Read ASCII-style name and quoted attribute pairs;
  :Reject malformed lexical delimiters;
  :Invoke start/end callbacks; self-closing tag invokes both;
 endif
endwhile (done)
:Return parser success;
stop''',[xml,native],'Complete XML event parser and both archive readers reviewed. The archive reader selects the first entry; despite its name it does not require exactly one entry or validate the entry filename. Parsing avoids a full AST but retains whole compressed/inflated buffers; it is not bounded streaming from disk. The event parser has no nesting stack, root/repertoire completeness check, entity-reference decoding or duplicate-attribute rejection. ValidateUcdXml uses no-op callbacks, so lexical success is not full UAX42/XML/repertoire conformance. Native snapshot callbacks add property interpretation separately.',['1.2','1.7'])
    add('monorepo-unicode-snapshot','Monorepo — native Unicode snapshot, ranks and staging','''start
:Allocate fixed-window records; retain inflated XML bytes;
:Initialize segmentation/combining/whitespace arrays to defaults;
:Parse repertoire events; inherit group attributes unless locally overridden;
:Recognize char, reserved, surrogate and noncharacter;
:Expand cp or first-cp/last-cp into property arrays;
:Read single-codepoint DUCET entries; retain only first collation element;
:For nonexplicit Hangul use leading-jamo key;
:Fill remaining keys from declared implicit ranges or hard-coded Han/fallback bands;
:Sort by packed key, then codepoint; derive rank;
while (Codepoint remains?) is (yes)
 :Compute open-placement point from rank;
 :Compute Hilbert128 and hash UTF8 codepoint;
 :Pack segmentation/combining/whitespace flags and store record;
endwhile (done)
:Return snapshot; managed wrapper requires fixed record count;
if (StageRange requested?) then (yes)
 :Validate bounds; hold snapshot reference across native call;
 :One crossing stages contiguous entities and physicalities;
 :Managed wrapper requires exactly count rows of each kind;
 :Record source span for staged physicalities;
else (semantic XML pass)
 :Expose bounded-copy stream over retained XML bytes;
endif
stop''',[native,xml,bridge],'Complete native snapshot compute/stage/copy routines and managed stage entry reviewed. Fixed record count follows preallocation and iteration, not proof that XML contained each codepoint exactly once. No seen bitmap detects gaps/duplicates; absent properties remain defaults, repeated ranges overwrite, out-of-range last codepoint is skipped. DUCET expansions after first element and NFD tie-breaks are absent here, unlike the modular generator; version directives are not enforced in this parser. Open-placement and UTF8 helpers are separate nested boundaries. Native stage failure can occur after appending a prefix; owner disposal/admission governs cleanup.',['1.2','1.3','1.4','1.5','1.8'])
    add('monorepo-unicode-admission','Monorepo — Unicode source floor through semantic admission','''start
:Resolve selected artifact jobs;
:Require exactly one complete-XML artifact and one DUCET artifact;
:Load installed UAX42/ucd.all.grouped.xml recipe and register cookbook;
:Claim files; resolve source roots and provenance/recipe identities;
if (Both file roots completed and ReObservePresent false?) then (yes)
 :Skip floor and XML semantic work;
else (admit)
 :Open immutable XML + DUCET native snapshot;
 :Yield contiguous floor ranges through common phase/admission machinery;
 :Yield tier0-floor-persisted barrier;
endif
:Load installed runtime perfcache only after floor barrier or completed-floor check;
if (New floor and no MaxInputUnits limit?) then (yes)
 :Replay retained XML bytes through installed native semantic recipe;
 :Bind semantic provenance; phase owns snapshot disposal;
endif
if (Floor skipped?) then (yes)
 :Emit skipped boundaries for both artifacts;
elseif (Input limit specified?) then (yes)
 :Emit cancelled boundaries for both artifacts;
else (full source)
 :Yield XML/DUCET provenance and recipe changes;
 :Yield provenance-persisted barrier;
 :Emit file completion or period boundaries;
endif
if (Input limit specified?) then (yes)
 :Dispose remaining snapshot and end iterator;
 stop
endif
:Yield license metadata;
:Run independent artifacts excluding XML, DUCET and repertoire compensation;
stop''',[managed,bridge],'Reviewed RunIngestAsync, PrepareFloor, RunFloorDataAsync, FinalizeFloorAsync and UcdXmlSemanticPhase; independent property parsers and full recipe interpreter remain nested. Barriers are yielded to the common ingestion executor, not direct commits in this iterator. Source floor generation does not use installed cache rows; later cache load is a separate dependency. The selected grouped-XML recipe differs from Wiki flat-XML input. Full XML property semantics are a second pass, separate from the small set packed into floor flags. No ingestion or conformance test executed.',['1.1','1.2','1.5','1.6','1.8'])
