"""Independent Conventional preparation and training pipeline."""
def add_conventional_views(add):
    base='Laplace-Conventional/src/laplace_conventional/'
    def view(id,title,body,files,note):
        add('conventional-'+id,'Conventional — '+title,body,[base+'cli.py']+[f if f.startswith('Laplace-Conventional/') else base+f for f in files],note,[])
    view('configure-launch','configuration pipeline and retained partial state','''start
:Resolve corpus/state/project config; create state directory;
:Inventory physical files; select corpus and write decisions;
if (Full coverage required and unsupported or inaccessible selected bytes nonzero?) then (yes)
 :Exit before preparation; inventory and selection outputs remain;
 stop
endif
:Train/select tokenizer; prepare token shards; derive model config;
:Measure hardware once and write hardware receipt;
:Plan text execution with require-fit;
if (Text plan does not fit?) then (yes)
 :Fail after execution plan has been written;
 stop
endif
:Plan modalities with require-fit;
if (Any required modality does not fit?) then (yes)
 :Fail before writing new combined modality plan;
 stop
endif
:Print configuration complete;
stop''',['Laplace-Conventional/scripts/configure.sh'],'Complete configuration shell reviewed alongside the CLI subcommands. It executes stages sequentially with set -euo pipefail; there is no whole-pipeline transaction or cleanup of earlier/stale state. Selected-byte counts drive full-coverage gating.')
    view('setup','environment installation and host policy','''start
:Require Python command and version at least 3.10;
:Create venv; upgrade pip/setuptools/wheel;
if (Torch index unspecified?) then (yes)
 :Default CPU index; choose cu126 index when host policy allows;
endif
if (Offload requested or index differs from CPU URL?) then (yes)
 :Require GPU host policy;
endif
:Install configured Torch version from selected index;
:Install editable project with train/media/RL/test extras;
:Include offload extra when requested;
:Import Torch, ffmpeg provider and Pillow; assemble environment report;
if (Host permits GPU and CUDA available?) then (yes)
 :Inspect device zero name, memory and compute capability;
 if (Compute capability major below 6?) then (yes)
  :Exit error after installations;
  stop
 endif
endif
:Print environment report; import DeepSpeed when offload requested;
:Print environment ready;
stop''',['Laplace-Conventional/scripts/setup.sh','host_policy.py','Laplace-Conventional/pyproject.toml'],'Complete setup script reviewed without executing it. Package installation and venv changes precede several checks and are not rolled back on failure. A GPU-eligible host with unavailable CUDA can still print environment ready; the script reports availability rather than requiring it. Versions are source-pinned defaults, not independently verified package availability.')
    view('runner-install','runner registration, service and online verification','''start
:Read required runner/work/scratch paths and host-derived labels;
:Require root or reexec sudo; require commands and non-root operator;
:Create service group/user as needed; add operator to group;
:Require runner primary group matches;
:Require absolute paths without whitespace or percent; refuse final-path symlinks;
:Create setgid directories; export scratch environment;
:Require operator GitHub ADMIN permission;
if (Distribution absent or recorded hash differs?) then (yes)
 :Reuse verified archive or download pinned version; verify SHA256;
 :Extract as runner user; write distribution hash marker;
endif
if (Existing registration?) then (yes)
 :Require repository, runner name and work path match;
else (no)
 :Request GitHub registration token; configure runner as service user;
 :Unset token;
endif
:Install service if absent; require service User matches;
:Write systemd mount/group/umask/scratch/host override;
:Reload systemd; enable and restart service; require active;
while (Up to 20 online polls?) is (yes)
 :Query repository runners and find matching name;
 if (Reported online?) then (yes)
  :Print ONLINE; exit success;
  stop
 endif
 :Wait two seconds;
endwhile (exhausted)
:Report service started but online unconfirmed; exit failure;
stop''',['Laplace-Conventional/scripts/install-runner.sh'],'Complete installer source reviewed without running it or contacting GitHub for registration. Failure retains earlier user/group/files/registration/service changes. Path checks inspect the supplied final path and do not canonicalize symlink ancestors. A matching distribution marker does not rehash every extracted executable. GitHub online status is not a completed workflow acceptance test.')
    view('rl','external reward and GRPO entry boundary','''start
if (Invoked using documented python -m ...rl?) then (yes)
 :Module defines helpers and main; no main guard calls it;
 :End module without invoking training;
 stop
endif
:Explicit caller invokes main(); parse required model/dataset/reward/output;
:Import datasets and TRL; import reward module and resolve callable;
if (Reward callable and JSON/JSONL/NDJSON dataset has prompt column?) then (yes)
 :Create GRPOTrainer with supplied reward, model, dataset and max steps;
 :Use CPU unless host policy allows GPU;
 :Call trainer.train(); then save_model(output);
else (no)
 :Raise error; no trainer call;
endif
stop''',['rl.py','host_policy.py','Laplace-Conventional/README.md','Laplace-Conventional/pyproject.toml'],'Complete local RL wrapper reviewed. README documents python -m invocation, but this module has no __main__ guard and the only pyproject console entry is cli:main. The callable main body is shown separately from the documented inactive entry. Reward import can execute arbitrary module initialization. External reward semantics, GRPO internals and runtime success are not established.')
    view('evaluate','validation counting and inactive module entry','''start
if (Executed as python -m ...evaluate?) then (yes)
 :Imports run and main is defined; no main guard calls it;
 stop
endif
:Explicit caller invokes main(); parse arguments;
:Load pretrained causal LM; choose eligible CUDA or CPU; set eval mode;
:Read dataset dtype; make validation TokenShardDataset and batch-size-one loader;
:Initialize loss sum and token counter to zero; disable gradients;
while (Batch available and index below max batches?) is (yes)
 :Move tensors to device; evaluate model;
 :Count all label elements, including padded ignored labels;
 :Accumulate model loss times this count;
endwhile (done)
:NLL = sum / max(token count, 1);
:Print count, NLL and exp(min(NLL, 50));
note right
 Empty stream or nonpositive batch cap yields
 zero tokens, NLL zero, perplexity one.
 The count is not valid prediction tokens.
end note
stop''',['evaluate.py','train.py','host_policy.py'],'Complete evaluation wrapper reviewed, without running model inference. It uses label.numel() rather than the nonignored, shifted prediction count. Reported NLL is a weighted mean of provider batch losses using that count. There is no empty-validation rejection or __main__ entry guard.')
    view('models','language and modality model construction','''start
if (Language model?) then (yes)
 :Read generated model configuration;
 :Construct LlamaConfig: heads = key/value heads;
 :BOS 1 / EOS 2 / PAD 3; tie embeddings; disable cache;
 :Instantiate LlamaForCausalLM from config;
else (modality)
 if (Provider image-vit-mae?) then (yes)
  :Configure RGB ViTMAE encoder/decoder, patches, masking and pixel loss;
 elseif (Provider audio-wav2vec2?) then (yes)
  :Configure Wav2Vec2 transformer, masking, negatives and codebook;
 elseif (Provider video-videomae?) then (yes)
  :Configure RGB VideoMAE frames, tubelets, patches and encoder/decoder;
 else (unknown)
  :Raise unsupported provider;
  stop
 endif
 :Instantiate corresponding pretraining model;
endif
:When planning, count actual parameter elements;
:Estimate largest layer as min(total, ceil(total / max(1,layers) * 2));
stop''',['model.py','modality_models.py'],'All local model constructors and parameter_stats reviewed. These instantiate standard external transformer architectures from configuration; they do not load pretrained weights here. The largest-layer statistic is an estimate, not measured peak memory or inspection of the largest concrete layer. Provider internals remain external.')
    add('conventional-launch','Conventional — launch text, media and aggregate receipt','''start
:train-all resolves corpus, state, output and project config; creates output directory;
:Call train.sh for text;
:Require training/execution/dataset/tokenizer files;
:If absent, derive MICRO_BATCH_SIZE from execution plan;
:exec python -m train; forward extra text arguments;
if (Text exits successfully?) then (yes)
 :Call train-modalities.sh;
 :Require manifest and modality plan; read validation split from project config;
 :Enumerate enabled and present image/audio/video entries;
 while (Next modality?) is (yes)
  :If auto resume enabled and marker exists, require checkpoint directory;
  :Invoke python -m modality_train with modality, manifest, plan and optional resume;
  if (Training exits unsuccessfully?) then (yes)
   :Stop shell; prior outputs remain;
   stop
  endif
 endwhile (done)
 :Hash training/execution/modalities/hardware JSON;
 :Write run-receipt.json with those hashes and output paths;
else (no)
 :Stop shell; prior outputs remain;
endif
stop''',['Laplace-Conventional/scripts/train-all.sh','Laplace-Conventional/scripts/train.sh','Laplace-Conventional/scripts/train-modalities.sh'],'Complete three launch scripts reviewed. Aggregate receipt hashes planning files, not checkpoint contents, dataset bytes or verification results. train.sh assigns PROJECT_CONFIG but does not consume it. Text auto-resume is not supplied by train-all; media auto-resume uses a path from its marker. Bash process-substitution producer errors are not explicitly checked after mapfile. No training or launcher was executed.',[])
    view('runtime','instantiate the planned training provider','''start
if (Plan has GPU bytes or selects ZeRO-3?) then (yes)
 :Require LAPLACE_GPU_HOST matches this host;
endif
:Map fp32/fp16/bf16 to accelerator mixed-precision mode;
if (Native training backend?) then (yes)
 :Construct Accelerator with CPU flag inverse of host eligibility;
else (ZeRO-3)
 :Require Accelerate DeepSpeedPlugin and deepspeed imports;
 :Configure stage three, CPU parameter and optimizer offload,
initialization ownership, accumulation, clipping and model-save precision;
 :Construct Accelerator with plugin;
endif
:Return accelerator; unsupported backend raises;
stop''',['runtime.py','host_policy.py'],'Complete runtime adapter reviewed. This invokes external providers; construction is not independent verification of hardware fit or training completion. Main training paths separately validate the stored plan before this call.')
    view('media-decode','image frames, audio chunks and video clips','''start
:Select unique accessible provider-matching manifest entries;
:Split by SHA256 of path; assign whole entries to loader workers;
if (Image?) then (yes)
 :Open each image; iterate every image frame;
 :Convert RGB; bicubic pad to configured square;
 :Scale pixels and normalize channels; emit tensor;
elseif (Audio?) then (yes)
 :Run ffmpeg on first audio stream only;
 :Convert to mono, requested sample rate and float32 little-endian PCM;
 :Emit nonoverlapping chunks; pad final values with zero and validity mask;
 :Close pipes and wait; nonzero decoder exit raises after emitted chunks;
else (Video)
 :Decode sequential RGB frames;
 :Apply image normalization to each frame;
 :Emit nonoverlapping configured-size clips;
 :Repeat last frame to pad final short clip;
 :Close reader in finally;
endif
stop
legend bottom
Video audio extraction is a separate audio-provider path.
Audio probe recognizes explicit no-stream errors; other probe failures raise.
Decode failure is not silently reclassified as successful coverage.
Transforms are training representations, not exact source-byte preservation.
endlegend''',['modality_data.py','providers.py'],'Entire modality_data and provider registry modules reviewed. Only the first audio stream is selected; video timestamps/audio synchronization are not carried in emitted clip tensors. External decoder behavior and sampling correctness are not runtime-validated here.')
    view('media-train','media training, source selection and resume binding','''start
:Require selected modality enabled and present; validate execution plan;
:Construct accelerator; load manifest and path-based train/validation sets;
:For audio, probe configured video containers and record missing audio streams;
:Fingerprint modality plan and source summary; write source receipt;
if (No train entries?) then (yes)
 if (Audio, no validation entries and video-audio search enabled?) then (yes)
  :Write no_selected_audio_streams_present receipt; return;
 else (no)
  :Raise missing training files;
 endif
 stop
endif
:Construct provider model, datasets, loaders and AdamW;
:Require positive whole-number epochs;
if (Resume?) then (yes)
 :Require matching provider, plan and source-summary fingerprints;
 :Load accelerator state and progress; restore epoch/batch/example counters;
 :If recorded epoch already at target, return;
endif
while (Epochs remain?) is (yes)
 :Skip saved prefix batches in first resumed epoch;
 :For each batch build modality objective; forward, backward, clip and step;
 :At synchronized time threshold save state, progress and latest-checkpoint path;
 :Reject zero epoch batches;
 :If validation set exists, gather and average batch losses;
 :Report epoch; reset batch offset;
endwhile (done)
:Synchronize; save final state; export model;
:Write final progress and training receipt;
stop
legend bottom
Image objective uses model pixel reconstruction; audio creates feature masks
and sampled negatives; video randomly masks spatiotemporal positions.
The source fingerprint hashes source_receipt summaries, not the full manifest
or all selected content hashes. Equal summaries do not establish identical corpora.
endlegend''',['modality_train.py','modality_data.py'],'Entire modality_train module reviewed. Resume rejects changed summary fingerprints but does not prove exact source-byte identity. Final artifacts are separate writes. Provider model construction and library optimization internals remain nested; no training or decoder was run.')
    view('inventory','physical inventory before selection','''start
:Parse project settings and inventory command;
:Resolve root; require directory;
:Enumerate files in sorted relative-path order;
while (More files?) is (yes)
 :Read size, SHA256 and classification;
 :Catch stat/read OSError into inaccessible entry and error;
 :Record source from first relative-path segment;
 :CLI passes dedupe=false, preserving physical copies;
endwhile (no)
:Summarize files, bytes, kinds, sources and unsupported/inaccessible byte counts;
:Write manifest.jsonl then summary.json; print summary;
stop''',['corpus.py'],'build_manifest and writers reviewed. Enumeration/classification helper details remain nested. coverage_complete is based on unsupported/inaccessible byte sums; a zero-byte inaccessible entry can remain in the file count without changing that boolean. Output files are separate writes.',)
    view('select','selection, deduplication and provider coverage','''start
:Load physical manifest and ordered policy rules;
:Require each rule has glob; exclusions need explicit reason;
:For each path apply all matching rules; last match wins;
:Filter selected entries;
:Resolve duplicates by SHA256 only among selected entries;
:Resolve text or enabled media provider for each unique selected entry;
:When video audio is required, video coverage also needs enabled audio provider;
:Summarize selected, excluded, duplicates, providers and inaccessible/unsupported bytes;
:Write per-path decisions then selected manifest and summary;
:Print summary;
stop''',['selection.py'],'Complete selection module reviewed. Provider availability here is policy/manifest classification, not a decode or training acceptance run. Coverage uses byte totals and is not equivalent to successful processing of every selected file.')
    view('tokenizer','candidate tokenizer training and selection','''start
:Require nonempty configured vocabulary candidate list;
while (More vocabulary candidates?) is (yes)
 :Create SentencePiece unigram model from train-split nonblank records;
 :Use identity normalization, byte fallback and configured special-token IDs;
 :Allow actual vocabulary below requested size;
 :Encode validation records; count tokens and UTF8 bytes;
 :Score model_bytes times eight plus validation_tokens times log2(actual_vocab);
endwhile (no)
:Choose minimum description_bits candidate;
:Copy chosen model to tokenizer.model;
:Write tokenizer-report.json; print report;
stop''',['tokenizer.py','corpus.py'],'Tokenizer orchestration reviewed. Each candidate invokes the record factory again; split assignment is stable-key based. This is a Conventional learned tokenizer, separate from native canonical Unicode composition. External SentencePiece internals and evaluation adequacy remain separate.')
    view('prepare','token shards and dataset report','''start
:Load manifest and tokenizer;
:Choose uint16 or uint32 from vocabulary size;
:Derive tokens per shard from byte limit, minimum one;
:Create train and validation shard writers;
while (More accessible trainable nonduplicate records?) is (yes)
 :Normalize/chunk records according to file format;
 :Choose stable-key train/validation split;
 :Encode BOS + text tokens + EOS;
 :Write binary token slices across bounded shard files;
 :Update split counts; add train record length histogram;
endwhile (no)
:Close writers; write dataset-report.json with shard list and quantiles;
:Print report;
stop
legend bottom
JSON/JSONL are canonicalized; CSV/TSV rows become JSON; XML emits depth-two elements;
CoNLL-U groups blank-line-separated records; other text is chunked.
Line endings normalize and blank-only chunks are skipped.
endlegend''',['prepare.py','corpus.py'],'Preparation and record transformation bodies reviewed. This dataset representation is not an exact source-byte archival representation. Shards write directly and no rollback/finally closes the whole pipeline on an intermediate exception. Existing extra shards in an output directory are not removed by this body.')
    view('derive-config','derive model shape from measured token corpus','''start
:Load dataset report; require positive tokens_per_parameter;
:Set target parameters=max(vocabulary times 256, train_tokens / ratio);
:Enumerate hidden sizes in increments of 128 and even layers 4..128;
:Estimate parameters; choose minimum absolute log ratio to target;
:Round selected record-length quantile up to power of two, minimum 256;
:Apply training defaults and configured overrides;
:Reassert derived context; global token batch at least context;
:Derive checkpoint token interval from fraction and minimum;
:Write config with derivation, model and training sections; print;
stop''',['configure.py'],'Complete configuration module reviewed. Parameter sizing is an explicit formula/search estimate, not a quality or fit guarantee. Later execution planning chooses a provider without reducing this derived model scope.')
    view('hardware','host policy and hardware observation','''start
:Record platform, Python, logical CPUs and total RAM;
:Compare LAPLACE_GPU_HOST with local short hostname;
if (Host explicitly eligible?) then (yes)
 :Import torch; observe CUDA availability;
 if (CUDA available?) then (yes)
  :Record device zero name, total memory, capability and multiprocessors;
 endif
 :Catch probe exception into torch_probe_error;
else (no)
 :Return CPU/host policy report without CUDA probe;
endif
:Write hardware report and print;
stop''',['hardware.py','host_policy.py'],'Complete hardware and host-policy modules reviewed. This probe records total memory, not current free memory or a resource reservation. Explicit GPU host policy is separate from native Laplace CPU execution requirements.')
    view('plan-execution','model-preserving execution fit decision','''start
:Read model configuration, hardware and execution policy;
:Validate backend, precision, microbatch and memory fractions;
:Compute parameter-state estimate and activation reserve;
:Compute native GPU estimate and largest-layer ZeRO-3 GPU/CPU estimates;
:Compare estimates with configured GPU and RAM budgets;
if (Explicit backend policy?) then (yes)
 :Retain requested backend, even if its fit flag is false;
elseif (Native fits?) then (yes)
 :Select native training backend;
elseif (ZeRO-3 CPU offload fits?) then (yes)
 :Select offload backend;
else (none)
 :Select unavailable;
endif
:Write execution plan with scope_preserved=true; print;
if (CLI require-fit?) then (yes)
 :Reject unsupported/nonfitting backend or false scope_preserved;
endif
stop''',['execution.py'],'Complete execution planner reviewed. CLI writes the plan before optional fit rejection. Both modeled backends require detected GPU memory; native here means Conventional training backend, not the Laplace-Native semantic library. Estimates and stored fit flags are not live allocation proof.')
    view('plan-modalities','image, audio and video provider plans','''start
:Load selected manifest and hardware;
while (Image, audio, video settings remain?) is (yes)
 if (Modality disabled?) then (yes)
  :Record disabled with zero files/bytes;
 else (enabled)
  :Select unique manifest entries matching provider;
  :For audio with video-audio policy, also include selected video containers;
  if (No source entries?) then (yes)
   :Record present=false;
  else (present)
   :Instantiate configured provider model; count parameters and largest layer;
   :Delete model; collect memory;
   :Apply optional modality microbatch override; derive execution plan;
   :If require-fit, validate before adding this plan;
  endif
 endif
endwhile (done)
:Write combined modality plan; print;
stop''',['modality_plan.py'],'Complete planning module reviewed. Provider candidates are selected from metadata; video containers without audio are resolved later by training/data code. Model constructors, media decoding and training acceptance remain nested. Unlike plan-execution CLI, required fit is checked before writing the combined plan.')
    view('train','language-model training, checkpoints and export','''start
:Load config, execution plan and dataset report; validate fit;
:Require MICRO_BATCH_SIZE equals planned value;
:Derive gradient accumulation from target global tokens and world size;
:Select accelerator before building model; enable checkpointing;
:Create token-shard dataset, AdamW and warmup/cosine scheduler;
:Prepare model, optimizer, loader and scheduler with accelerator;
if (Resume supplied?) then (yes)
 :Load accelerator state; require progress.json;
 :Restore counters; skip previously consumed batches in current epoch;
endif
:Main process writes execution receipt;
while (Token target not reached?) is (yes)
 :Iterate batches; forward loss; backward; clip when gradients synchronize;
 :Step optimizer and scheduler; clear gradients;
 :Count valid labels times process count;
 :At synchronized checkpoint boundary save state and atomic progress file;
 :At target and synchronization break batch loop;
 if (No batches produced?) then (yes)
  :Raise runtime error;
  stop
 endif
 :If target remains, increment epoch and rebuild loader;
endwhile (target)
:Wait for processes; save final state;
:Main process writes final progress, exports model and tokenizer;
stop''',['train.py'],'Entire train module reviewed. Dataset assigns whole shards among workers, makes context+1 windows and pads the final remainder with ignored labels. Runtime provider, model architecture, optimizer/library internals, distributed counting accuracy and resumability acceptance remain separate. Nothing was trained or exported during this research.')
