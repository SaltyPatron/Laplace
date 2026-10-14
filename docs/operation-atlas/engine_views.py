"""Additional reviewed Engine orchestrations, registered by the trace builder."""
def add_engine_views(add):
    def view(name,file,body,note):
        add('engine-'+name,'Modular Engine — '+name,'start\n'+body+'\nstop', ['Laplace-Engine/src/'+file],note+' Source review only; no Engine command executed.',[])
    view('tier0','tier0.c','''
:Parse UCD root and output path;
if (Unexpected arguments?) then (yes)
 :Return 2;
 stop
endif
:load_ucd; load_allkeys; build_keys;
:Sort every codepoint by keycmp; construct rank array;
:Generate fixed-population Super-Fibonacci points;
:Sort points by Hilbert key;
while (More codepoints?) is (yes)
 :Select Hilbert-ordered point at codepoint rank;
 :Round four coordinates to fixed integer grid;
 while (Squared norm exceeds 2^106?) is (yes)
  :Move largest absolute coordinate one integer unit inward;
 endwhile (no)
 :Hash codepoint ID; store coordinate, Hilbert value and rank;
endwhile (no)
:Open output directly; write all records;
if (Open or record write fails?) then (yes)
 :Return 1; partial output may remain;
 stop
endif
:Close; hash record table and three-byte codepoint order;
:Print fingerprints and record count; return 0;''','UCD loading and key ordering are expanded in engine-unicode-collation; native Hilbert kernels remain nested. The handler uses fixed-population placement, not an open-ended radical-inverse insertion algorithm. Output replacement is direct; fclose status is not checked.')
    view('flags','flags.c','''
:Parse UCD root and flags output path;
if (Unexpected arguments?) then (yes)
 :Return 2;
 stop
endif
:Read property aliases and enumerated value aliases;
:Assign one bit per binary property, then widths for enumerated values;
if (Total width exceeds 256?) then (yes)
 :Return 1;
 stop
endif
:Allocate zeroed 32-byte records and coverage bitmap;
while (More XML lines?) is (yes)
 :Accept char, reserved, noncharacter and surrogate elements;
 :Read property values and codepoint range;
 :Count unknown values; set recognized property bits;
 if (Valid codepoint range?) then (yes)
  :Copy record across range; update coverage;
 endif
endwhile (no)
:Count enumerated values across every record;
:Open flags output directly and write records;
if (Open or record write fails?) then (yes)
 :Return 1;
 stop
endif
:Close records; open layout output;
if (Layout open fails?) then (yes)
 :Return 1; records already written;
 stop
endif
:Write bit layout; close; hash record bytes;
:Report unknown/unseen properties, coverage and fingerprint; return 0;''','Uncovered codepoints retain zeroed records. Unknown enumerated values are counted but do not fail this wrapper. Record and layout writes are separate; text writes and close statuses are unchecked. open_in failures remain helper behavior.')
    view('highway','highway.c','''
:Parse output path and fingerprint mode;
if (Unexpected arguments?) then (yes)
 :Return 2;
 stop
endif
if (Fingerprint mode?) then (yes)
 :Map existing highway;
 if (Map fails?) then (yes)
  :Return 1;
 else (no)
  :Print existing fingerprint; return 0;
 endif
 stop
endif
:Initialize contexts; disable reading highway being built; load recipes;
while (More sources declaring highway data?) is (yes)
 if (Source root found?) then (yes)
  if (Broken recipes?) then (yes)
   :Return 2;
   stop
  endif
  :Enumerate files; highway_file gathers types, keys, aliases and mappings;
 else (no)
  :Report omitted source;
 endif
endwhile (no)
:Resolve aliases until no key is added;
:freeze slots using manifest; number lists;
:Resolve edges to slots; omit unresolved mappings; sort and deduplicate;
if (No type records?) then (yes)
 :Return 1; existing highway output unchanged;
 stop
endif
:Construct records; write fresh binary;
if (Open, record write or swap fails?) then (yes)
 :Return 1;
 stop
endif
:Binary output replaced;
:Open fresh layout; read and validate manifest banks;
if (Open/bank validation/layout swap fails?) then (yes)
 :Return 1; binary replacement remains;
 stop
endif
:Write and swap keys sidecar;
if (Open or swap fails?) then (yes)
 :Return 1; earlier replacements remain;
 stop
endif
:Write type node compositions and slot IDs; swap nodes sidecar;
if (Open or swap fails?) then (yes)
 :Return 1; earlier replacements remain;
 stop
endif
:Report omitted mappings/keys, counts and binary fingerprint; return 0;''','freeze, highway_file, node_write and swap_in remain nested. Four files are replaced separately; no whole-set transaction is established. Edge writes, text writes and fclose statuses are not all checked. The no-record guard follows freeze and does not establish that manifest state is untouched.')
    view('hop','pull.c','''
:Parse options; load hop firmware and default fan/confidence;
if (Argument count is neither one nor three?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; create text context; connect for reads;
if (Whole entity request?) then (yes)
 :Resolve entity; claims_of;
else (pattern)
 :Resolve subject/predicate/object, leaving question marks open;
 if (All parts open?) then (yes)
  :Return 2;
  stop
 endif
 :claims_like;
endif
:Apply refused and weighed; retrieve witness positions;
:Order pattern results by witness position when configured;
:Decode and print at most limit claims;
if (Pattern request?) then (yes)
 :Cleanup; return 0;
 stop
endif
:Query non-claim containers of whole entity;
if (Query fails?) then (yes)
 :Return 1; claims may already have been printed;
 stop
endif
:Print observed container counts by tier separately from attested claims;
:Cleanup; return 0;''','Refusal, weighting, claim retrieval and text reconstruction remain nested. Observed containment is displayed separately from testimony; absence of claims does not itself make this handler fail.')
    view('translate','pull.c','''
:Parse source word, source language, target languages and options;
:Load translate firmware;
if (Missing arguments or missing up/language route?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; open read connection and reader; resolve relation IDs;
:Read first upward claims in witness order;
while (More upward steps and frontier nonempty?) is (yes)
 :open_each for entire frontier;
 if (Last upward step?) then (yes)
  :Retain first concept and standing per origin;
 else (no)
  :Advance claimed targets;
  :For uncarried steps, expand constituents;
  :Exclude original word unless composition contains only that word;
 endif
endwhile (no)
:Resolve synset languages; choose distinct concepts in source language up to limit;
:Read optional gloss and downward synsets in batches;
:Keep requested target languages, at most 64;
while (More downward steps and frontier nonempty?) is (yes)
 :Read inverse relation for whole frontier;
 if (Uncarried step above final word step?) then (yes)
  :Fall back to containing entities;
 endif
endwhile (no)
:Print concept standing, gloss and at most eight unique words per target;
:Print no-concept / nothing-attested labels where applicable;
:Cleanup; return 0;''','Firmware defines the relation route. This traverses attested concepts and structural fallbacks; it is not a generic neural translation call. No-concept results still return 0. open_each, language_each and parts_each require nested review.')
    view('degrees','pull.c','''
:Parse search options;
if (Missing origin or batch below one?) then (yes)
 :Return 2;
 stop
endif
:Load firmware; resolve origin, optional goal and weights;
:Open reads; seed forward frontier;
if (Goal supplied?) then (yes)
 :Seed backward frontier; best = infinity;
 while (Open costs can improve best and a frontier is available?) is (yes)
  :Expand side with lower open cost, bounded by half hop budget;
  if (Expansion empty?) then (yes)
   break
  endif
  :Compare meetings with opposite frontier; exclude hubs;
  :Keep cheaper meeting within total hop budget;
 endwhile (no)
 if (No meeting found?) then (yes)
  :Report no chain; cleanup; return 1;
  stop
 endif
 :Reconstruct forward chain and reversed backward chain; print;
else (nearest)
 while (Fewer than limit known or open frontier may improve top results?) is (yes)
  :expand forward frontier;
  if (Expansion empty?) then (yes)
   break
  endif
  :Collect closed entities except origin;
 endwhile (no)
 :Sort by cost; reconstruct and print up to limit chains;
endif
:Report expansion statistics; cleanup; return 0;''','expand, frontier ordering, costs and show_chain remain nested. The diagram assumes a positive result limit: this wrapper checks batch but does not reject zero/negative limit, and the nearest branch indexes near[limit-1]. Source review does not establish global optimality under fan/hop limits.')
    view('bench','bench.c','''
:Report available and active CPU dispatch;
:Time codepoint and composition identity kernels;
:Time ID geometry conversion, EWKB encoding and fixed-coordinate kernels;
:Time scalar continuation scan;
if (AVX2 supported?) then (yes)
 :Time AVX2 scan and later distance kernels;
endif
if (AVX512 supported?) then (yes)
 :Time AVX512 scan;
endif
:Time scalar distance, Frechet, DTW, EDR and outlier kernels;
if (Tier0 can be mapped?) then (yes)
 :Time text decomposition and codepoint lookup;
else (no)
 :Print text measurement skipped;
endif
:Time frontier reach/drain;
:Time scalar attestation and trust-bounded series;
:Return 0;''','This measures kernels on synthetic/fixed inputs. It does not compare outputs against correctness expectations; missing tier0 skips text timing and still returns 0. Arguments are ignored by this handler; memory allocation failures are not consistently checked.')
    add('engine-model','Modular Engine — model build variants','''start
if (Build provides laplace_model target?) then (no)
 :Compile model_none.c handler;
 :Print MKL/OpenMP requirement; return 1;
 stop
else (yes)
 :Compile model.c handler;
endif
if (No model directory?) then (yes)
 :Return 2;
 stop
endif
:Parse layer count, z threshold, cap and sample words;
if (MKL reproducible mode refused?) then (yes)
 :Return 1;
 stop
endif
:Map tier0; parse model config and vocabulary;
:Load model.safetensors embedding, unembedding and final norm;
:Fold norm gain into unembedding;
:Measure normalized embedding similarity and direct output signatures;
while (More selected layers?) is (yes)
 :Normalize original embeddings using layer input norm;
 :Project Q, K and V;
 while (More attention heads?) is (yes)
  :Measure QK row signatures;
  :Measure low-rank OV row signatures;
 endwhile (no)
 :Transpose down-projection directions;
 :Measure FFN neuron-to-token output signatures;
 :Report layer statistics;
endwhile (no)
:Report timing and FLOPs; return 0;
stop''',['Laplace-Engine/src/model.c','Laplace-Engine/src/model_none.c','Laplace-Engine/CMakeLists.txt'],'The normal handler reports candidate circuit signatures; it contains no database persistence/admission step. Deeper layers use original embeddings rather than an actual residual trajectory. Parsing, tensor validation and lp_rowsig remain nested. Numeric option reads do not consistently check that a following argument exists. Build branch is source evidence, not a claim about an installed binary.',[])
    view('pull','forward.c','''
:Parse prompt, firmware and optional seed;
if (Prompt absent?) then (yes)
 :Return 2;
 stop
endif
:Select seed; load firmware; decompose prompt and constituents;
:Open read connection and reader;
:laplace_forward retrieves observed segments, run counts and continuations;
if (Query fails?) then (yes)
 :Return 1;
 stop
endif
:Print bounded segment preview; distinguish hubs from counted runs;
:Gather up to 2048 observations holding multiword runs;
:Find standing-bearing claims by observation tier;
:Reject forbidden predicates; sum weighted confidence at opposite endpoints;
:Exclude prompt's own words; print strongest returned pulls;
while (More firmware take steps and no curated fact held back rest?) is (yes)
 switch (Take kind)
 case (fact)
  :Lazily fetch prompt claims; refuse and weigh;
  :Walk witness attestations; choose claim meeting trust threshold;
  if (Qualifying claim found?) then (yes)
   :Print fact; hold back remaining take steps;
  endif
 case (segment)
  :Find every exact prompt occurrence in containing paths;
  :Follow most frequent continuation among surviving trajectories;
  :Stop at end, dominant ending, divergent singletons or 4096 constituents;
  :Apply configured seeded near-top choice; print segment if any;
 case (attestations)
  :Lazily fetch, refuse and weigh prompt claims;
  :take_top; print configured count;
 case (constituents)
  :Fetch claims for unique composed constituents in one set;
  :Apply fan, refusal, weights and take_top per constituent; print;
 case (chain)
  :Rank unique prompt words using attested firmware roles;
  :Try configured relation chains for selected positive-weight words;
  :After first step, also try constituents then original puller;
  :Choose witness-ordered/top claim at each step;
  :Print complete chain answer or point where chain stopped;
 endswitch
endwhile (no)
:Print take count; cleanup; return 0;''','This wrapper reads and prints; it does not persist turn/session testimony. The curated-fact trust gate is not the same calculation as consensus confidence. Individual take branches, firmware validation, claims/holds queries and native semantics remain nested; empty answers can still return 0.')
    view('deploy','deploy.c','''
:database_made; connect; map tier0;
while (More declared extensions?) is (yes)
 :CREATE EXTENSION IF NOT EXISTS;
 if (run reports failure?) then (yes)
  :Return 1;
  stop
 endif
endwhile (no)
:ALTER EXTENSION laplace UPDATE;
:Set database tier0, flags and highway paths;
if (Any checked run fails?) then (yes)
 :Return 1;
 stop
endif
:Find existing public entity / physicality / witness / consensus tables;
:Attempt to adopt tables into extension;
:SELECT laplace_schema_indexes();
if (Index run fails?) then (yes)
 :Return 1;
 stop
endif
:Close and reconnect; enable_parallel_append = on for database;
:Close; return cmd_status result;''','database_made and run are nested helpers. Not every PQexec status is checked: table-adoption and parallel-append statements do not independently change the handler return. Earlier DDL can remain after later failure.')
    view('index','deploy.c','''
:Connect;
:SET maintenance_work_mem = 8GB;
if (run succeeds?) then (yes)
 :SELECT laplace_schema_indexes();
 if (run succeeds?) then (yes)
  :ANALYZE;
 endif
endif
:Close; return 0 only if all three checked operations succeeded;''','The operations short-circuit in order. The 8GB setting is a literal source value, not a resource-derived setting in this wrapper.')
    view('status','deploy.c','''
:Connect; query extension version, ISA, tier0 path/fingerprint,
database size and server version;
:Print available values with fallback labels;
if (Local tier0 and server fingerprint available?) then (yes)
 :Compare fingerprints; print same or DIFFERS;
endif
:Read and compare highway settings/fingerprints when available;
:Sample entity rows at 1 percent; estimate counts by tier;
:Read planner row estimates and relation sizes;
:Read witness / consensus estimates and index counts;
:Free results and close;
:Return 0;''','Fingerprint mismatch is printed but this handler still returns 0. Printed population counts include sampled and planner estimates; they are not exact admission or acceptance counts. Connection/helper failures remain separate.')
    view('sources','deploy.c','''
:Connect, map tier0, load recipes and source order;
:Attempt to read witness trunks;
:Collect first constituent of each witness trunk;
while (More configured sources?) is (yes)
 :Count recipes belonging to this source;
 :Decompose source_called name; compare against witness first constituents;
 :Print source order, recipe count, yes/no and discovered root;
 :Print declared after dependencies;
endwhile (no)
:Free map; close; return 0;''','The displayed yes/no comes from matching a witness trunk source-record constituent. It does not validate every artifact or whole-source admission contract. ask_try may produce no witness result.')
    view('pgn','chess.c','''
:Parse roots, connection, tier0, output and verify options;
if (No roots?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; load chess rules;
:Parse root IDs;
if (Invalid root ID?) then (yes)
 :Return 2;
 stop
endif
:Open read connection;
:db_fetch roots and descendants;
:Open output file or use stdout;
if (Output cannot be opened?) then (yes)
 :Return 1;
 stop
endif
while (More roots?) is (yes)
 :records_in; use root itself if present and no records found;
 while (More selected records?) is (yes)
  :record_pgn reconstructs text and accumulates replay statistics;
  :Write reconstructed bytes if nonempty;
 endwhile (no)
endwhile (no)
:Close output file if requested;
if (--verify?) then (yes)
 :verify_ids from constituents / leaves;
endif
:Print replay, Chess960, transposition and verification statistics;
:Close database;
:Return 1 for replay/ID/leaf/dangling/split failures, otherwise 0;''','PGN is reconstructed from database state. record_pgn, rules, db_fetch and verify_ids remain nested operations. Output is written before final verification; fwrite/fclose return values are not checked in this handler.')
    view('held','provenance.c','''
:Parse parts, --id, files, voices and TSV;
if (No parts?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; connect;
:Resolve explicit IDs or strands_named;
if (Malformed explicit ID?) then (yes)
 :Return 2;
 stop
endif
:Sort claims by ID;
:Open optional TSV;
if (TSV open fails?) then (yes)
 :Return 1;
 stop
endif
:walk_up from claims through containment;
:Identify parentless trunks and metadata-bearing file nodes;
:roots_known distinguishes witness trunks from other roots;
while (More trunks?) is (yes)
 :rows_under computes claim/voice tallies and optional TSV;
 if (Small claim set?) then (yes)
  :down_from and tally_under; print detailed claim and voice counts;
 endif
endwhile (no)
if (--files?) then (yes)
 :Compute and print per-file rows/tallies;
endif
:Print walk counts and games/tokens; close TSV and database;
:Free DAG, readers, holds and maps; return 0;''','Games and tokens remain separate tallies; a parentless trunk is not automatically a registered witness. Walker, record classification and tally semantics remain nested and inspectable in source.')
    view('structure','structure.c','''
:Parse layout, file and display-node limit;
if (Layout or file argument absent?) then (yes)
 :Return 2;
 stop
endif
:Open layout;
if (Open fails?) then (yes)
 :Return 1;
 stop
endif
:Read layout lines through layout_says;
if (Layout rejected?) then (yes)
 :Return 2;
 stop
endif
:Open data file;
if (Open fails?) then (yes)
 :Return 1;
 stop
endif
:Read first 1 MiB; trim full-buffer tail to a newline if available;
:s_head then s_decompose with show_unit and node limit;
:Report outermost parts within inspected bytes;
:Free buffers; return 0;''','This is a bounded structure preview of the first MiB, not complete file ingestion or byte-perfect source admission.')
    view('tree','recipe.c','''
:Parse recipe directory, optional grammar and node limit;
if (File argument absent?) then (yes)
 :Return 2;
 stop
endif
if (Explicit grammar?) then (yes)
 :grammar_load;
else (no)
 :Load recipes; find recipe_for file; select recipe grammar;
endif
if (No grammar?) then (yes)
 :Return 1;
 stop
endif
:read_all file;
if (Read fails?) then (yes)
 :Return 1;
 stop
endif
:Create tree-sitter parser; parse file bytes;
:tree_print under node budget; print truncation marker when exhausted;
:Delete cursor/tree/parser; free bytes; return 0;''','Parser diagnostics and grammar correctness are not converted into a separate failure result by this wrapper. This command prints syntax, not semantic testimony.')
    view('merge','db.c','''
:Parse -d and -j;
if (Unrecognized arguments?) then (yes)
 :Return 2;
 stop
endif
if (Thread count not positive?) then (yes)
 :Use omp_get_max_threads;
endif
:Print connection count;
:Return merge(conninfo, threads);''','The staging merge is a nested operation in db.c. This wrapper does not establish atomicity, rollback or per-partition failure behavior.')
    view('fills','fills.c','''
:Parse phrase, row limit, tier0 and connection;
if (No phrase?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; initialize working table/context;
:Compose exact phrase and constituent IDs;
:Choose composed lookup keys, falling back to all phrase parts;
:Fetch candidate containers;
:lp_path_follows checks exact run including separators;
while (Parent frontier nonempty?) is (yes)
 :Fetch parents in sets of up to 20000;
 :Record parent multiplicities; enqueue unseen parents;
endwhile (no)
:occ_of propagates root occurrences down through multiplicities;
:Weight each continuation by containing path occurrence count;
:Sort descending occurrence totals;
:Batch request top continuation texts; print at most limit;
:Report traversal/timings; free maps and close; return 0;''','This calculates occurrence-weighted continuations; repetition is structural occurrence, not independent testimony. Path matching and decoding remain native nested operations.')
