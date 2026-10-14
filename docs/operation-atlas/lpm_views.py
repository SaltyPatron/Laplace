"""liblpm local index lifecycle and recipe-specific composition."""
def add_lpm_views(add):
    f=['Laplace-MCP/native/lpm.c']
    add('lpm-index','Modular liblpm — file traversal and admission selection','''start
:stat supplied path; missing path returns minus one;
if (Directory?) then (yes)
 :Recursively enumerate entries;
 :Skip symlinks, named build/cache directories and unwanted extensions;
 :Skip child paths overflowing 4096-byte buffer;
else (file)
 :Use direct file path without extension allowlist;
endif
:For each selected file require regular file and size 1..1048576 bytes;
:Open and read exact stat size;
if (Empty/large/unreadable/short read?) then (yes)
 :Increment skipped; return zero for that file;
else (read)
 :lpm_admit content; allocation/admission failure propagates minus one;
endif
:Return traversal status;
stop
legend bottom
Zero status can mean skipped input rather than a newly admitted document.
lpm_last returns the last existing document. A caller that checks only status
can report a previous document after a skipped file, or no record on an empty index.
Directory order is readdir order, not sorted source order.
endlegend''',f,'Path traversal and file-read bodies reviewed. Direct path stat follows symlinks whereas recursive traversal rejects symlink entries. This is the local index path used by Python admit and native lpm CLI, distinct from Engine database ingest.',[])
    add('lpm-admit','Modular liblpm — local content and document occurrence admission','''start
if (Empty bytes?) then (yes)
 :Return zero without a document;
 stop
endif
:Reset features; enable collection; test bytes with Native UTF8 decoder;
if (UTF8 and csv/tsv suffix?) then (yes)
 :Compose fields, rows and file with delimited parser;
elseif (UTF8 and recognized grammar available?) then (yes)
 :Parse concrete syntax tree; compose child and interstitial text spans;
elseif (UTF8?) then (yes)
 :Use exact-byte text cache or Native decomposition;
else (binary)
 :Map each byte value to tier-zero codepoint record;
 :Hash ordered IDs; integer-average coordinates; set tier one;
 :Put trunk entity; no child-array registration in this branch;
endif
:Add trunk feature;
:Append a document occurrence with ID, placement, path, lines and byte count;
:Deduplicate feature IDs and add document postings;
:Disable collecting; increment files and byte totals; return zero;
stop
legend bottom
Entity table keys by 16-byte ID; repeated put increments seen.
Document occurrences remain separate even when trunk IDs match.
seen is an insertion counter, not independent testimony or consensus standing.
This code writes local memory; PostgreSQL admission is a different path.
endlegend''',f,'Admission branches, entity table, feature posting and exact-byte text cache reviewed. UTF8 acceptance follows Native generalized decoder behavior. Binary composition does not preserve an explicit child list here. Allocation failures may abort the process or return failure; no transactional rollback surrounds partial memory changes.',[])
    add('lpm-delimited','Modular liblpm — CSV/TSV structural composition','''start
:Split bytes into physical newline-delimited lines;
:Remove trailing carriage return; skip empty lines;
while (More lines?) is (yes)
 :Scan comma fields with quote state, or tab fields without quote handling;
 :In comma mode skip over doubled quote while scanning;
 :Strip one surrounding quote pair from a field when present;
 :Text-compose retained field bytes; empty field gives zero reference;
 :Compose ordered fields at max child tier plus one;
endwhile (no)
:Compose ordered rows at max row tier plus one;
:Return trunk; no rows gives zero reference;
stop
legend bottom
This body splits lines before quote parsing: quoted multiline CSV is not preserved here.
Doubled quotes affect scanning but are not unescaped from field bytes.
Separators, line endings and surrounding quote syntax are not child content.
The resulting structure is not established as an exact original-file byte round trip.
endlegend''',f,'admit_delimited reviewed. This local parser is not promoted to the general source-recipe contract. Empty fields, blank lines and syntax removal remain visible behavioral distinctions.',[])
    add('lpm-code-compose','Modular liblpm — grammar composition and text fallback','''start
:Choose grammar by recognized filename suffix;
:Load configured tree-sitter shared library and entry symbol;
if (No available grammar?) then (yes)
 :Caller falls back to cached text decomposition;
 stop
endif
:Reject input larger than uint32 in grammar helper;
:Create parser; set language; parse entire byte string;
:Recursively traverse all concrete children;
if (Leaf or recursion depth greater than 200?) then (yes)
 :Use exact span through text_cached;
else (branch)
 :Compose nonempty children recursively;
 :Keep bytes before, between and after child spans as text;
 :Compose resulting ordered pieces at max tier plus one;
endif
:Destroy tree/parser; return trunk;
stop
legend bottom
Node type labels are not added as semantic testimony by this composition body.
Parser error nodes are not separately rejected here.
text_cached verifies hash, byte length and exact bytes; cache hits replay feature IDs.
ent_kids stores at most 200000 children and skips allocation failure silently.
endlegend''',f,'Grammar selection, dynamic loader, AST recursion and text cache bodies reviewed. Grammar ABI compatibility, parser-null behavior, parse correctness and native exact-content guarantees remain external boundaries. This is not a code execution or software-correctness proof.',[])
    add('lpm-search-fetch','Modular liblpm — local search, lookup and bounded child fetch','''start
if (Search requested?) then (yes)
 :Require nonzero result capacity and existing documents;
 :Decompose query; collect and deduplicate features;
 :Restore file/byte/composition/document counters; retain created entities;
 :For each feature add document_count / posting_count to matching document rank;
 :Count shared features per document;
 :Select untaken positive-match documents by rank, then shared count;
 :Emit at most caller capacity with document ID, path and metadata;
else (Lookup/fetch)
 :Find exact ID in local hash table; missing returns minus one;
 :Return coordinate, tier, Hilbert, seen and found;
 :Return full child count; copy at most caller child capacity;
endif
stop
legend bottom
Search mutates the local entity table even though it adds no document occurrence.
Ranking uses local feature rarity, not typed consensus confidence.
This is distinct from native HTTP search over PostgreSQL holds and attestations.
endlegend''',f,'lpm_search, lpm_lookup and lpm_fetch reviewed. Their callers choose capacities (Python fetch shows eight children). Container and realization helpers remain separate local operations.',[])
    add('lpm-save-load','Modular liblpm — LPM1 file persistence and reconstruction','''start
if (Save?) then (yes)
 :Open target with wb; write LPM1 magic and version one;
 :Write entity IDs, coordinates, Hilbert, seen and tier;
 :Write document records and path bytes;
 :Write entity-to-document posting lists;
 :Close; return zero;
else (Load)
 :Open file; require LPM1 magic and version one;
 :Create tier-zero/text index;
 :Reinsert serialized entities; restore seen and Hilbert;
 :Recreate documents, paths, file count and byte totals;
 :Restore postings; missing referenced entity aborts;
 :Close; return index;
endif
stop
legend bottom
Child-ID arrays and exact-byte text-cache entries are not serialized.
Short fwrite/fread aborts the process. Save directly truncates the target;
there is no temp-file rename, checksum or explicit fsync in these bodies.
Scalar fields use host representation rather than a declared byte-order encoder.
endlegend''',f,'Complete save/load bodies reviewed. Reloading search postings does not prove preservation of constituent reconstruction, all counters or portable file representation. No save/load execution was performed for this review.',[])
