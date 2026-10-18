"""Reviewed model admission and HTTP export boundaries, separate from target contracts."""

def add_model_views(add):
    root = 'Laplace/app/'
    decomposer = root+'Laplace.Decomposers/Model/ModelDecomposer.cs'
    checkpoint = root+'Laplace.Decomposers/Model/ModelCheckpoint.cs'
    circuit = root+'Laplace.Decomposers/Model/ModelTokenEdgeETL.cs'
    coordinates = root+'Laplace.Decomposers/Model/ModelCoordinates.cs'
    endpoint = root+'Laplace.Endpoints.OpenAICompat/EndpointMappings.Foundry.cs'
    service = root+'Laplace.Endpoints.OpenAICompat/FoundryExportService.cs'
    compile_service = root+'Laplace.Endpoints.OpenAICompat/RecipeCompileService.cs'

    add('monorepo-model-ingest', 'Monorepo — model source and decomposition orchestration', r'''start
:SourceForModel uses ModelContentSourceId\nor canonical-string hash fallback from derived directory name;
:Initialize registers ModelRuntimeManifest;
:Read config, tokenizer ID space and safetensors headers;
note right
Tokenizer ID-space errors log a warning.
Header exceptions become an empty header list.
Recognition / container parsing remain nested.
end note
:Recognize manifest; report ambiguity, unrecognized tensors and conflicts;
:ResolvePlanesMode accepts only structure\nany other configured mode throws;
if (Headers available?) then (yes)
 :Stage checkpoint header composition\nand six declared integer configuration facts;
 :Yield change to shared ingestion executor;
endif
if (Unsupported manifest or no tokenizer.json?) then (yes)
 :Log disposition and end iterator;
 stop
endif
:Parse tokenizer; run vocabulary and merges through common compose phases;
if (Token IDs dense?) then (yes)
 :Stage ordered vocabulary and yield change;
else (no)
 :Warn model-local ordinals are not retained;
endif
:Run ModelTokenEdgeETL.EmitAsync with commitEpoch = 1;
:Yield each circuit / evidence change\ncancellation checked between emitted changes;
stop''', [decomposer, circuit],
        'Complete ModelDecomposer and ModelTokenEdgeETL read. This iterator emits changes; it does not prove a whole-checkpoint transaction. Source identity fallback hashes a constructed name rather than the target source trunk. Unsupported/no-tokenizer branches may follow already-yielded header changes. The analyzer-mode branch in ModelDecomposer is unreachable under the current ResolvePlanesMode contract. DescribeInput reports safetensors only and estimated matchups; it is not a complete source artifact census. Token parser, manifest recognition, snapshot identity and common phase internals remain separate.',
        ['24.1','24.2','24.3','24.4','24.5','24.11'])

    add('monorepo-model-header', 'Monorepo — checkpoint header content construction', r'''start
:Take tensor descriptors in supplied order;
while (Tensor remains?) is (yes)
 :Split dotted tensor name into nonempty text segments\nreject an empty resulting path;
 :Reuse staged text leaves by exact string;
 :Stage dtype and dimension text\nreject negative dimensions; scalar shape uses text scalar;
 :Prepare ordered path and shape requests;
endwhile (no)
:Native StageBatch composes all paths and all shapes;
:Compose each tensor as [path, dtype, shape];
:Compose checkpoint from tensor results in supplied order;
note right
Singleton continuation preserves child atom metadata.
Payload values and data ranges are absent from this root.
It represents header structure, not payload equivalence.
end note
:Return checkpoint root; caller adds configuration testimony;
stop''', [checkpoint, coordinates, decomposer],
        'Complete ModelCheckpoint reviewed, including pure ComposeTensorHeader and singleton handling. No sort is added here; parser-supplied order is significant. Equal headers can produce the same header root despite different weight payloads. Native OrderedComposition and the independent source snapshot identity must not be conflated with this header root.',
        ['24.1','24.2','24.4'])

    add('monorepo-model-circuits', 'Monorepo — circuit decomposition and snapshot lifetime', r'''start
if (Runnable text recipe, classified embedding, positive dimensions?) then (no)
 :End without circuit changes;
 stop
endif
:Deduplicate in-range tokenizer entities\nretain first available canonical placement per entity;
if (At least two distinct entities?) then (no)
 stop
endif
:Open model content snapshot\nrequire snapshot source ID to equal admitted source;
:Own snapshot through reference-counted apply envelope\nattach VerifySourceId callback;
while (Relation family remains?) is (yes)
 while (Circuit descriptor remains?) is (yes)
  :Native contraction computes salience and ordering;
  :Check returned cardinality and index bounds;
  :Pack ordered entity IDs and salience\ncentroid uses first at most 64 available placement anchors;
  :StageCircuit identity from witness + model-circuit + plane\nplus layer / head path components when present;
  :Attach Projection physicality containing ordered token trajectory;
  :Yield circuit change with retained snapshot envelope;
  :Emit significant claim pages\neach also retains snapshot envelope;
 endwhile (no)
 :Update observed peak native resident bytes;
endwhile (no)
:Log forms, significant claims, skipped circuits\nand raw_weight_bytes_retained = 0;
:Dispose iterator's snapshot owner\nemitted envelopes retain their own lifetime;
stop
legend
IDENTITY DIFFERENCE: Wiki 24.6 identifies a circuit by its coupled content.
This source identifies it by witness and structural plane/layer/head address;
ordered coupled entities are stored in its Projection physicality.
Equal circuits in different source witnesses do not converge by this construction.
endlegend''', [circuit, coordinates],
        'Complete EmitAsync, BuildCircuitObservation and ModelCoordinates reviewed. Array cardinality and index ranges are checked; permutation uniqueness is not checked here. Native contraction, ModelCircuitEstate and apply-envelope implementation remain nested. Yielding a retained envelope is not proof that every writer calls its verifier. This source has no whole-checkpoint acceptance read. Logged raw retained bytes is a source assertion, not an independent storage measurement.',
        ['24.6','24.7','24.11'])

    add('monorepo-model-claims', 'Monorepo — significant model testimony and calculated scores', r'''start
:Resolve symmetry from relation registry\nuse symmetric pairs only when contraction has shared factors;
:Set page capacity to max(commit row sizing, vocabulary count minus one);
while (Unprocessed subject rows?) is (yes)
 :Check cancellation; call native SignificantPairs;
 if (NextRow advances?) then (no)
  :Throw InvalidDataException;
  stop
 endif
 if (Page contains pairs?) then (yes)
  :Create source-prior change with caller commit epoch;
  while (Pair remains in page?) is (yes)
   :Map returned row and column to canonical entities;
   :Stage Confirm attestation\nmodel source witness + circuit context + calculation qualifier;
   :Attach ephemeral fold input\nversioned calculation receipt + native fixed-point score;
  endwhile (no)
  :Yield change; caller retains source snapshot envelope;
 endif
 :Advance to NextRow;
endwhile (no)
stop
legend
Native significance contract names z >= sqrt(2 ln N).
This wrapper consumes native pages; it does not independently verify that threshold.
Low or negative scores do not become refutation in this wrapper.
Receipt/context IDs use formatted-string Hash128.OfCanonical calls.
endlegend''', [circuit],
        'Complete EmitSignificantClaims and context/calculation ID helpers reviewed. Native pair construction and numeric correctness remain nested. The managed layer trusts native column/score array shape and indices beyond normal array access. TestimonyWidthPerCircuit returns zero but does not disable this significance-emission path. Analyzer version is 10; no marker probe or marker write occurs in this EmitAsync body.',
        ['24.7','24.11'])

    add('monorepo-foundry-http', 'Monorepo — recipe compile and synthesis export HTTP effects', r'''start
:Parse JSON; require recipe or export recipe prefix;
:RequireQuoteAsync for recipe.compile or synthesis;
if (Quote gate allows?) then (no)
 :Return HTTP 402;
 stop
endif
if (Recipe compile?) then (yes)
 :RecipeDescriptor.Parse\nreturn descriptor identity and shape;
 note right
This compile service has no persistence call.
Parsing a recipe is not evidence of recipe deposit.
 end note
 if (ArgumentException or InvalidOperationException?) then (yes)
  :Return HTTP 400 invalid_recipe;
  stop
 endif
else (Export)
 :Call IFoundryExportService with recipe / prefix / tokenizer\nformat / filename / request cancellation;
 if (ArgumentException?) then (yes)
  :Return HTTP 400;
  stop
 elseif (InvalidOperationException?) then (yes)
  :Return HTTP 503;
  stop
 endif
endif
:After successful service call, consume quote and record billing;
:Return compile descriptor or export path / bytes / format;
stop''', [endpoint, compile_service, service],
        'Complete routes and both service wrappers reviewed. Exception mapping is not rollback. Quote admission/debit and later consumption/ledger are separate reviewed billing effects; consumption occurs after successful compile/export here. These route bodies have no explicit EXPORT-capability or source-scope check; upstream middleware and CLI/database enforcement need separate evidence. RecipeDescriptor.Parse internals remain nested.',
        ['25.1','25.2','25.6','17.4','17.7'])

    add('monorepo-foundry-process', 'Monorepo — export subprocess, file result and failure boundaries', r'''start
:Default format to gguf; reject every other format;
:Locate repo and existing CLI project;
:Create temporary laplace-export GUID directory;
if (Recipe prefix supplied?) then (yes)
 :Require existing tokenizer directory\npass recipe-from prefix and absolute tokenizer options;
else (JSON recipe)
 :Require recipe and write workDir/recipe.json;
endif
:Combine work directory with caller filename or default\nappend output path to argument list;
note right
This service does not validate output containment.
Rooted names or parent traversal can escape workDir.
No cleanup is implemented in this body.
end note
:Start dotnet run with CLI project and no-build\nCLI command: synthesize substrate\nUseShellExecute=false; arguments passed individually;
:Read stdout to end, then stderr to end\nwait for exit with request cancellation;
note right
Sequential redirected-pipe reads can stall if stderr fills.
Cancellation does not explicitly kill the subprocess.
Process disposal is not a declared effect rollback.
end note
if (Exit code nonzero?) then (yes)
 :Throw with stderr or stdout detail;
 stop
endif
if (Output path exists?) then (no)
 :Throw missing output error;
 stop
endif
:Read file length and return path / size / gguf;
stop
legend
Success here is exit zero plus file presence.
This wrapper does not load the file, run held-out semantics or verify provenance.
CLI synthesis and external-consumer validation remain separate boundaries.
endlegend''', [service],
        'Complete CliFoundryExportService reviewed. This is a source-observed export boundary, not an executed export. The generated output is returned as a filesystem path, not streamed as artifact bytes by this endpoint. No timeout, process-tree termination, result fingerprint, external load, ablation or temporary-directory cleanup occurs in this service. Lower CLI tensor construction was only partially inspected and is not claimed reviewed here.',
        ['25.2','25.5','25.6','25.7'])
