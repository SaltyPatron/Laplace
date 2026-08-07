# Backlog kill list — verified 2026-08-06

**Authority:** running code + live `laplace` DB on this host. Prior audits,
scratchpads, COMPLETION_PLAN, CHECKPOINT, W* docs, and issue bodies are
hypotheses that were falsified or confirmed here — not a shrink license.

**Live box at verify time:** `substrate_health` ok; ~33.1M entities; 28
ingest journal rows; OMW ~6.9M evidence_approx; ChessAnalysis top source.

**Invention rank (functionality importance):**
1 = identity / fold / ingest honesty / speaking correctness
2 = read ISA / election / generation wiring / consensus supply
3 = modality lanes that feed the fold
4 = perf / ops / CI protecting the above
5 = docs / debt / speculative design without live defect

**Census:** 240 open issues scored. Status: DUPLICATE=2, FIXED_UNCLOSED=25, NEEDS_SPEC=22, OBSOLETE=3, OPEN_DEFECT=185, UNVERIFIABLE=3
**Rank mix:** r1=46, r2=55, r3=47, r4=57, r5=35

**Process law for this list:** close/merge before inventing; no unsupervised
new tickets from audits; discoveries go into this list or a living plan section.
Execute Phase 0, then Rank 1→5. One issue (or one tightly coupled cluster) in flight.

### Rank-1 execution clusters (after Phase 0)

Work these clusters in order. Inside a cluster, PR boundaries may split issues;
do not skip to Rank 2 while Rank 1 OPEN_DEFECT identity/speaking items remain.

1. **Identity parity / law** — #904 (C#↔C physicality + CollapseIndex gates), #469 (Mask256 vs highway), #525 (highway blob CRC/fingerprint), #548 (XPOS namespace), #752 (tier-collision seam).
2. **Document/work identity epic** — #799 → #800 → #801 → #802 → #807 (and #754 document lane as the ingest consumer).
3. **Speaking correctness** — #900/#903 (`eff_mu` / realize_batch), #901 (English templates), #358/#359 (intent/topic — rewrite stale bodies first), #785 (held-out election; `pawn` still wrong live), #878 (tiered replace-or-fix), #658, #804, #360, #409, #379.
4. **Ingest honesty** — #898 (per-file resume), #417 (novelty→fold), #429, #487, #496, #520, #537.
5. **Chess fold/identity** — #835, #838, #840, #447, #511; design #491.
6. **Design decisions (blockers, not drive-by coding)** — #399, #436, #451, #506, #523, #529.

---

## Phase 0 — close / merge now (claims falsified or duplicate)

**LANDED 2026-08-06:** 30 issues closed with evidence comments (240→210 open). Section retained as the audit trail.

**Body hygiene (same day, completed):** Every open issue that scored THIN/STALE/EMPTY on
the 2026-08-06 verify pass has a rewritten GH body (status / verified evidence / hygiene
note / done-when / kill-list pointer). First wave: Rank-1 + Rank-2 read/election
(#358–#359, #399, #409, #429, #487, #491, #496, #785, #401, #458–#461, #464). Second
wave: remaining 57 including Uncracked-List #368–#374, model-lane #476–#485, foundry
#516/#519, ops/perf/debt thin set — **57/57 ok, 0 fail**. Unauthorized #904
implementation worktree was removed; no invention fixes in this pass.

Do these first on any future re-verify pass. They reduce the queue without invention risk.

### #481 — model-lane: ArchitectureProfile.For silently falls back to Llama on unknown model_type
- **disposition:** `DUPLICATE` of #383
- **rank/axis:** r3 / MODEL_EXPORT
- **labels now:** type:bug,priority:normal,model-lane,triage:deferred
- **evidence:** Same defect as #383's silent Llama fallback: ArchitectureProfile.cs:49 `_ => Llama`; #383 body already names this mis-map.
- **hygiene:** Close as duplicate of #383 (or keep as blocker subtask linked).
- **acceptance:** Same as #383 hard-fail on unknown model_type.

### #490 — chess: build-time / resident position→coord perfcache (retarget ECO-only blob)
- **disposition:** `DUPLICATE` of #822
- **rank/axis:** r4 / PERF
- **labels now:** area:app,perf,triage:deferred
- **evidence:** Body retargets under #818 and names detail issue #822. Code floor exists (chess_position_table.c, laplace_chess_position_ready in api) but live SELECT laplace_chess_position_ready() = false — blob not loaded. Work tracked on #822.
- **hygiene:** close as dup of #822; keep #822
- **acceptance:** See #822: resident position_id→coord perfcache ready() true and shape/neighbor paths use it.

### #366 — Tatoeba HAS_EXTERNAL_ID / repo decomposer path-id row smells (reported, unfixed)
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r5 / IDENTITY
- **labels now:** area:app,type:bug,priority:low,triage:active
- **evidence:** TatoebaGrammarWitness.cs links content roots via IS_TRANSLATION_OF; EtlManifest.cs notes HAS_EXTERNAL_ID dropped 2026-07-28; TatoebaDecomposerTests asserts no HAS_EXTERNAL_ID.
- **hygiene:** Close as fixed; drop 'unfixed' from title; note repo-lane follow-up separately if any.
- **acceptance:** Already met: no HAS_EXTERNAL_ID emission; translations on content roots.

### #430 — producer/queue/consumer file pool completion (bounded queue, one continuous consumer)
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / INGEST_SPINE
- **labels now:** ingest,perf,tracker-migration,triage:deferred
- **evidence:** IngestPipeline.RunMultiFileAsync: N file workers → bounded outCh → single-reader yield (IngestPipeline.cs:574-723); DecomposerMultiFile defaults to FileWorkers pool.
- **hygiene:** Close; body still says 'finish continuous-consumer' though shape is landed.
- **acceptance:** N-file source saturates FileWorkers with one apply consumer; telemetry shows parallel INGEST_FILE_* lines (already true on live UD run).

### #463 — read-side: v_word_points + word_anchor() + v_attestation_readable unbuilt (Rule #6 residual)
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r2 / READ_ISA
- **labels now:** read-side,triage:active
- **evidence:** Live DB: to_regclass v_word_points/v_attestation_readable/v_word_senses all present; word_anchor() in api() and pg_proc.
- **hygiene:** Close; body claims 0 hits are false against installed extension.
- **acceptance:** Views + word_anchor present in installed extension (already true).

### #466 — read-side: EvalCommands eval kernel + FoundryExport raw-plane SQL belong in the substrate
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r2 / READ_ISA
- **labels now:** read-side,foundry,triage:deferred
- **evidence:** EvalCommands uses NpgsqlIngestOps.GenerationProbeAsync (EvalCommands.cs:63); FoundryExport FillHilbertKeys/ReadTrajectoryLadder use NpgsqlFoundryReads helpers — cited line SQL is gone.
- **hygiene:** Close; re-audit if any remaining ad-hoc SQL in those files.
- **acceptance:** No hand-rolled plane/eval SQL in EvalCommands/FoundryExport; installed functions + regress (already largely true).

### #513 — chess: extend GameId to the full 7-tag PGN roster
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r1 / IDENTITY
- **labels now:** area:app,ingest,triage:active
- **evidence:** ChessVocabulary.PgnPlayingId(white,black,date,event,round,site,movetextId) at :159-162 closes over Event/Round/Site. Legacy GameId(white,black,date,moves) gone. GAP 6 claim falsified against running code.
- **hygiene:** close issue; body cites obsolete GameId signature
- **acceptance:** n/a — landed; close.

### #527 — ingest: build the M0 modality ladders — code(AST), audio(PCM), image(RGB), video
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r3 / MODALITY
- **labels now:** type:enhancement,ingest,triage:active
- **evidence:** Claim 'no decomposer exists for any of the four' falsified: CodeDecomposer/TinyCodes/Repo (AST/grammar), TrackAudioDecomposer (PCM), RgbaImageDecomposer (RGBA), FrameVideoDecomposer. All implement IIngestInventoryProvider. Body gate about reseed is ops, not absence of ladders.
- **hygiene:** close or retitle to residual M0 completeness/reseed
- **acceptance:** n/a for original absence claim — close; file residuals separately if ladder law gaps remain.

### #546 — chess: re-seed openings — LINE entities + clear pre-LINE / games=4 residue
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r3 / INGEST_SPINE
- **labels now:** type:bug,priority:low,ingest,triage:active
- **evidence:** Live: OPENING_NAME observation_count games4=0 games1=7466; chess_opening_record returns line-grain rows; 7466 OPENING_NAME subjects carry trajectories. ChessOpenings ingest ok 2026-08-05 (3733 units). Code: ChessOpeningsDecomposer OpeningGames=>1, LineId+AppendGameTrajectory.
- **hygiene:** Parent #818/#821; openings half proven on this host — closeable.
- **acceptance:** Met for openings: LINE grain + games=1; no games=4 residue.

### #547 — chess: game-tier mantissa-packed trajectory is not wired — EmitGame deposits a bare Document entity
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r3 / MODALITY
- **labels now:** type:bug,priority:normal,substrate-law,ingest,triage:active
- **evidence:** EmitGame still has no AddPhysicality, but ComposeGame(analyzeInline)/ChessAnalyze/ChessTrajectoryDecomposer call AppendGameTrajectory. Live: 250181 PLAYS_LINE object lines carry trajectories; ChessGameTrajectoryTests pins invertibility. Frechet has line trajectories to read.
- **hygiene:** Body cites EmitGame:281-300 bare deposit; architecture now deposits via analyze/trajectory lane.
- **acceptance:** Game/line entities carry invertible mantissa-packed trajectories — met live for PGN lines.

### #549 — ingest: PredicateMatrix source id is not namespaced — 'PredicateMatrixDecomposer' vs 'substrate/source/X/v1'
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r1 / IDENTITY
- **labels now:** type:bug,substrate-law,ingest,triage:active
- **evidence:** PredicateMatrixSource.SourceId = SubstrateCanonicalIds.Source("PredicateMatrixDecomposer") → substrate/source/.../v1. Live source_id hex d9b695… equals canonical_id('substrate/source/PredicateMatrixDecomposer/v1'), not bare. Display render shows short name because canonical_names row missing (WordNet has full key).
- **hygiene:** Identity keyspace fixed; optional follow-up: deposit canonical_names for PM source.
- **acceptance:** Source id in substrate/source/*/v1 space — met; reseed only if bare-id residue remains (none on this id).

### #550 — secrets: move the live Lichess token out of on-disk plaintext into the secrets flow
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / OPS_CI
- **labels now:** area:infra,type:bug,triage:active
- **evidence:** pipeline.sh phase_runtime_secrets writes /opt/laplace/secrets/lichess.env from GitHub LICHESS_API; laplace.yml passes secrets; sync-github-secrets.cmd documented. Live: /opt/laplace/secrets/lichess.env present (mode 640). Rotation of the token itself not verified.
- **hygiene:** Deploy path still plaintext-at-rest by design; ~/.config/shell/secrets.env also exists.
- **acceptance:** GitHub Secrets → publish → /opt/laplace/secrets path live; rotate token ops acknowledgment remaining.

### #575 — chess: unreachable from converse/chat/recall/MCP — FEN never resolves to a position entity
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r2 / READ_ISA
- **labels now:** area:app,type:enhancement,triage:active
- **evidence:** ChessPositionRef.RewriteFenToHex wired in MCP SubstrateTools (recall/walk/facts/…) and OpenAICompat SubstrateClient/Query/Explore. ChessPositionRefTests pin compose. FEN → composed position hex before lexical resolve.
- **hygiene:** Body predates ChessPositionRef; close after one live FEN→chess_moves smoke if desired.
- **acceptance:** FEN-shaped prompts resolve to position ids on MCP/chat/recall surfaces — code path landed.

### #594 — RepoDecomposer has no nested-repo boundary detection — a directory with multiple .git roots flattens into one repo identity
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r3 / INGEST_SPINE
- **labels now:** area:app,type:bug,ingest,triage:active
- **evidence:** RepoDecomposer.OnInitializedAsync calls ThrowIfNestedRepos; walks nested .git and throws InvalidOperationException listing nested roots (option b from issue).
- **hygiene:** Body claims no check; code now fails loud.
- **acceptance:** Nested multi-.git paths fail loudly or fan per-root — met (fail loud).

### #604 — chess: match-orchestration terminal surface — close the cutechess→substrate loop, dashboard over the existing lab service
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / OPS_CI
- **labels now:** triage:active
- **evidence:** ChessMatchDashboard: CutechessRunner + ChessLabService + AnsiConsole.Live; comments document re-ingest via ChessLabRunners→ChessPgnIngestor. ChessEngineService.StartPlaySession takes tenantId/userId (default public). Spectre command present in ChessCommands.
- **hygiene:** Remaining polish: stub→real auth ids; confirm cutechess job ingest flag in one live run.
- **acceptance:** Terminal dashboard drives lab; games re-ingest; identity fields threaded — largely met.

### #606 — chess: analytical dashboard — promote ply-grain clock/time-pressure data to a queryable typed signal (research pass needed)
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r3 / READ_ISA
- **labels now:** triage:active
- **evidence:** chess_time_pressure_outcome() installed and live: rushed/normal/deep with position/play counts and avg_eff_mu. chess_game_plies exposes HAS_CLOCK. Typed signal queryable today.
- **hygiene:** Body still says research-pass/blocked on #600; surface exists — narrow remaining scope or close.
- **acceptance:** Queryable typed time-pressure signal — met via chess_time_pressure_outcome.

### #626 — P2 cleanup: dead C# APIs + applied codemods + orphan scripts
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r5 / DEBT
- **labels now:** area:app,tech-debt,triage:active
- **evidence:** AttestDeprel/AttestFeature/NotImplemented/stream_e_pending gone from tree. migrate-decomposer-attest.py / migrate-seed-type-paths.py / llama_behavioral.sh / foundry-probe.py / decode-probe.py / probe-ffn-concepts.py / laplace-bench.sql / forward-pass.sql gone. Remain: scripts/book-receipts.sql, wordnet-receipts.sql.
- **hygiene:** Close after deleting two leftover receipt SQL files or move to adhoc/.
- **acceptance:** Dead APIs/codemods/orphan probes removed — essentially met.

### #659 — generation/corpus: retire trajectory_pairs, bound corpus_ensure, stream_stats perf
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r5 / DEBT
- **labels now:** triage:active
- **evidence:** ARCHITECTURE.md + drop_retired_content_lane.sql.in retired trajectory_pairs/_ensure/stream_stats. Live to_regprocedure all NULL for corpus_ensure, stream_stats, trajectory_pairs_ensure. Items 2–3 moot after drop.
- **hygiene:** Closeable; body still lists retired objects as present.
- **acceptance:** trajectory_pairs gone; corpus/stream_stats bounded or retired — met by retirement.

### #751 — generation: wire the steered lane into chat — walk shape runs the unsteered walker while the steered loop sits uncalled
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r1 / SPEAKING
- **labels now:** type:bug,priority:high,read-side,triage:completion-axis
- **evidence:** chat.sql.in walk branch calls converse_compose(..., lang, session-derived seed, session_trajectory ctx) with converse_walk fallback. Comment cites #751/PR #884. converse_tiered still deliberately unwired (content regression #878).
- **hygiene:** Main wiring claim fixed; S7 frontier static / kappa items may remain as follow-ups.
- **acceptance:** chat(walk) calls steered compose lane with lang — met.

### #759 — voice: discourse readback — orientation reads one topic id while the witnessed turn history goes unread
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r1 / SPEAKING
- **labels now:** type:enhancement,read-side,triage:completion-axis
- **evidence:** session_trajectory.sql.in + chat.sql.in orient/walk use session_trajectory (recency/frequency biography), not only session_last_resolved. Walk compose gets ctx_arr from session_trajectory.
- **hygiene:** Body claims unread history; code now consults trajectory.
- **acceptance:** Orientation consults session witnessed turns — met via session_trajectory.

### #777 — ops: foundation seed owed — empty box here (THIN_SUBSTRATE); prove #776 on live ladder after seed
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / OPS_CI
- **labels now:** priority:high,triage:ops-blocked
- **evidence:** Live: substrate_health ok; ~33.1M entities / ~79.6M attestations. UnicodeDecomposer status=ok (2026-08-05); WordNet/OMW/etc ok. Orphan running Unicode rows cleared to cancelled with error notes. #776 writer dedup on main assumed by successful foundation.
- **hygiene:** THIN_SUBSTRATE claim obsolete on this host; close after CHECKPOINT note update.
- **acceptance:** Foundation seed completes; no lying running journal; no 23505 replay — met.

### #792 — ops: empty / thin substrate must fail loud — smoke gate, not silent skip
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / OPS_CI
- **labels now:** area:ci,priority:high,triage:active,triage:ops-blocked
- **evidence:** scripts/check-substrate-floor.sh emits THIN_SUBSTRATE / INGEST_JOURNAL_NONTERMINAL; wired in laplace.yml (publish + post-deploy). ensure-foundation.sh --check-only. Would currently fail this host (UD running) — correct loud fail.
- **hygiene:** Closeable; confirm CHECKPOINT docs mention the gate.
- **acceptance:** Named smoke fails on thin/nonterminal journal — met in CI scripts.

### #812 — surface parity: one 'op' invoker — every installed operation reachable from MCP and the OpenAI endpoint, gated
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / OPS_CI
- **labels now:** triage:active
- **evidence:** MCP tool 'op' in SubstrateTools.cs; InstalledOpInvoker shared; OpenAICompat POST /v1/op (EndpointMappings.Op.cs). Live compositional_tier_distribution() callable via catalog. substrate_counts() now ESTIMATE (~2ms) — health inventory no longer 57014-class on this box.
- **hygiene:** Close when acceptance checklist confirmed on both deployed surfaces; issue still open on GH.
- **acceptance:** op(name) against api() catalog; bad names rejected; both MCP+OpenAI; health inside timeout — code+live counts satisfy.

### #867 — OMW discards language provenance: cross-lingual edges land on IS_SYNONYM_OF with context_id NULL
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r1 / IDENTITY
- **labels now:** (unlabeled)
- **evidence:** OMWGrammarWitness attests IS_SYNONYM_OF with contextId=langId. Live: attestations from OMWDecomposer IS_SYNONYM_OF null_ctx=0, nonnull_ctx=2,617,335.
- **hygiene:** Close issue; seed already carries fix.
- **acceptance:** IS_SYNONYM_OF carries language context — met live.

### #868 — Untiered KNN over v_word_points: the unit shell is a tier-0 monopoly
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r2 / READ_ISA
- **labels now:** (unlabeled)
- **evidence:** laplace_nearest_entity gained p_tiers smallint[] (2026-08-05 comment GH #868) with tier predicate. Default NULL preserves old untiered behaviour for callers that omit scope — capability fixed; caller migration may remain.
- **hygiene:** Close after hot callers pass tier scopes; default-NULL is intentional compat.
- **acceptance:** Tier scope from entities.tier available; shell monopoly avoidable — API met.

### #871 — consensus_step_edge uses the both-directions OR predicate spec 37 blames for the 280s hang
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / PERF
- **labels now:** (unlabeled)
- **evidence:** consensus_step_edge.sql.in is two-arm UNION ALL with per-arm ORDER BY eff_mu LIMIT 1; header cites GH #871. No OR predicate remains in that function. (Surviving OR joins elsewhere are #908.)
- **hygiene:** Close; do not conflate with #908 survivors.
- **acceptance:** No both-directions OR in consensus_step_edge — met.

### #880 — CI: ctest job fails on docs-only pushes — checkout wipes build/, skipped build job never recreates it
- **disposition:** `FIXED_UNCLOSED`
- **rank/axis:** r4 / OPS_CI
- **labels now:** (unlabeled)
- **evidence:** laplace.yml: concurrency group laplace-shared-workspace cancel-in-progress:false; unit-test needs: build; explicit build/ absence tripwire citing #880. Addresses measured wipe race.
- **hygiene:** Close after one docs-only / skip-build path observed green under new concurrency.
- **acceptance:** ctest never runs without build tree from same run's build job — wiring met.

### #378 — Verify: UD per-token MISC Lang= HAS_LANGUAGE emission — intentional code-switch signal or residual tier violation
- **disposition:** `OBSOLETE`
- **rank/axis:** r5 / IDENTITY
- **labels now:** area:app,type:bug,priority:low,spike,triage:research
- **evidence:** UdSentenceEmitter.cs:47-49,102-104,161-165 documents intentional per-token MISC Lang= code-switch; sentence-tier HAS_LANGUAGE for corpus language already landed.
- **hygiene:** Close as not-a-bug; optional note linking UD MISC Lang= spec.
- **acceptance:** Already decided in code: keep MISC Lang= per-token; sentence language at root.

### #431 — physicality GIST COPY 4x slow — cycle/defer geometry index during COPY
- **disposition:** `OBSOLETE`
- **rank/axis:** r4 / PERF
- **labels now:** ingest,perf,tracker-migration,triage:deferred
- **evidence:** NpgsqlIndexCycle.Enabled default OFF for partitioned schema (NpgsqlIndexCycle.cs:41-51); physicalities_*_coord_idx GiST live per hilbert partition — class note says partition-local indexes make global cycle not worth it.
- **hygiene:** Close as superseded by partitioned physicalities + cycle-off default; open new issue only if measured GiST COPY regression returns.
- **acceptance:** N/A — close with cite to partitioned index law; or new measured defect if COPY rate regresses.

### #503 — perf: perfcache skips BLAKE3 re-CRC on per-backend remap (Windows EXEC_BACKEND re-maps the 85MB t0 blob per connection)
- **disposition:** `OBSOLETE`
- **rank/axis:** r4 / PERF
- **labels now:** perf,triage:deferred
- **evidence:** codepoint_table_load_perfcache always BLAKE3-CRCs body before publish (codepoint_table.c:135-140). shared_preload prewarm documents fork CoW inherit (perfcache.c:208-214; perfcache_native.h:30-33). Stated 'skips re-CRC' defect falsified. Residual Windows remap cost is a different issue if still wanted.
- **hygiene:** close or retitle to Windows remap cost if still desired
- **acceptance:** n/a — claim false; optional new issue for EXEC_BACKEND remap amortization without skipping CRC.

---

## Remaining work — every non-closed issue

Ordered by invention rank, then axis, then number.

## Rank 1

### #399 — highway_mask grain: relation-TYPE bits vs value-grained (reseed-class decision)
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** THIN
- **labels now:** substrate-law,design-decision,tracker-migration,triage:decision
- **verified:** relation_types.toml still assigns one bit per relation type with family_root collapse; no accepted decision record choosing value-grained masks.
- **hygiene owed:** Needs ADR accept/reject; triage:decision stays until operator signs grain.
- **done when:** Accepted ADR stating type-grain forever OR value-grain migration plan with reseed cost; mask-consuming features cite it.

### #436 — governance: adversarial analysis of prompt-injection-as-attestation before the write path opens beyond the operator
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** STRONG
- **labels now:** substrate-law,read-side,design-decision,triage:decision
- **verified:** No docs/decisions adversarial-witness ADR found; issue correctly marks design-only — write lanes (MCP ingest/feedback) exist without recorded threat model.
- **hygiene owed:** Keep triage:decision; deliverable path docs/decisions/NNNN-…
- **done when:** Accepted decisions doc + quantified trust-class mass model; hardening issues filed from it.

### #451 — substrate: witness-trajectory evidence virtualization (O(witnesses) rows -> O(facts) rows + testimony vertices)
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** STRONG
- **labels now:** module:laplace_substrate,substrate-law,design-decision,triage:decision
- **verified:** EVIDENCE still per-(fact,source,context,outcome) row shape in live schema/architecture; no signed schema-law amendment implementing witness trajectories.
- **hygiene owed:** Keep design-decision; require operator sign-off before any DDL work.
- **done when:** Operator-signed schema law + migration/reseed plan; O(facts) evidence storage proven on a pilot relation.

### #523 — substrate-law: XPOS→UPOS is recorded as IS_A, but doc 18 calls that a recording defect (CORRESPONDS_TO)
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** STRONG
- **labels now:** substrate-law,design-decision,triage:decision
- **verified:** UdSentenceEmitter.cs:80-86 still emits xposId IS_A uposId. Specs 16 vs 18 contradict; code shipped IS_A. Reseed-class; needs operator ruling before change.
- **hygiene owed:** decision label correct
- **done when:** Operator ruling recorded; code+docs agree on IS_A vs CORRESPONDS_TO; reseed if relation changes.

### #529 — read-side: enforce that highway_mask/perfcache are accelerators only — no path may treat a mask miss as absence
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** STRONG
- **labels now:** substrate-law,read-side,design-decision,triage:decision
- **verified:** generate_walk.c:222-226 fails open on NULL/malformed masks. But consensus_layer_plane_masked.sql.in:10-11 filters `highway_mask IS NOT NULL` (mask miss = excluded). #413 CLOSED; masks populated live. Accelerator-only law not uniformly enforced; needs ruling + audit of all gate sites.
- **hygiene owed:** update for #413 closed + fail-open in generate_walk
- **done when:** Operator ruling + gate: every read path fails open on mask miss; masked SQL filters fixed or justified as non-authoritative.

### #417 — refold-on-reingest: novelty gate must reach the consensus fold
- **status:** `OPEN_DEFECT` · **axis:** FOLD · **body:** ADEQUATE
- **labels now:** ingest,perf,tracker-migration,triage:active
- **verified:** ConsensusAccumulatingWriter still folds every applied delta (inline consensus_upsert); attestation presence merges observation counts — no skip of already-folded observations on rows_new=0 re-ingest.
- **hygiene owed:** Add measured re-ingest wall time / rel/s acceptance numbers against a named source.
- **done when:** Idempotent re-ingest with rows_new=0 does not re-fold prior observations (witness_count stable; fold work ≈0).

### #447 — chess consensus modeling: self-play trust + move-quality-as-outcome + opening-book drawish poisoning (C07/C08/C09)
- **status:** `OPEN_DEFECT` · **axis:** FOLD · **body:** ADEQUATE
- **labels now:** area:app,type:bug,triage:active
- **verified:** C07/C08 mostly patched (EmitPlayer Response in SubstrateTurnHost.cs:82; MOVE_QUALITY not Outcome in ChessReviewIngest.cs:53-59; openings games=1 in ChessOpeningsDecomposer.cs:29-33) but CheckmateGames=3 still decisive-overweights self-play (SubstrateTurnHost.cs:70).
- **hygiene owed:** Split closed C07/C08/openings-C09 from residual self-play games-multiplier; update labels.
- **done when:** Self-play player trust source-conditional; MOVE_QUALITY never shares Outcome cells; opening/self-play masses do not systematically drawish or overweight decisive games without documented law.

### #511 — chess consensus: opponent-aware Glicko weighting (beating 2800 should move more than beating 1200)
- **status:** `OPEN_DEFECT` · **axis:** FOLD · **body:** ADEQUATE
- **labels now:** area:app,priority:low,substrate-law,triage:active
- **verified:** ChessGraph sets ScoreFp1e9 outcomes and PLAYED_BY edges but zero OpponentRdFp1e9 usage under app/Laplace.Chess. Fold weight for chess results remains opponent-RD-blind at deposit.
- **hygiene owed:** ok
- **done when:** Chess outcome deposits populate OpponentRdFp1e9 from opponent rating state; fold moves more against stronger/lower-RD opposition; tested.

### #838 — EPIC: chess write-volume — 92% of MOVE cells are single-witness; the reseed batch that fixes it
- **status:** `OPEN_DEFECT` · **axis:** FOLD · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** pg_stats consensus_r_move_h0 witness_count most_common_freq for 1 = 0.9418 (~94% single-witness) — property still live. ChessModality.Apply still clones board + CanonicalKey string. Reseed batch not landed.
- **hygiene owed:** Parent for #839/#840 emission changes.
- **done when:** Reseed changes identity/emission so MOVE cells are multi-witness conditioned; stats leave ~92% singleton regime.

### #840 — chess: Explore ranks Na3 above e4 — the fold is correct, the move cell is unconditioned on who played it
- **status:** `OPEN_DEFECT` · **axis:** FOLD · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** chess_moves orders by eff_mu only; MOVE cells have no rating/player/context conditioning in SQL. ExploreAsync optional player scope exists but default explore is unconditioned fold. HTTP /chess/explore timed out this pass; mechanism in code matches issue diagnosis. Belongs in #838 reseed.
- **hygiene owed:** chess-lab.md cites Na3>e4 as edge-mode poison; same cell design.
- **done when:** Condition move cell on rating gap / context; UI shows witnesses/rd; panel semantics honest.

### #469 — substrate-law: two independent 256-bit mask algebras — Mask256.cs vs highway_table.c
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** ADEQUATE
- **labels now:** area:app,substrate-law,triage:active
- **verified:** Mask256.cs implements |/&/Test/Set in C#; NativeInterop also binds highway_table_mask_* — two algebras; ADR 0001 accepted for bit registry but does not unify C#/C ops.
- **hygiene owed:** Cite ADR 0001 ACCEPTED; scope = algebra ownership not bit assignment.
- **done when:** Single source of truth for mask ops (C# thin P/Invoke or shared header); gate proves parity.

### #491 — chess: unify Zobrist and PositionContent.Surface position identity (C13)
- **status:** `NEEDS_SPEC` · **axis:** IDENTITY · **body:** THIN
- **labels now:** area:app,design-decision,triage:decision
- **verified:** Two identities still coexist: Zobrist.Hash for TT/search (Zobrist.cs; Search.cs:166) vs PositionContent.Surface → content-hash PositionId for substrate (PositionContent.cs; ChessCompose.cs:199-243). Content-addressing law wants one id per content; Zobrist is 64-bit TT key not Hash128. Operator ruling needed: keep Zobrist as non-identity accelerator vs force substrate id everywhere.
- **hygiene owed:** design-decision label correct; body too thin for C13 detail
- **done when:** Written ruling: either (a) Zobrist is explicitly non-identity cache key only, or (b) one shared content id path; code matches ruling with gate test.

### #506 — substrate-law: audit the whole SQL/native surface for Rule #1 OR-combined order-sensitive/insensitive metrics
- **status:** `NEEDS_SPEC` · **axis:** IDENTITY · **body:** ADEQUATE
- **labels now:** area:extension,substrate-law,triage:active
- **verified:** Audit campaign per docs/specs/06 Rule #1. Spot presence of hilbert/id probes (e.g. physicalities_present_ordinals) does not finish the surface. No exhaustive gate exists; cannot declare fixed without the audit artifact.
- **hygiene owed:** needs checklist deliverable
- **done when:** Published surface inventory of OR-combined metrics with pass/fail per site; defects filed or cleared.

### #525 — substrate: highway perfcache blob has no BLAKE3 CRC and no input-fingerprint staleness key (violates spec 33 law 3)
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** area:engine,type:bug,priority:normal,substrate-law,triage:active
- **verified:** highway_table_load validates magic/version/counts/offsets only (highway_table.c:109-125); no body CRC, no input fingerprint — unlike codepoint_table.c:135-140. Spec 33 law 3 unmet for highway blob.
- **hygiene owed:** distinct from #503 correctly
- **done when:** Highway blob header carries BLAKE3 body CRC + input fingerprint; loader verifies before use; stale blob fails closed.

### #548 — ingest: XPOS tags are minted unnamespaced (bare NodeHash) while UPOS goes through the governed resolver
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** type:bug,priority:normal,substrate-law,ingest,triage:active
- **verified:** UdSentenceEmitter.cs:76 still HighwayNodeEmitter.Emit(tok.Xpos); PosReference.PosTagset has Upos/WordNet/Wiktionary/FrameNet only — no Xpos. UPOS still PosReference.Attest.
- **hygiene owed:** Reseed-class; related #413/#523/#529.
- **done when:** XPOS through PosTagset-qualified ResolvePos; no bare NodeHash collisions across schemes.

### #752 — substrate: tier-collision seam — a surface's sense set unions its tier-0 Codepoint lineage with its word senses
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** type:bug,priority:normal,substrate-law,triage:completion-axis
- **verified:** Live: word_id('a') has entities at tier 0 and tier 2. prompt_coherence('What is a glacier?') still emits LATIN SMALL LETTER A rows. Root election now prefers glacier (#785 fixed) but seam remains.
- **hygiene owed:** completion-axis; ingest-boundary vs type=POS decision still open.
- **done when:** Single-char surfaces do not union codepoint senses into word election; OP3 gate clean.

### #799 — EPIC: a book, a document, a text file and its text are four entities, not one
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** DocumentDecomposer still conflates file content root as document SourceId; no work Merkle; HAS_TITLE/EXPRESSES absent from highway (relation_highway_bit null). FileEntity content⊕metadata law intact.
- **hygiene owed:** Parent of #800/#754; do not hang titles on files.
- **done when:** work_id Merkle(title,author); EXPRESSES/HAS_TITLE; editions converge.

### #800 — vocabulary: append EXPRESSES and HAS_TITLE bits — and reuse AUTHORED_BY/HAS_LICENSE/IS_TRANSLATION_OF rather than minting parallels
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** relation_types.toml: HAS_TITLE/EXPRESSES absent (only HAS_TITLECASE_MAPPING). Live relation_highway_bit(HAS_TITLE)=NULL, EXPRESSES=NULL; HAS_ECO/OPENING_NAME resolve. Next free bit claim needs re-check at append time.
- **hygiene owed:** Parent #799; append-only ADR 0001.
- **done when:** relation_type_id resolves with highway bits; codegen deterministic; no parallel author/license bits.

### #801 — work node: work_id = Merkle(title, author), EXPRESSES from file — identity from what names a work, not from its bytes
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** Manifest has AUTHORED_BY and CONTAINS but no EXPRESSES/HAS_TITLE (only HAS_TITLECASE_MAPPING). No WorkId/work_id mint in app/; FileEntity comment anticipates format-native metadata but does not mint a work Merkle. api('work'|'express') empty. Acceptance tests absent.
- **hygiene owed:** Parent #799 epic; depends #800 vocab. Supercedes title-on-file part of #754.
- **done when:** Two files same normalized (title,author) different bytes → one work_id + two file entities + EXPRESSES edges; (title,∅) defined; normalization documented at mint site.

### #802 — document layer: a document is a composition over content, not the file's content root
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** DocumentFileExtract.OpenAsync still yields ContentIngestRecord with SourceId=ContentTierSpine.ResolveRoot(bytes) — file content root doubles as document (DocumentDecomposer.cs ~104-118). No composition document node distinct from file source_id.
- **hygiene owed:** Interacts #418 grammar route and #660 modality_counts documents.
- **done when:** Multi-essay file → multiple document entities under one file; 1:1 case document id ≠ file source_id; re-ingest no-op.

### #807 — acceptance: prove editions collide, formats converge, and titles render — as tests, not as claims
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** Depends #801/#806 which are absent; no CI tests for work collision / format-crossing / containers_of titles found under app tests for WorkId/EXPRESSES.
- **hygiene owed:** Epic acceptance; not independently shippable before parents.
- **done when:** CI tests for items 1–7 (and 8 post-#803); no PR-description-only claims.

### #835 — chess: ChessPgn corpus is Live-Chess volume + name-splinter identity (partial cancelled seed)
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** Live source_status ChessPgn evidence_approx~4.8M last_run_status=cancelled — partial seed confirmed. chess_opening_record leaders are English Opening / Slav style online volume fingerprint. Name-splinter identity defect not re-proven via UI this pass but corpus shape claim holds.
- **hygiene owed:** SoR quality for STEER measurement; related #447 poison class.
- **done when:** Classical roster identity + finished ChessPgn seed or explicit scoped corpus declaration for STEER metrics.

### #904 — Identity pre-image layouts duplicated across C# and C with no parity gate (PhysicalityId, TierTree.Compose)
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** PhysicalityId.cs:19-25 vs content_witness_batch.c; PhysicalityIdRegressionTests same-side only; no cross-language vector pin. Open-coded claim stands for CollapseIndex prose-only lockstep.
- **hygiene owed:** Body overstates "no pin": OutcomeEncodingPinTests pins Content=1; still missing byte-identical C#↔C PhysicalityId + CollapseIndex parity gates.
- **done when:** Parity gate pins C# vs C preimages; little-endian coincidence not the only bond.

### #429 — client-dedup + ON CONFLICT offload — drop the DB existence probe
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** THIN
- **labels now:** ingest,perf,tracker-migration,triage:deferred
- **verified:** NpgsqlWorkingSetApply still probes (attestations_exist_bitmap / tier_batch_existence_probe) then pure COPY with explicit 'no ON CONFLICT' (NpgsqlWorkingSetApply.cs:15-17,191).
- **hygiene owed:** Reconcile issue prescription with current pure-COPY law — either change law or retitle to probe-cost reduction under probe+COPY.
- **done when:** Hot-path existence probe removed or proven unnecessary; bulk apply correct under concurrent overlap without the 12M-id probe cost.

### #487 — model-lane: analyzer marker grain — a mid-pass crash re-folds the completed portion on restart
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** THIN
- **labels now:** ingest,model-lane,triage:deferred
- **verified:** ChessAnalyze stamps AnalysisMarkerId/ANALYZED_AT in the same SubstrateChange as derived edges (ChessAnalyze.cs:50,111-113); ChessWitnessHydrator skips on marker presence. Marker is apply-atomic with its batch, not per-row inside a flush — crash after partial COPY of a working set still re-witnesses those rows (testimony doubles). Same class as open #417. Live markers exist; grain does not close mid-batch testimony double-count.
- **hygiene owed:** title says model-lane but body/scratchpad are chess-analyzer; labels include model-lane wrongly
- **done when:** Restart after mid-analyzer crash leaves consensus observation counts unchanged for already-applied units (marker/novelty reaches the fold before or with testimony).

### #496 — ingest: UDDecomposer emits self-referential HAS_DEFINITION rows (dog -> dog)
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** THIN
- **labels now:** type:bug,ingest,triage:active
- **verified:** Live: 436 HAS_DEFINITION rows with subject_id=object_id, all source_id=source_id('UDDecomposer'). UdSentenceEmitter.cs:147-152 emits form HAS_DEFINITION RootFor(gloss) — when gloss roots to the form, self-loop. Samples realize as numeric/'//' tokens, not only 'dog', but defect class confirmed.
- **hygiene owed:** title example outdated; quantify self-ref guard
- **done when:** UD ingest emits zero subject=object HAS_DEFINITION; existing 436 cleaned or reseeded; gate test on Gloss misc.

### #520 — ingest: WordNet/ConceptNet silently drop synset anchors when the CILI map is missing
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** STRONG
- **labels now:** type:bug,ingest,triage:active
- **verified:** WordNetDecomposer.cs:57 and ConceptNetDecomposer.cs:56 still WarnIfCiliMapMissing; OMW/SemLink/MapNet/WordFrameNet use EnsureCiliMapForIngest (hard-fail). Asymmetry intact in SourceEntityIdConventions.cs:30-43.
- **hygiene owed:** ok
- **done when:** All five CILI-dependent lanes either hard-fail or degrade with an explicit recorded degradation signal; no silent synset-anchor drop.

### #537 — model-lane: prove the quantized-container refusal path — GGUF/GPTQ/AWQ must hard-fail ingest
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** ADEQUATE
- **labels now:** type:bug,priority:normal,model-lane,triage:deferred
- **verified:** Model lane resolves *.safetensors only (SafetensorsContainerParser / ModelDecomposer). No CON_QUANT / GGUF/GPTQ/AWQ hard-fail gate. Unknown model_type still falls back to Llama (ArchitectureProfile.cs:49). Quantized containers are not explicitly refused — absence of parser ≠ proven refusal.
- **hygiene owed:** related #481
- **done when:** Ingest of GGUF/GPTQ/AWQ (and block-quant) hard-fails with a named gate test; no silent float mis-read.

### #754 — ingest: finish the document lane (Pillar 0) — stop borrowing UserPrompt; titles, source identity, license, typed trunk edges
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** STRONG
- **labels now:** priority:high,substrate-law,ingest,triage:completion-axis
- **verified:** DocumentDecomposer.SourceName still "UserPrompt"; SourceId=>UserPromptContent.Source; DocumentIngestAdapter SourceId:fileRoot. No HAS_TITLE in relation_types.toml (highway bit null). UserPrompt ingest present in journal.
- **hygiene owed:** Tied to #799/#800/#660.
- **done when:** DocumentSource id; titles via containers_of; content-only gate amended; malformed skip.

### #898 — Per-file resume for multi-file seeds — killed-run restarts must true-skip, not re-fold
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** UD/OMW set PerFileResume=true; IngestPipeline EmitFileMarker + HasSourceCompletedAsync exist. WiktionaryDecomposer and ConceptNetDecomposer do not set PerFileResume (ConceptNet is typically monolith; Wiktionary multi-file still exposed). Issue named all four — incomplete.
- **hygiene owed:** Partial land on UD/OMW; monolith strategy still owed for ConceptNet/Wiktionary.
- **done when:** Killed multi-file seed true-skips completed files without witness inflation for UD/OMW/Wiktionary/ConceptNet as scoped.

### #358 — doc 22 Phase B — Language-agnostic INTENT via frame evocation (replace chat's English regex)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** STALE
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** recall_route.c regex ladder removed (callers use recall_intent/query_shapes); generate_walk.c:208 still says intent_band(prompt) isn't built; api has recall_intent not intent_band.
- **hygiene owed:** Rewrite: regex already gone; remaining work is frame-evocation intent_band feeding p_intent_mask.
- **done when:** intent_band(prompt) exists, beats regex coverage gate, supplies walk_branches p_intent_mask.

### #359 — doc 22 Phase C — Language-agnostic TOPIC resolution via POS-in-context (replace English stoplist, deduplicate 3x implementation)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** STALE
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** resolve_topic.sql.in uses prompt_coherence but still English pronoun regex for follow-up (line 66); pos_class_transitions exists but not wired into resolve_topic.
- **hygiene owed:** Update duplication claim vs current prompt_coherence unification; keep POS-in-context + pronoun gate.
- **done when:** Topic resolution uses POS-in-context; no English pronoun stoplist; single shared function for chat/converse/recall.

### #360 — doc 22 Phase D — Session as a TRAJECTORY entity (retire session_topics unlogged-table stopgap)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** area:extension,area:app,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** session_topics.sql.in UNLOGGED table still live; chat.sql.in/session_record_prompt.sql.in still read/write it.
- **hygiene owed:** None.
- **done when:** Sessions are trajectory entities; session_topics gone; p_topic_bias populated from session trajectory.

### #379 — doc 18 Q6 — Echo-loop guard for the generation corpus (self-improvement vs self-contamination, same loop)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** area:extension,type:bug,priority:normal,module:laplace_substrate,triage:active
- **verified:** trajectory_generate.c has no trust/glicko/SourceTrust weighting on n-gram/corpus-count path (grep trust|rating|eff_mu empty).
- **hygiene owed:** Promote from scratchpad Q to binding acceptance with measured proof.
- **done when:** Prove trust-class weighting reaches n-gram fold, or ship guard that prevents self-contamination.

### #409 — walk_text generation lane times out at scale — GenCorpus perfcache blob prescribed
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** STALE
- **labels now:** read-side,perf,tracker-migration,triage:deferred
- **verified:** Live: ~79M attestations; walk_text('king',3…) hit statement_timeout 15s inside trajectory_continuations/separator_ids. GenCorpus retired (drop_retired_content_lane / trajectory_continuations) but replacement still times out.
- **hygiene owed:** Retitle off GenCorpus; prescribe fix against walk_continuations/trajectory_continuations cold path.
- **done when:** walk_text cold completes under API budget (≤30s) on ≥current corpus; measured log attached.

### #658 — voice: ORIENT can seed on a one-word translation lemma instead of the real gloss
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** triage:active
- **verified:** converse_walk.sql.in still documents one-word gloss/roi fall-through. Live: converse_walk('king',20) returned non-English fragment; chat walk path uses converse_compose (different). ORIENT seed quality defect remains on converse_walk.
- **hygiene owed:** Blocked-on-thin-substrate note obsolete (33M entities); defect still repro.
- **done when:** ORIENT prefers multi-word gloss in prompt language; king/roi class fixed.

### #804 — render(id) has one argument and one return type — it cannot express what the caller asked for
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** STRONG
- **labels now:** triage:active
- **verified:** Live api('render'): render(p_id bytea) RETURNS text. render.sql.in still COALESCE(canonical_names, chr(codepoint_for_id), resolve_name, render_text, hex) — cannot abstain for non-text intent; single projection.
- **hygiene owed:** Body truncated in GH but claim verified. Related #462.
- **done when:** Caller intent/containment selects projection; abstain (NULL) when containment does not say text; no silent mojibake for audio/etc.

### #878 — converse_tiered: content regressed to pure topic echo; CREATE TABLE blocks read-only lanes
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** Live: converse_tiered(word_id('dog')) → 'dog. dog. dog. dog.' (~12s). Function still CREATE TEMP TABLE _ct_unit/_ct_concept (writes; blocks read-only MCP op lane).
- **hygiene owed:** Do not wire into chat() until content+RO fixed.
- **done when:** Multi-clause witnessed content; no CREATE/TEMP write on describe path; RO-safe.

### #900 — chat() ELABORATE branch: inline (rating-2*rd) duplicates eff_mu, per-row realize(), magic band numbers
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** chat.sql.in ELABORATE block ~321-334 still (c.rating-2*c.rd) and per-row realize() with highway band literals 4/2/7 and English template strings.
- **hygiene owed:** Sibling #901/#903.
- **done when:** Use eff_mu; batch realize; no magic bands/open-coded formula.

### #901 — chat(): English prose templates hardwired in a language-agnostic surface
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** ELABORATE still hardwires English: 'has parts such as', 'Kinds of', 'is related to', 'That is the core of what I hold about' (chat.sql.in ~321-340). Pattern-ladder removal comments do not cover these templates.
- **hygiene owed:** Related #900.
- **done when:** No English-only template strings on language-agnostic chat surface.

### #903 — converse_facts(): six open-coded (rating-2*rd) sites and per-row realize() ahead of the sort
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** converse_facts.sql.in still has (c.rating-2*c.rd) at ORDER BY/select sites (52,66,122,127,155,162) beside eff_mu_display calls; realize() in select lists before LIMIT cuts (121,154).
- **hygiene owed:** Sibling of #900.
- **done when:** eff_mu helper only; realize after cut / batched.

## Rank 2

### #354 — Consolidate the four independent native walk/traversal engines (steered_walk.c, walk_continuations, walk_strongest, walk_branches)
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** STRONG
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** All four still present (steered_walk.c, trajectory_generate.c, generate_walk.c walk_strongest+walk_branches); no ADR deciding merge.
- **hygiene owed:** Acceptance is ADR-only — split follow-on implementation stories after decision.
- **done when:** ADR names which engines merge/stay and why; follow-on stories filed per engine work.

### #519 — foundry research: make the correction planes head-informative (apply the readout law to correction subspaces)
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** THIN
- **labels now:** spike,foundry,triage:research
- **verified:** Research item from spec 14; no code surface named 'correction plane readout'. Spike — needs research output before defect status.
- **hygiene owed:** triage:research
- **done when:** Research note + implementation or explicit decline of head-informative corrections.

### #369 — Uncracked-List C — OODA fold of walked model-pair evidence
- **status:** `OPEN_DEFECT` · **axis:** FOLD · **body:** THIN
- **labels now:** area:extension,area:app,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** Blocked on #368; no AiModelProbe walk-touch→FeedbackContent deposit path found as campaign deliverable.
- **hygiene owed:** Inline gate; label blocked-on #368.
- **done when:** Walk touch deposits attestation; next query has consensus; cross-model second deposit collides same cell.

### #539 — model-lane: tokenizer sidecars unread — special_tokens_map.json / added_tokens.json / tokenizer_config.json never witnessed at ingest
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** ADEQUATE
- **labels now:** ingest,model-lane,triage:deferred
- **verified:** rg under app/Laplace.Decomposers/Model for special_tokens_map|tokenizer_config|added_tokens.json = 0. FoundryCommands reads tokenizer_config on export side only. LlamaTokenizerParser may read added_tokens inside tokenizer.json, not the sidecar files. Sidecar witness gap stands.
- **hygiene owed:** related #484
- **done when:** Ingest witnesses special_tokens_map.json, added_tokens.json, tokenizer_config.json as source facts; export is not the only reader.

### #8 — Milestone: run an exported substrate GGUF in llama.cpp and judge it semantically
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** chunk-8,area:engine,area:app,area:ci,type:enhancement,priority:high,epic,triage:active
- **verified:** No external/llama.cpp; /opt/laplace/bin/llama-cli absent; model-synthesize-ci.sh exists but .github/workflows/laplace.yml has zero roundtrip/llama wiring.
- **hygiene owed:** Refresh subtask checkboxes against closed/open child issues; drop retired chunk-8 sequencing framing.
- **done when:** just/model-synthesize path produces GGUF that llama-cli loads and returns coherent chat; CI green on that path.

### #111 — Vendor + verify llama.cpp as the external validation harness for exported GGUFs
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** chunk-8,area:engine,area:app,area:ci,priority:high,story,triage:active
- **verified:** external/ lists blake3/eigen/spectra/… but no llama.cpp; /opt/laplace/bin/llama-cli missing.
- **hygiene owed:** Pin release tag + toolchain choice (gcc smoke vs icpx) in body.
- **done when:** /opt/laplace/bin/llama-cli --version after build-deps; CI Integration job asserts it.

### #112 — Model load test: exported substrate GGUF loads and generates in llama.cpp
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** chunk-8,area:engine,area:app,area:ci,priority:normal,story,triage:active
- **verified:** Blocked on #111; no vendored llama-cli and no proven exported GGUF load path in tree/CI.
- **hygiene owed:** Replace qwen3-0.6b path with TinyLlama pilot (#107/#8); add structural package checks.
- **done when:** Native Synthesis package validates; llama-cli -m <gguf> -p Hello loads and emits tokens.

### #272 — Export metadata must be recipe-driven — WriteGgufMetadata is a hardcoded llama template
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** area:engine,priority:normal,story,module:engine_synthesis,triage:active
- **verified:** No engine/synthesis llama_gguf_export.*; WriteGgufMetadata/HfToGgmlName still in app/Laplace.Cli/FoundryCommands.cs:1904+.
- **hygiene owed:** Update Program.cs line refs to FoundryCommands.cs.
- **done when:** llama_gguf_export.{h,c/cpp} + NativeInterop bindings + ctest -R llama_gguf.

### #273 — Move byte-BPE re-encoding + generation_config read out of WriteGgufMetadata (naming already native)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** area:app,priority:normal,story,module:engine_synthesis,triage:active
- **verified:** FoundryCommands.cs still defines WriteGgufMetadata and HfToGgmlName and calls them from synthesize paths.
- **hygiene owed:** Depends on #272; fix path refs Program.cs→FoundryCommands.cs.
- **done when:** No WriteGgufMetadata/HfToGgmlName or llama.* metadata key literals in app/*.cs.

### #368 — Uncracked-List B — Native SPI scorer (model_pair_score/model_row_topk)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** model_factor.c exposes model_pair_score/model_row_topk but scratchpad/issue treat as gate-instrument only; SQL==kernel ≤1e-6 gate not proven in CI.
- **hygiene owed:** Inline gate criteria from scratchpad 26 into issue so body is authoritative.
- **done when:** SQL score == kernel-direct ≤1e-6; row_topk == native brute force; TinyLlama ArchitectureProfile; no Python.

### #370 — Uncracked-List D — Frontier-as-residual composition (deep-layer replay)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** No frontier-as-residual / deep-layer replay implementation found; depends on B scorer (#368).
- **hygiene owed:** Inline L5≥90% gate + baseline citation into body.
- **done when:** Probe-set replay L5 top-1 attention agreement ≥90%; next-token top-5 overlap reported.

### #374 — Uncracked-List I — Argentina gate (campaign acceptance test)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** Depends on #368/#369/#370; no Argentina capital model-source-only gate implementation found.
- **hygiene owed:** Inline exact query surface + pass/fail reporting contract.
- **done when:** Model-source-only evidence surfaces Argentina capital via substrate query, or miss reported as measured.

### #486 — foundry: materialize_norm is one clamped scalar broadcast — least substrate-derived materializer
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** foundry,triage:deferred
- **verified:** engine/synthesis/src/arch_template.cpp:298-307 still clamps norm_aggregate to [0.5,2.0] (default 1.0) and write_dtype's the same scale for every hidden dim. Other slots in the same file pull per-pair/token geometry; this one remains a broadcast scalar.
- **hygiene owed:** triage:deferred; cite still matches live lines
- **done when:** materialize_norm writes a substrate-derived per-dimension (or documented equivalent) vector; no single clamped scalar broadcast for all hidden units.

### #488 — model-lane: model_forward v0 was merged while labeled UNVERIFIED — verify it against a rebuilt substrate
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,priority:normal,model-lane,triage:deferred
- **verified:** api('model_forward') returns model_forward + pg_laplace_model_forward live. SQL header still says 'model_forward v0' / linearized single-token (model_factor.sql.in:86-88; model_factor.c:334). Zero *Test* hits for model_forward. Verification debt unpaid.
- **hygiene owed:** still open verification; not a code absence
- **done when:** Documented live proof (regress or measured SELECT) that model_forward ranks agree with expected next-token behavior on a rebuilt model-seeded substrate; UNVERIFIED labels removed from ship path.

### #500 — engine: move FoundryExport/FoundryCommands transcendental math into engine/synthesis
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** area:engine,area:app,triage:active
- **verified:** rg Math.* in FoundryCommands.cs (~16) and FoundryExport.cs (~20) still present; FillGateBanded lives in FoundryExport.cs:1624. Layer law (math in C) unmet.
- **hygiene owed:** ok
- **done when:** Transcendental/tensor fill math invoked from engine/synthesis; C# orchestration only; native tests cover former C# sites.

### #515 — foundry: tier-scheduled layer operators — replace all-ops-every-layer with the resolution ladder
- **status:** `NEEDS_SPEC` · **axis:** MODEL_EXPORT · **body:** STRONG
- **labels now:** priority:normal,foundry,triage:deferred
- **verified:** No L-comp/L-wsd/L-frame schedule symbols in engine/. FillGateBanded still recipe/layer-indexed in FoundryExport. Specs 14/18 invent schedule; operator ruling on schedule+depth still blocking per body.
- **hygiene owed:** blocked on decision — keep triage:deferred
- **done when:** Operator-approved layer schedule implemented; layers run tier ladder ops not all-ops-every-layer.

### #516 — foundry: expose the per-layer operator schedule as a recipe knob (UI-specifiable) with a derived default
- **status:** `NEEDS_SPEC` · **axis:** MODEL_EXPORT · **body:** STALE
- **labels now:** priority:low,ingest,perf,triage:deferred
- **verified:** Title is recipe-knob for operator schedule; body is unrelated MERGE_CONFLICT bootstrap noise (~440e/~240p) from spec 14:344. Neither claim implemented as titled; body/title mismatch makes status a spec problem first.
- **hygiene owed:** CRITICAL: body pasted from wrong defect — rewrite or split MERGE_CONFLICT issue
- **done when:** After body repair: recipe exposes schedule knob with derived default; UI can override.

### #518 — foundry: re-measure FillGateBanded band membership now that entities.highway_mask is populated
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** priority:low,foundry,triage:deferred
- **verified:** Live: 33,131,069/33,131,069 entities have non-zero highway_mask. FillGateBanded still in FoundryExport.cs deriving band centroids via export path, not entity masks. Re-measure/comparison owed, not done.
- **hygiene owed:** doc claim of 0-populated masks fully obsolete
- **done when:** Side-by-side measurement of mask-band vs current FillGateBanded membership; choose path with recorded numbers.

### #521 — foundry: typed residual stratum allocation (S/W/C/F/G subspaces; heads as typed maps)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** STRONG
- **labels now:** type:enhancement,priority:normal,foundry,triage:deferred
- **verified:** stratum/strata only in docs (spec 18, INVENTIONS); 0 implementation hits in app/engine/extension sources. Unbuilt.
- **hygiene owed:** ok
- **done when:** d_model allocated into named S/W/C/F/G strata; heads are typed maps; synthesis uses them.

### #522 — foundry: synthesize selectors — EVOKES_FRAME/definition planes as QK, G-subspace bookkeeping
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** foundry,triage:deferred
- **verified:** FillGateBanded (band-FFN) exists; no EVOKES_FRAME/definition→QK selector synthesis or G-subspace bookkeeping in engine/synthesis or generation SQL. Selector half still absent.
- **hygiene owed:** ok
- **done when:** Frame/definition planes synthesize into QK selectors with G-subspace bookkeeping per spec 18 §4.

### #538 — model-lane: TOK-BPE encode-replay gate — re-tokenize from witnessed vocab+merges and prove id-for-id agreement
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:enhancement,model-lane,triage:deferred
- **verified:** BpeTokenizerRoundTripTests only checks EntityId invariance WordLevel vs BPE and ParseMerges round-trip — not re-tokenizeize text via witnessed merges vs source tokenizer ids. Encode-replay gate absent.
- **hygiene owed:** do not confuse with existing BpeTokenizerRoundTripTests
- **done when:** Gate re-encodes corpus bytes from witnessed vocab+merges and asserts id-for-id match to source tokenizer.

### #757 — read-side: port infer() to C — both directions, n-hop bias family, multi-step loop
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** area:extension,read-side,perf,triage:completion-axis
- **verified:** infer.sql.in still LANGUAGE sql with stated limits (forward-only, one-hop, single-step). No C port beside prompt_coherence.
- **hygiene owed:** Depends on #756 wiring decisions.
- **done when:** C infer with both-directions + n-hop; SQL thin binding; regress parity.

### #350 — walk_branches: hilbert-band-scoped search for trajectory-ordinal continuity (currently opt-in, default-off)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** walk_branches.sql.in p_ordinal_continuity DEFAULT false; generate_walk.c ORDINAL_CONTINUITY_QUERY still unscoped containment (comment cites 64-partition Append).
- **hygiene owed:** None critical — keep EXPLAIN acceptance.
- **done when:** EXPLAIN shows few partitions on miss; default p_ordinal_continuity=true without regress timing regress.

### #351 — walk_branches: generalize to the unfiltered-type case (recall_walk_response 'walk' mode still uses the old walk_strongest)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** recall_walk_response.sql.in: complete→walk_branches; walk mode still FROM walk_strongest (lines 63+).
- **hygiene owed:** None.
- **done when:** 'walk' routes through enriched walk_branches (or equal) with bounded partition scans on EXPLAIN.

### #362 — recall.c: highway-mask read-side gating (B6 continuation — generate_walk.c already done, recall.c untouched)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** grep highway_mask/mask_overlaps/p_intent_mask in recall.c: zero matches; generate_walk.c has mask_overlaps.
- **hygiene owed:** List exact recall.c entry points to gate.
- **done when:** respond_impl/word_shape_peers_fast_impl (etc.) AND against entities.highway_mask when caller supplies band/intent.

### #364 — WSD sense-selection via frontier-queue backtrack semantics (0=backtrack, not abstain)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** No WSD frontier-backtrack wiring found; overlaps #371 Uncracked E; generate_walk beam exists but not sense-queue semantics.
- **hygiene owed:** Cross-link #371; pick one owning issue.
- **done when:** Documented ambiguous case (ohm unit vs name) resolves via backtrack when top-1 dead-ends.

### #371 — Uncracked-List E — WSD witness loop (living priors)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** THIN
- **labels now:** area:extension,area:app,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** Overlaps #364 mechanism; no corpus-pass HAS_SENSE witness-loop depositor beyond existing WordNet SemCor priors path.
- **hygiene owed:** Merge or clearly subordinate to #364; inline gate.
- **done when:** One corpus pass shifts sense witness mass; ohm→unit-not-person via prior+context.

### #401 — read-side: generic native KNN driver (candidate generation + ranking in one native call)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** THIN
- **labels now:** read-side,perf,tracker-migration,triage:active
- **verified:** word_shape_peers_fast is native (recall.c); structural_neighbors still LANGUAGE plpgsql SQL over v_word_points (structural_neighbors.sql.in); api('structural_neigh') returns SQL functions only.
- **hygiene owed:** List exact callers still on SQL KNN; drop Plan-13 Phase-3 pointer.
- **done when:** structural_neighbors/_of (+ listed peers) are one native SPI call; EXPLAIN shows no correlated CTE anchor defect.

### #457 — read-side: serving chain stacks non-inlined SQL functions — build native-SPI translate_to_fast
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** substrate-law,read-side,perf,triage:active
- **verified:** api('translate_to') returns SQL translate_to only; no translate_to_fast; body still stacks synset_members/label/render_text (translate_to.sql.in).
- **hygiene owed:** Pin EXPLAIN of stacked Function Scans as acceptance baseline.
- **done when:** Native SPI translate_to_fast installed; planner sees one plan; regress pin; serving callers switched.

### #458 — read-side: relate_path + laplace_ancestry still WITH RECURSIVE — lift to the native graph engine
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** THIN
- **labels now:** read-side,perf,triage:deferred
- **verified:** relate_path.sql.in and laplace_ancestry.sql.in both WITH RECURSIVE; astar_path native exists in api() but does not replace them.
- **hygiene owed:** Merge tracking with #461 or declare dependency.
- **done when:** relate_path/laplace_ancestry implemented via native graph engine; no WITH RECURSIVE in their bodies.

### #459 — read-side native lift: rank_edges(subject_ids, types, k) — collapse four duplicated ranking cores
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** THIN
- **labels now:** read-side,perf,triage:deferred
- **verified:** api('rank_edges') empty; salient_facts/related/top_relations/evidence_receipt each ORDER BY eff_mu/edge_rank in separate SQL cores.
- **hygiene owed:** Name the four call sites with file paths in body.
- **done when:** rank_edges native primitive exists; four callers delegate; one ranking definition.

### #460 — read-side native lift: taxonomy_closure(seed_ids, dir, depth) — replace 4 recursive CTEs
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** EMPTY
- **labels now:** read-side,perf,triage:deferred
- **verified:** api('taxonomy_closure') empty; taxonomy_tree uses walk_strongest SQL; recursive closures remain (e.g. constituents_closure WITH RECURSIVE).
- **hygiene owed:** Body needs the four CTE names/paths; currently scratchpad-only.
- **done when:** taxonomy_closure installed; four recursive CTE call sites removed or thin wrappers.

### #461 — read-side native lift: resolve_path — unify relate_path's recursive CTE with astar_path.c
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** THIN
- **labels now:** read-side,perf,triage:deferred
- **verified:** api('resolve_path') empty; relate_path still WITH RECURSIVE while astar_path/laplace_astar_path are separate native entrypoints.
- **hygiene owed:** Explicitly supersede or merge with #458.
- **done when:** One path engine; relate_path/resolve_path are thin SQL over native astar (or successor).

### #462 — read-side: reconstruct_content(id)->bytea, entity_form(id), decompose(text) missing from the SQL surface
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** read-side,triage:active
- **verified:** api() empty for reconstruct/entity_form/decompose; ContentRoundtrip.ReconstructAsync hand-walks TrajectoryTreeDumpPointsAsync (ContentRoundtrip.cs:26-64). entity_physicalities exists but is not entity_form.
- **hygiene owed:** Note entity_physicalities landed as adjacent surface; keep three named gaps.
- **done when:** api() lists reconstruct_content, entity_form, decompose; CLI roundtrip uses them; regress pins.

### #464 — read-side: edge_strength(subject,type,object) + batch form; bless label() or render() as the one renderer
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** THIN
- **labels now:** read-side,design-decision,triage:decision
- **verified:** api('edge_strength') empty; both label() and render() families installed (api lists both); C helper laplace_edge_strength in spi_common.h is not a SQL function.
- **hygiene owed:** Split SQL edge_strength delivery from renderer blessing (design-decision).
- **done when:** edge_strength(+batch) in api(); ADR or code comment declaring sole renderer; callers migrate.

### #501 — read-side: salient_facts 16-entry relation exclusion list should be driven from relation_types.toml bands
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** read-side,triage:active
- **verified:** salient_facts.sql.in:36-48 still hardcodes NOT IN (HAS_SENSE, IS_SENSE_OF, HAS_LANGUAGE, PRECEDES, …) plus feat_dynamic / HAS_POS|HAS_FEATURE families. Manifest salience bands not the driver.
- **hygiene owed:** partial family helpers already used; residual is the literal list
- **done when:** Exclusion derived from governed band/family metadata; no hand list of canonical names in salient_facts.

### #514 — foundry: wire volatility, source-trust class and raw outcome polarity into the generation planes (P5 residual)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** area:extension,foundry,triage:deferred
- **verified:** generation/*.sql.in uses walk_edge_weight(rating,rd,witness_count) / foundry_rd_kappa; rg volatility under generation/ = 0 hits. No source_id in plane weights. rd+witness half landed as body says; volatility/trust/polarity still unread.
- **hygiene owed:** ok
- **done when:** Generation planes consume consensus.volatility, source-trust class, and outcome polarity in the weight; measured difference on contested vs settled edges.

### #524 — read-side: derived word→synset→word projection so hub synonymy reaches 1-hop planes
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** read-side,foundry,triage:deferred
- **verified:** api has synonyms()/recall_synonyms_response (lexical read), not a synthesized generation plane from 2-hop word→synset→word. Generation planes do not project hub synonymy to 1-hop. Mesh deposited; plane synthesis gap remains.
- **hygiene owed:** note synonyms() ≠ generation plane
- **done when:** Synthesized 1-hop synonymy (and stated hub-POS/EVOKES_FRAME) plane consumed by generation/foundry path.

### #576 — chess: read surface phase 2 — motif queries, book-vs-practice contrast, position similarity
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** area:app,type:enhancement,triage:active
- **verified:** GAME_HAS_MOTIF live (846551 consensus rows) but no chess_games_with_motif() in api(). chess_opening_shape_peers exists (W16 sibling). No book-vs-practice contrast helper. Motif SQL surface + graph_contrast-style book/practice still missing.
- **hygiene owed:** Parent #818; shape retargeted to #820/#821.
- **done when:** chess_games_with_motif; book-vs-practice contrast; Frechet peers via shared compose ISA (retire sibling dialect).

### #753 — coherence: sense election needs a salience prior — content-band mass comparator + HAS_SENSE_RANK at WordNet ingest
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** type:bug,ingest,read-side,triage:completion-axis
- **verified:** ICF/specificity prior exists in prompt_coherence.c; dog→canine chat OK. Trumpet still elects verb 'proclaim'. HAS_SENSE_RANK absent from relation_types.toml and consensus count=0.
- **hygiene owed:** Partial prior landed; acceptance (trumpet→instrument + HAS_SENSE_RANK) unmet.
- **done when:** dog→canine, trumpet→instrument, car→automobile + held-outs; HAS_SENSE_RANK attested.

### #756 — read-side: questions route themselves — relation-name attestation + infer() rel_type wiring
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** type:enhancement,read-side,triage:completion-axis
- **verified:** infer.sql.in still signature infer(text,int) — no rel_type_id wiring; CAPITAL_OF absent from relation_types.toml. Relation-name attestation work not evidenced.
- **hygiene owed:** Related #575/#510.
- **done when:** Named relations route infer/chat without bespoke SQL (Magnus openings example).

### #785 — read-path: sense election ignores the language it already computed — multilingual seeding breaks every prompt
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STALE
- **labels now:** type:bug,priority:normal,read-side,triage:completion-axis
- **verified:** prompt_coherence has lang_agree; live chat(glacier) elects ice-mass OK, but chat(pawn) returned Hock/consign money gloss — chess-piece claim falsified on this box. Keep open until held-out multilingual+homograph suite passes.
- **hygiene owed:** Replace closed-table claims with held-out prompt suite (glacier, pawn, dog/Bulgarian, etc.) and current elect results.
- **done when:** Held-out prompt suite: language-correct sense for glacier/pawn/homographs; no silent wrong-lang election.

### #820 — chess: compose shape/DTB/missed-finish over existing ISA surfaces — delete sibling Frechet engines
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** area:extension,substrate-law,read-side,triage:active
- **verified:** chess_opening_shape_peers.sql.in still private Frechet over entity_curve — does not call structural_neighbors_of/word_shape_peers_fast. chess_distance_to_syzygy and chess_missed_finish remain installed sibling programs (api()).
- **hygiene owed:** Geometry Rule #3 comments correct; composition unpaid.
- **done when:** Shape peers = shared surface/wrapper; DTB/missed-finish as OP composition or tracked opcode block; no second Frechet generator.

### #833 — chess: compose UCI/play as full Chess Forward Pass (STEER over observational frontier, not ±150cp straw)
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** area:app,type:enhancement,substrate-law,triage:active
- **verified:** SubstrateRootBias/SubstructureFoldBias still default cpPerPoint=8 capCp=150. docs/guides/chess.md and chess-lab.md state UCI STEER is ±150 straw and Syzygy/shape/motifs not wired into UCI STEER.
- **hygiene owed:** PROPOSE spine exists; STEER composition is the gap.
- **done when:** STEER reads fuller observational frontier; measured Elo lift; single engine tree.

### #861 — Elector ranks on a language-specific word-order prior because the joint signals are inert
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** Live prompt_coherence('The opposite of hot is'): coherence=0, rel_mass=0, rel_type_id NULL for opposite/hot; specificity prior machinery exists (entity_container_degree / election_token_profile) but joint keys still inert on the issue's probes — elector fallthrough class remains. Follow-on #865/#864.
- **hygiene owed:** #865 claims containment prior landed and regressed score — verify as OPEN residual of this defect class.
- **done when:** Joint signals discriminate; election_correctness not ord-DESC collapse on foundation probes.

### #864 — prompt_coherence only detects relation names it already has edges for
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** Source prompt_coherence.c claims whole-manifest naming (GH #864 comments ~585-605) and installed .so newer than source, but LIVE still: opposite/hot and synonym/dog all rel_mass=0, rel_type_id NULL. Symptom of issue unrepaired on running system (opposite≠ANTONYM segment may be separate naming gap).
- **hygiene owed:** STALE relative to source comments; live falsifies 'fixed'.
- **done when:** prompt_coherence('The opposite of hot is') names oppositional relation; highway-mask gating as specified.

### #865 — Containment prior alone over-rewards rare tokens; must be weighed against the fold
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** election_token_profile shows urine icf 0.114 > water 0.077 (rarity prior alone prefers urine). entity_container_degree/icf present; issue's claim that raw ICF elects opposite over hot matches same mechanism. OP4-weighted combination not the sole election key.
- **hygiene owed:** Follows #861.
- **done when:** Election weighs fold signals (rank×μ×rd×witness) with containment; hot/water probes elect content tokens/senses correctly.

### #905 — Read-law sweep: 9 LIMIT-without-ORDER picks, 4 EXISTS tri-state collapses, 3 render-to-classify sites
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** Spot-checked cited sites still violative: first_placed_topic.sql.in LIMIT 1 no ORDER BY; source_roster.sql.in LIMIT p_limit no ORDER BY; mesh_position.sql.in LIMIT 1 on IS_TYPED_AS without order + EXISTS patterns. Sweep claim holds for checked samples.
- **hygiene owed:** Full 9/4/3 census not re-counted line-by-line; sampled sites confirm OPEN.
- **done when:** Each cited site ordered or rewritten; EXISTS tri-state honest; no render-to-classify.

### #355 — doc 15 B3 — Behavioral harness as witness (standing SQL-engine test suite deposited as attestations)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** No BehavioralHarness source/attestation path in extension or app greps.
- **hygiene owed:** Promote binding acceptance out of scratchpad 15 into issue body.
- **done when:** Harness runs on seeded DB, deposits attestations, Glicko trend queryable.

### #356 — doc 15 B4 — Walk-policy rating (decode policies as content-addressed, Glicko-rated entities)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** area:extension,area:app,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** No walk-policy entity/eff_mu selection path found; walk_branches still takes hardcoded depth/breadth defaults.
- **hygiene owed:** Cite call site that will select policies.
- **done when:** Policy tuples content-addressed+attestable; at least one caller selects by eff_mu/rd.

### #357 — doc 15 D — Loop-closure metrics (feedback-to-next-walk delta, depth-k accuracy, latency budget)
- **status:** `OPEN_DEFECT` · **axis:** SPEAKING · **body:** ADEQUATE
- **labels now:** area:extension,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** Depends on #355; no loop-closure metric helpers in api()/extension greps.
- **hygiene owed:** Mark blocked-on #355 explicitly in labels.
- **done when:** Queryable trending metrics for depth-k accuracy, feedback→walk delta, µs/step.

## Rank 3

### #472 — model-lane: rank-aware OV factor storage (v_h + per-head O-basis) before the TinyLlama gate
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** THIN
- **labels now:** design-decision,model-lane,triage:decision
- **verified:** No decided OV storage shape in code; issue correctly blocks TinyLlama gate on a design choice (~18TB naive rank-d cited, not re-measured here).
- **hygiene owed:** Needs storage ADR before implementation; triage:decision.
- **done when:** Accepted storage design; TinyLlama gate runs against that shape.

### #475 — model-lane: Tier-2 behavioral-fidelity gates against a llama.cpp reference
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** THIN
- **labels now:** spike,model-lane,triage:research
- **verified:** No llama.cpp behavioral gate tests in ModelGate* suite; issue is research/spike by label — scope of 'behavioral fidelity' undefined in code.
- **hygiene owed:** Define non-weight-recovery metrics before coding.
- **done when:** Named behavioral gates vs llama.cpp reference with pass thresholds; no weight-recovery L2.

### #504 — ingest: decide the git-lane relation ledger (reuse vs new) BEFORE codegen — one reseed should pay for it
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** STALE
- **labels now:** ingest,design-decision,triage:decision
- **verified:** ADR 0001 ACCEPTED; bit=N append-only landed (#551 CLOSED); TOML has reserved git relations (e.g. HAS_COMMIT_MESSAGE ~1816+). Alphabetical-renumber premise in body is obsolete. ADR §2 still names GH #504 completeness pass for git-lane ledger before freeze. Repo decomposer #452 still OPEN. Decision on reuse vs new set not recorded as closed.
- **hygiene owed:** rewrite body against ADR 0001; drop renumber claim
- **done when:** Operator-signed git-lane relation ledger (reuse vs new) reflected in relation_types.toml bits; #452 implements against that ledger.

### #894 — Code-as-player: novel code generation with toolchain outcomes as testimony
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** No ToolchainWitness / code-as-player loop in app/. Repo/code ingest + CALLS/DEFINES exist as cited. Operator directive for a new closed loop — invention work needing design, not a failing existing path.
- **hygiene owed:** Requires explicit implementation authorization.
- **done when:** Generate→stage→toolchain witness→attestation loop with pass/fail testimony on code entities.

### #418 — tree-sitter format router on IngestInput: route .md/.rst/.html/etc through grammar containers, not the UAX29 document lane
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** THIN
- **labels now:** ingest,tracker-migration,triage:active
- **verified:** DocumentFileExtract yields raw ContentIngestRecord bytes (DocumentDecomposer.cs:116); IngestInput has no format→grammar router; TreeSitterTextAdapter only opens streams listing .md in TextExt.
- **hygiene owed:** Name exact extensions + valet entrypoint; distinguish DocumentDecomposer vs RepoDecomposer paths.
- **done when:** Ingest of .md/.rst/.html uses grammar modality containers (not bare UAX29 document spine); T-SQL docs case non-NOOP.

### #577 — chess: seed-manifest hygiene — CORRESPONDS_TO/HAS_NAME_ALIAS emitted but undeclared; GAME_AT/GAME_AT_PLY/Chess_Concept declared but dead
- **status:** `OPEN_DEFECT` · **axis:** INGEST_SPINE · **body:** STALE
- **labels now:** area:app,type:bug,ingest,triage:active
- **verified:** Emitted-but-undeclared half FIXED: ChessSeedManifest.Relations now includes CORRESPONDS_TO, HAS_NAME_ALIAS. Declared-but-dead half LIVE: GAME_AT/GAME_AT_PLY still in Relations; live attestations count 0 for both. Chess_Concept still in Types.
- **hygiene owed:** Update body: declare half done; retire-or-annotate dead half remains.
- **done when:** All emitted relations declared; dead declarations retired or reserved with citation.

### #187 — Wiktionary: remaining unbuilt relations — HAS_IPA_PRONUNCIATION, HAS_AUDIO_PRONUNCIATION, HAS_INFLECTION_FORM
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STALE
- **labels now:** area:engine,area:app,priority:normal,story,triage:active
- **verified:** WiktionarySource.cs Relations omit those three names; WalkSounds emits IPA as TRANSCRIBES_AS; FORM_OF exists; no audio emission; live evidence_count(WiktionaryDecomposer)=0.
- **hygiene owed:** Retitle around audio gap + whether TRANSCRIBES_AS/FORM_OF supersede named IPA/inflection relations.
- **done when:** Declared relations cover IPA/audio/inflection (or documented aliases); seeded Wiktionary evidence_count > 0 for those edges.

### #365 — ConceptNet: re-seed on hart-desktop (0 attestations currently — prior killed refold never completed, likely lost in cluster rebuild)
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** ADEQUATE
- **labels now:** area:app,type:bug,priority:normal,triage:active
- **verified:** Live: evidence_count(p_source=>source_id('ConceptNet'))=0 and source_id('ConceptNetDecomposer')=0; ConceptNet absent from source_counts_approx().
- **hygiene owed:** Retitle host-agnostic (this DB also empty); use source_counts_approx not ad-hoc hash.
- **done when:** source_counts_approx shows ConceptNet evidence >> 0; consensus sample evidence-without-consensus near 0%.

### #372 — Uncracked-List F — Encyclopedic lane (proper names, Wikidata-shaped decomposer)
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** THIN
- **labels now:** area:app,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** No Wikidata*Decomposer under app/Laplace.Decomposers/; proper-name encyclopedic lane absent.
- **hygiene owed:** Inline 84.7%→≥95% coverage gate + license attestation requirement.
- **done when:** Wikidata-shaped decomposer seeded; probe-vocab coverage ≥95%.

### #449 — chess: ply-cap-adjudicated self-play games recorded as draw on every edge (C14)
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** ADEQUATE
- **labels now:** area:app,type:bug,triage:active
- **verified:** ModalityEngine.PlayGameAsync still `var outcome = terminal ?? GameOutcome.Draw` at line 225; MatchRunner ply-cap returns adjudicated Draw (MatchRunner.cs:92-93).
- **hygiene owed:** Keep; cite ModalityEngine.cs:225 as pin.
- **done when:** Ply-capped games adjudicate by final eval or exclude edges from outcome fold; no false Draw mass on winning positions.

### #452 — Repo decomposer: witness the git object DB as a Merkle-DAG (commits/trees/refs), not just the working tree
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** area:app,type:enhancement,ingest,triage:active
- **verified:** VendoredPathFilter excludes ".git" (VendoredPathFilter.cs:28); RepoDecomposer.EnumerateRepoFiles skips vendored paths — no GitCommit/GitTree entities.
- **hygiene owed:** Fine; mark enhancement not bug if desired.
- **done when:** Ingest witnesses blobs/trees/commits/refs; same-bytes blob and working-tree file share content id with distinct provenance.

### #512 — chess: build the board geometry ladder — square/piece S3 anchors, move transitions, position entities, packed game trajectories
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** area:app,type:enhancement,priority:normal,substrate-law,triage:active
- **verified:** 0 SquareAnchor/BoardAnchor hits. ChessCompose still centroids child/token coords (ChessCompose.cs:316; ChessGraph.cs:293). Positions ride text-token geometry, not board S3 anchors. Gaps 1–5 unbuilt. 13.7M Chess_Position entities live on the wrong ladder.
- **hygiene owed:** ok
- **done when:** Board tier-0 anchors + move/position geometry + packed trajectories per spec 11; positions no longer centroid-of-text-tokens.

### #574 — chess: book decomposer under-extracts — grandmaster books yielded 82 games / 2.3k rows, txt-only
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** ADEQUATE
- **labels now:** area:app,ingest,triage:completion-axis
- **verified:** ChessInput.BookExtensions = [".txt"] only; ChessBookDecomposer EnumerateFiles uses that. No PDF/epub unpack. ChessBook skipped-complete / small prior ok run on host; yield gaps unfixed.
- **hygiene owed:** completion-axis; extraction quality still thin by design of txt-only gate.
- **done when:** Non-txt containers unpack; mid-game diagram commentary grounds; EXPLAINS yield rises on GM corpus.

### #593 — code semantics: tree-sitter gives syntax, not resolved symbols — need an LSP-backed decomposer for real IMPORTS/EXTENDS/IMPLEMENTS/type relations
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** area:app,type:enhancement,ingest,triage:active
- **verified:** CodeSource still declares CALLS/DEFINES/REFERENCES; live DEFINES consensus count=0. No LSP decomposer. Tree-sitter grammar path unchanged.
- **hygiene owed:** Enhancement-shaped; DEFINES declared-but-dead is a smaller concrete bug inside.
- **done when:** Resolved IMPORTS/EXTENDS/IMPLEMENTS (or scoped CALLS) from LSP lane; DEFINES either emitted or undeclared.

### #605 — chess: Syzygy closings catalog — compose + live proof (probing-oracle framing superseded)
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** substrate-law,ingest,triage:active
- **verified:** Code: ChessSyzygyDecomposer + chess_syzygy_line/distance/missed_finish in api(). Live: HAS_WDL=0, HAS_DTZ=0 — catalog not resident. Parent #818/#821 still own live smoke.
- **hygiene owed:** Architecture landed; host proof red.
- **done when:** Non-zero HAS_WDL/HAS_DTZ; chess_syzygy_line returns rows; #819/#820 compose cleanup.

### #803 — tatoeba audio: ingest the recordings, not just the text — the cross-modal bridge that makes audio a first-class entity
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** TatoebaDecomposer still sentences.csv/links only. No TatoebaAudioDecomposer.cs (IngestIntegrityGateTests asserts file absent). source_status shows TatoebaAudioDecomposer known/ingested with evidence_approx=0, last_run capped — bootstrap ghost, not audio ingest. TrackAudioDecomposer is generic fixture lane, not Tatoeba bridge.
- **hygiene owed:** Blocked on #804 render intent; ladder above T0 and storage still open decisions.
- **done when:** Sentence+recording two entities with attested link; two speakers → one sentence two recordings; typed cross-modal read.

### #806 — format-native metadata extractors: Gutenberg headers, EPUB/OPF, PDF-XMP, ID3, EXIF — feed the work node
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** triage:active
- **verified:** FileEntity.cs comments anticipate EXIF/ID3/PDF-XMP append; no Gutenberg/EPUB/XMP/ID3/EXIF extractors feeding work Merkle. Blocked on #801. ChessBookDecomposer reads Gutenberg chess books as chess modality, not work-node title/author.
- **hygiene owed:** Depends #801/#800.
- **done when:** Gutenberg txt → work with header title+author; same book different releases → one work; no-header file expresses no work (no filename synthesis).

### #818 — EPIC: chess catalog dual + substrate ISA compose — modality stress of the invention
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** STRONG
- **labels now:** area:app,epic,substrate-law,triage:active
- **verified:** Children still open: #820 sibling Frechet SQL live; #821 Syzygy not ingested (source_status known=f); #833 STEER still ±150 SubstrateRootBias; #822 laplace_chess_position_ready()=f. Epic completion criteria unmet though PR #817 write path exists.
- **hygiene owed:** Framing valid; unfinished≠invalid per body.
- **done when:** Children done-means; ISA compose not sibling dialect; live LINE/Syzygy proof; forward-pass STEER beyond straw.

### #821 — chess: live proof — re-seed openings as LINEs, Syzygy closings smoke, QGD entity_curve Frechet
- **status:** `OPEN_DEFECT` · **axis:** MODALITY · **body:** ADEQUATE
- **labels now:** area:app,ingest,triage:ops-blocked
- **verified:** ChessOpenings source_status evidence_approx=0 last skipped-complete; ~3171 tier-3 traj rows first_observed_by ChessOpenings exist but Syzygy source known=false evidence=0. chess_syzygy_line installed but closings not live. QGD Frechet on deposited lines not re-proven this pass.
- **hygiene owed:** triage:ops-blocked still accurate.
- **done when:** Re-seed openings; LINE+ECO proof; Syzygy HAS_WDL/DTZ rows; optional QGD Frechet class; via api() only.

### #114 — Scoped-pour validation: a source/context-filtered export differs from the default pour in the expected direction
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** chunk-8,area:engine,area:app,area:ci,priority:normal,story,triage:active
- **verified:** No shrunken/scoped-pour CI or recipe artifact path found; depends on #111/#112 load harness.
- **hygiene owed:** Rewrite acceptance around scoped-pour/filter semantics (title) not just 'shrunken.json loads'.
- **done when:** Filtered export differs from default in expected direction; both load and generate under llama-cli.

### #373 — Uncracked-List G — Sentence-grain probes (positional heads)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** area:extension,area:app,type:enhancement,priority:normal,module:laplace_substrate,triage:active
- **verified:** No sentence-grain positional-head probe/analyzer pass found in extension/app for this campaign item.
- **hygiene owed:** Inline gate; define decoder-ring coverage metric.
- **done when:** Analyzer records per-circuit positional signatures; coverage on heads semantic join left silent.

### #376 — Foundry: geometric attention/positional operators (angular+Frechet head, hilbert/trajectory positional encoding, Voronoi MoE routing)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** area:engine,type:enhancement,priority:normal,module:engine_dynamics,module:engine_synthesis,triage:active
- **verified:** engine/synthesis grep finds no Frechet/Voronoi/hilbert-positional/angular-attention export operators.
- **hygiene owed:** Split into per-operator issues when picked up.
- **done when:** Each listed export-side geometric operator implemented with tests; distinct from read-side walk geometry.

### #383 — ArchitectureProfile: QK-norm, decoupled head_dim, prefix-drift pattern matching (near-term model-family coverage gaps)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** area:app,type:bug,priority:normal,triage:active
- **verified:** ArchitectureProfile.For still `_ => Llama` (ArchitectureProfile.cs:49) with only llama/phi/qwen2/bert; HeadDim/QkNorm parsed in ModelConfigReader but unknown model_type still silent-maps.
- **hygiene owed:** Retitle to include silent-Llama fallback (#481); note QNorm/KNorm tensors are loaded under NormFold (ModelTokenEdgeETL.cs:731) so 'unmodeled' is overstated for witness path.
- **done when:** Unknown model_type hard-fails; profiles exist or refuse for Qwen3/DeepSeek/Llama-4; HeadDim!=hidden/heads and QK-norm configs round-trip without Llama mis-map.

### #384 — ArchitectureProfile: MoE storage conventions, MLA (DeepSeek), DSA lightning indexer, FP8 block-quant, MTP layer count (DeepSeek/advanced-MoE coverage)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** area:app,type:enhancement,priority:normal,triage:active
- **verified:** ModelConfigReader reads num_experts + MLA ranks only; ArchitectureProfile has no MoE/MLA/DSA/FP8/MTP profiles; MoE path only router-tensor COMPLETES_TO (ModelTokenEdgeETL.cs:869+).
- **hygiene owed:** Split MTP/FP8 from MLA/MoE if scoped separately; drop scratchpad-only authority.
- **done when:** Each listed family either has an explicit ArchitectureProfile path that witnesses its tensors, or ingest hard-fails with a named gap.

### #473 — model-lane: A2 while-hot single-pass analyzer stack — HilbertIndex written as default
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,model-lane,triage:deferred
- **verified:** ModelTokenEdgeETL.cs:466 still `HilbertIndex: default` on projection physicality; line 1109 encodes centroid elsewhere — geometric index absent on that deposit path.
- **hygiene owed:** Update line cites; keep type:bug.
- **done when:** Model-lane physicalities deposit non-default HilbertIndex; KNN/readback finds them.

### #474 — model-lane: enforce recorder-first — refuse or auto-run the witnessed layer when layer_complete=False
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** model-lane,triage:deferred
- **verified:** Analyzer path runs when planes mode != structure; incomplete recorder is not a hard refuse — only markers skip re-derive (ModelDecomposer.cs:306-317); IsComplete gates inventory not analyzer.
- **hygiene owed:** Pin exact refuse/auto-run behavior in acceptance.
- **done when:** Analyzer cannot emit when witnessed layer incomplete; refuse or auto-run recorder first.

### #476 — model-lane: implement the named ModelGate_* exactness gates
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** model-lane,triage:deferred
- **verified:** Only ModelGateFactorReadbackTests.cs / ModelGate_FactorReadback_MiniLM exists under Decomposers.Tests/Model.
- **hygiene owed:** Enumerate missing ModelGate_* names in body from 27b table.
- **done when:** Named ModelGate_* set implemented (or explicitly deferred with reason) beyond FactorReadback.

### #477 — model-lane: ModelConfigReader does not parse rope_scaling
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** model-lane,triage:deferred
- **verified:** rg rope_scaling under app/Laplace.Decomposers/Model = 0; ModelConfigReader reads rope_theta only.
- **hygiene owed:** Adequate.
- **done when:** rope_scaling dict witnessed on ModelConfig/recipe; scaled-RoPE models not silently mis-recorded.

### #478 — model-lane: sliding_window / chunked-attention scalars are not witnessed
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** model-lane,triage:deferred
- **verified:** No sliding_window/use_sliding_window parse in ModelConfigReader or Model decomposer sources.
- **hygiene owed:** Adequate.
- **done when:** sliding_window (and family aliases) witnessed on recorder layer; gate for Qwen2-class config.

### #479 — model-lane: SafetensorsContainerParser ignores model.safetensors.index.json and silently unions shards
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,priority:normal,model-lane,triage:deferred
- **verified:** SafetensorsContainerParser.ParseModel globs *.safetensors and unions (SafetensorsContainerParser.cs:29-44); no index.json read or missing-shard hard-fail.
- **hygiene owed:** Keep priority:normal bug.
- **done when:** Reads index.json weight_map; missing shard hard-fails; partial checkpoint not recordable as complete.

### #480 — model-lane: safetensors __metadata__ block is skipped, not witnessed
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** model-lane,triage:deferred
- **verified:** safetensors_parser.cpp sets has_metadata and skips content (lines 182+); C# parser never stages __metadata__ as substrate testimony.
- **hygiene owed:** Update line cite from obsolete :76 continue.
- **done when:** __metadata__ key/values witnessed as provenance; gate proves presence when header carries the block.

### #482 — model-lane: fused-QKV (c_attn/query_key_value), fused gate_up, and GPT-2 Conv1D transpose unhandled
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** model-lane,triage:deferred
- **verified:** No c_attn/query_key_value/gate_up/Conv1D handling in Model decomposer sources; ArchitectureProfile only separate q/k/v/gate/up/down projs.
- **hygiene owed:** List target families (GPT-2/Phi fused) in body.
- **done when:** Fused layouts unpack to canonical roles or hard-fail; at least one fused-family fixture gated.

### #483 — model-lane: native GELU kernels (exact-erf, tanh, quickgelu)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** THIN
- **labels now:** area:engine,model-lane,triage:deferred
- **verified:** ffn_write_vectors_d supports act 0=SiLU gated, 1=erf-GELU only (model_math.cpp/h); ArchitectureProfile maps gelu_* strings to code 1 — tanh/quickgelu not distinct native kernels.
- **hygiene owed:** Clarify exact-erf already present vs missing tanh/quickgelu.
- **done when:** Native kernels for each witnessed activation variant; ResolveFfnActCode dispatches distinctly.

### #484 — model-lane: witness generation_config.json decode scalars on the recorder side
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** EMPTY
- **labels now:** model-lane,triage:deferred
- **verified:** No generation_config parse under app/Laplace.Decomposers/Model; FoundryCommands references the filename for export, not model-lane recorder witness.
- **hygiene owed:** Expand body with which scalars must be witnessed.
- **done when:** generation_config.json scalars deposited on recorder layer when present; absent file is explicit.

### #485 — model-lane: verify the WordPiece path through LlamaTokenizerParser vs MiniLM vocab.txt
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** EMPTY
- **labels now:** model-lane,triage:deferred
- **verified:** LlamaTokenizerParser has ## WordPiece surface handling (lines 242/262) but no ModelGate_TOK_WORDPIECE_MiniLM (or vocab.txt path test) exists.
- **hygiene owed:** Needs fixture path + expected token ids in body.
- **done when:** ModelGate_TOK_WORDPIECE_MiniLM (or equiv) green against MiniLM vocab.txt.

### #542 — model-lane: HasBiases is one boolean per profile — cannot express per-projection bias presence (Qwen2)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,model-lane,triage:deferred
- **verified:** ArchitectureProfile.cs still has single `bool HasBiases`; Qwen2 profile sets HasBiases=true with no per-projection (q/k/v vs o/FFN) slots. No Norm/bias-presence map added.
- **hygiene owed:** Deferred label; concrete Qwen2 mismatch still accurate.
- **done when:** Profile can witness per-projection bias presence; Qwen2 qkv-bias / no-o_proj-bias / no-FFN-bias expressible without lying.

### #543 — model-lane: Bert profile FinalNorm points at embeddings.LayerNorm.weight — embedding norm misfiled as final norm
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,priority:normal,model-lane,triage:deferred
- **verified:** ArchitectureProfile.Bert still has FinalNorm="embeddings.LayerNorm.weight" AND EmbeddingNormWeight set to the same path (lines 209-214). Embedding norm role added but FinalNorm not corrected.
- **hygiene owed:** Line citation stale (was :190); defect still live.
- **done when:** FinalNorm null or true final norm for BERT; embedding LN only in EmbeddingNorm* roles.

### #544 — model-lane: norm placement/topology (pre-LN / post-LN / sandwich / parallel) is not a witnessed field
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,substrate-law,model-lane,triage:deferred
- **verified:** ArchitectureProfile has no NormPlacement/topology field; For(string) only switches family skeletons. BERT vs Llama remain indistinguishable on placement.
- **hygiene owed:** substrate-law label; still unwitnessed architecture fact.
- **done when:** Witnessed placement enum (pre/post/sandwich/parallel) on profile; BERT post-LN ≠ Llama pre-LN in export/audit.

### #545 — model-lane: the phi profile is Phi-1/2 (mlp.fc1/fc2) — Phi-3 tensor names mismap under the same model_type
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** type:bug,priority:normal,priority:low,model-lane,triage:deferred
- **verified:** ArchitectureProfile.For("phi") => Phi with mlp.fc1/fc2 and self_attn.dense; no Phi-3 branch. Same coarse model_type dispatch as #481 (_ => Llama fallback).
- **hygiene owed:** dup_of #481 root (coarse model_type); keep open as concrete Phi-3 mismap.
- **done when:** Phi-3 configs map to correct tensor names (or refuse); Phi-1/2 path unchanged.

### #552 — engine: move recipe canonicalization to native, gated on proving byte-identical output
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** STRONG
- **labels now:** area:engine,substrate-law,model-lane,triage:deferred
- **verified:** LlamaRecipeExtractor.CanonicalizeJson still managed (comment: stays managed on purpose); ModelConfigReader also has managed CanonicalizeJson. No engine/synthesis verbatim-preserving canonicalizer gate found.
- **hygiene owed:** Identity-affecting; correctly deferred until byte-identical gate.
- **done when:** Native canonicalize byte-identical to managed over corpus; then switch + delete managed path.

### #793 — Campaign: model-lane honesty sweep (pillar 2 witnessing defects)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** ADEQUATE
- **labels now:** priority:normal,model-lane,triage:active,triage:deferred
- **verified:** Campaign parent; children #543/#545 still OPEN in this batch; For(string) still silent Llama fallback (_ => Llama). Not a closable epic until checklist clears.
- **hygiene owed:** triage:active+deferred dual labels; refresh checklist against code.
- **done when:** All listed child witnessing defects fixed or closed with evidence.

## Rank 4

### #259 — Refactor C# app to engine-orchestration shape (delete reinventions, push byte-level work to C/C++)
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STALE
- **labels now:** area:app,priority:normal,epic,triage:active
- **verified:** RecipeInfo/WriteGgufMetadata/HfToGgmlName still in app (FoundryCommands.cs); RelationTripleDecomposerBase still used; Containers.Abstractions still present.
- **hygiene owed:** Refresh story table against landed vs residual children (#264/#272/#273 still open).
- **done when:** Child stories closed; C# holds orchestration only per ADR 0027 for model/codec path.

### #49 — Story 3.11 — Cross-verification (perf-cache vs DB seed)
- **status:** `OPEN_DEFECT` · **axis:** IDENTITY · **body:** ADEQUATE
- **labels now:** chunk-3,area:engine,story,module:laplace_substrate,module:engine_core,triage:active
- **verified:** engine/core/CMakeLists.txt:267 laplace_verify_perfcache_determinism only re-emits blob and compare_files to itself — no DB entities/physicalities field crosswalk.
- **hygiene owed:** Retarget acceptance at existing CMake target vs still-missing DB↔blob sibling check.
- **done when:** ctest/just verify compares per-codepoint hash128/coord/hilbert between UnicodeSeed DB rows and mmap blob; first mismatch prints CP+field.

### #264 — Recipe lane residual: RecipeInfo DTO + managed canonicalization (duplicate JSON parser is gone)
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** STRONG
- **labels now:** area:app,priority:normal,story,module:engine_synthesis,triage:active
- **verified:** LlamaRecipeExtractor.cs still exists; Parse uses RecipeParse but returns RecipeInfo; FoundryCommands/ModelDecomposer still take RecipeInfo.
- **hygiene owed:** Title already updated — keep acceptance focused on DTO deletion + #552 canonicalize gate.
- **done when:** No LlamaRecipeExtractor/RecipeInfo in app/; call sites use recipe_t* accessors; build+tests green.

### #50 — Story 3.12 — Cross-machine determinism CI check
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** chunk-3,area:engine,story,module:laplace_substrate,module:engine_core,triage:active
- **verified:** laplace_verify_perfcache_determinism exists locally; .github/workflows/*.yml have no verify-perfcache / cross-machine cmp job.
- **hygiene owed:** Link to #49 sibling gate; specify artifact path + CI job name.
- **done when:** CI runner blob cmp byte-identical to pinned reference for same UCD+UCA; #49 green on that runner.

### #115 — CI: wire scripts/model-synthesize-ci.sh into .github/workflows/laplace.yml as a self-hosted job
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** THIN
- **labels now:** chunk-8,area:engine,area:app,area:ci,story,triage:active
- **verified:** scripts/model-synthesize-ci.sh present; grep of .github/workflows/laplace.yml shows no model-synthesize/roundtrip job.
- **hygiene owed:** Body cites integration.yml — point at laplace.yml (or actual workflow) and self-hosted runner labels.
- **done when:** Self-hosted job runs model-synthesize-ci.sh on main push; green.

### #165 — D.7 — CI verification of MKL/TBB linkage
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** THIN
- **labels now:** area:ci,priority:normal,story,triage:active
- **verified:** No ldd/MKL/TBB symbol grep in .github/workflows/*.yml; core CMakeLists has no MKL link (good) but CI does not assert it.
- **hygiene owed:** Name workflow job + exact ldd patterns for dynamics vs core.
- **done when:** CI fails if liblaplace_dynamics lacks MKL/TBB or liblaplace_core gains them.

### #352 — converse.sql regress timing anomaly — ~20-24s wall-clock, sub-20ms server-side execution (unresolved)
- **status:** `UNVERIFIABLE` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** area:infra,type:bug,priority:normal,triage:active
- **verified:** Body scopes hart-desktop client/protocol lag; this Linux host was not used to reproduce wall-clock vs EXPLAIN gap.
- **hygiene owed:** Add reproduce matrix (desktop vs server) and close if env-only with workaround doc.
- **done when:** Root cause named; fixed or documented env-specific with workaround.

### #382 — Perfcache codegen Wave 1 remainder: C# valet routing + highway source-hash header parity
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STALE
- **labels now:** area:ci,area:infra,type:enhancement,priority:normal,triage:active
- **verified:** CMake DEPENDS + .gitignore:414-417 blobs done; no C# perfcache valet emit path; highway include has no source_hash parity header like modality/chess formats.
- **hygiene owed:** Drop steps 2–3 from open list; keep valet + highway source-hash only.
- **done when:** C# valet can regenerate T0 perfcache; highway blob has source-hash no-op gate parity with T0.

### #433 — verified at-scale runs: UD, Tatoeba, chess-ANALYZE profile
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** THIN
- **labels now:** ingest,tracker-migration,triage:active
- **verified:** ingest_run_journal: UDDecomposer status=running (not completed); no Tatoeba text source in source_counts_approx; ChessAnalysis ≈30.7M present but ANALYZE profile wall-clock not proven complete.
- **hygiene owed:** Track three separate seed-step log proofs; note UD currently mid-write (one-ingest rule).
- **done when:** seed-step (or ingest_runs) shows UD + Tatoeba + chess-ANALYZE finished ok with wall-clock and row counts.

### #495 — chess: live end-to-end verification debt (FIX 2/4/5/8/9, chess-books ingest, cutechess auto-ingest)
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** THIN
- **labels now:** area:app,ingest,triage:active
- **verified:** Chess SQL surface is rich (api('chess') → 25 helpers). ingest_runs shows ChessOpenings ok 2026-08-05. No evidence in this verification that FIX 2/4/5/8/9, chess-books, or cutechess auto-ingest were live-proven; #433 scoped elsewhere per body. Debt remains process/proof, not missing API names.
- **hygiene owed:** needs concrete acceptance checklist per FIX id
- **done when:** Each named FIX and books/cutechess path has a recorded live proof (row counts + helper outputs) on this DB after ingest.

### #532 — extension: five bootstrap/final signature collisions in manifest.install — the later definition silently wins
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STALE
- **labels now:** area:extension,type:bug,triage:active
- **verified:** manifest.install still loads senses/define(+with_context) bootstrap at :145-148 and finals at :225-228 (same CREATE OR REPLACE signatures; with_context files name overloads of define/senses). examples only once. Latent dual-body risk unchanged; live functions exist as overloads.
- **hygiene owed:** names are overloads not five distinct function names; rewrite
- **done when:** Single definition path per signature (no bootstrap/final overwrite), or CI diff proves bodies identical.

### #657 — ops: make ops.app_log production-live — shared log dir + logrotate + repoint on deploy
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STRONG
- **labels now:** triage:active
- **verified:** ops.app_log()/repoint_app_log exist. Live: LAPLACE_OPS_LOG_DIR unset; no /etc/logrotate.d/laplace*; SELECT count(*) FROM ops.app_log() = 0.
- **hygiene owed:** Host-gated; SQL surface alone insufficient.
- **done when:** Shared LAPLACE_OPS_LOG_DIR; logrotate copytruncate; deploy repoint; app_log() returns real rows.

### #660 — ops/ui: modality_counts has no 'documents' count — documents modality card can't render
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** triage:active
- **verified:** modality_counts() still RETURNS (text_evidence, chess, models, multilingual) — no documents. Live api('modality') confirms. Documents still UserPrompt lane (#754).
- **hygiene owed:** Blocked on document-lane identity (#754/#799).
- **done when:** documents column from structural entity/stream count.

### #747 — regress fixtures hand-transcribe the write path — generate them from the emission path instead
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STRONG
- **labels now:** triage:active
- **verified:** chess_read.sql still hand DO $$ … INSERT INTO attestations blocks (multiple). Not generated from ChessPgnDecomposer emission.
- **hygiene owed:** Law #6 duplication; still accurate.
- **done when:** Regress fixtures produced by real emission path; reads assert contract not replica.

### #755 — eval: W5 harness landed (runner+probes+CI); remaining acceptance needs seeded box + quality regress gate
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** type:enhancement,priority:high,triage:completion-axis
- **verified:** scripts/eval-generation.py + eval-probes.json + verify-generation.py wired in laplace.yml. eval-baselines.json: blocking_flip_date=null, advisory_until 2026-08-10 — quality gate not yet blocking.
- **hygiene owed:** Title accurate: harness landed, remaining acceptance open.
- **done when:** Blocking quality regress on seeded fixture; latency budgets; held-out continuation scores.

### #761 — campaign: seed the corpus through finished lanes, re-scoring the eval harness per wave
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** THIN
- **labels now:** task,ingest,triage:completion-axis
- **verified:** Blocked by #754/#755 by design. Foundation+lexical largely seeded; document lane unfinished; UD mid-run. Campaign tracking still valid.
- **hygiene owed:** Campaign umbrella; not closable until child lanes finish.
- **done when:** Quality scores rise across two seeded waves without latency budget breach.

### #791 — Epic: monetization / sell pillar — Stripe self-provision, API keys, tenant isolation, metering
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** priority:high,epic,triage:active
- **verified:** Substantial code: StripeCatalogSync, BillingBootstrap, ApiKeyService/EnforcementMiddleware, ApiKeyTenantResolver, credit tests. Program.cs still notes spoofable header partition for header mode. finished-v1 acceptance (spoof-proof tenant + meters) not fully proven as closed epic.
- **hygiene owed:** Epic; children partially landed — keep open until acceptance checklist green.
- **done when:** Install→catalog→key auth→unspoofable tenant→meters tick.

### #805 — source kind: corpus / runtime / derived, attested at bootstrap — 'is UserPrompt ingested' is a category error
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** triage:active
- **verified:** source_status returns ingested/known/evidence — no kind column (live UserPrompt: ingested=t, evidence=0). SourceVocabularyBootstrap has no SourceKind/corpus|runtime|derived. api('source_kind') empty.
- **hygiene owed:** Choose IS_INSTANCE_OF vs new bit before coding.
- **done when:** Every bootstrapped source has kind; UserPrompt reports runtime without meaningless ingested-as-corpus; undeclared kind fails gate.

### #811 — EPIC: standardization — every substrate question is a typed operation; hand-written SQL is a missing tool
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STRONG
- **labels now:** triage:active
- **verified:** Partial children landed (#812 op, #814 refuse/log) but epic done-means unmet: no build gate that every installed ops fn is a tool; atom_census/source_tier_census/mcp_lane still missing (#813); C# still hand-rolls substrate SQL (#909). Epic principle unenforced.
- **hygiene owed:** Children #812–#814; W16 read-path reinvented-wheel class.
- **done when:** Typed ops both surfaces; ops-without-tool fails build; sql gaps tracked; no ad-hoc SELECT on hot tables for ops questions.

### #813 — the question inventory: operations that do not exist yet — atom_census, source_tier_census, surface_sample, mcp_lane, drop ledger
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STRONG
- **labels now:** triage:active
- **verified:** api('atom_census'|'source_tier'|'surface_sample'|'mcp_lane'|'drop_ledger') all empty. compositional_tier_distribution exists (not source-scoped). ChessDropLedger remains log-line only.
- **hygiene owed:** First job still owed: declare N for atom window readably.
- **done when:** Named ops installed for each listed question; gateable atom_census.

### #814 — the sql hatch must refuse what the typed surface can answer, and log every use as a gap report
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** triage:active
- **verified:** ExecuteSql refuse path + mcp-sql-gap stderr log landed; third acceptance (queryable gap ledger) not present.
- **hygiene owed:** Keep open: refuse+log landed; queryable gap ledger via op() still owed.
- **done when:** Refuse covered tables; log accepted sql; gap log queryable via op — 2/3 landed.

### #834 — chess: drive/measure protocol — use observational pass (fold+openings+explore), not Stockfish-shaped demo as floor
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** STRONG
- **labels now:** area:app,type:enhancement,substrate-law,triage:active
- **verified:** chess-lab.md demotes cutechess/SF floor; stamped Elo/observational acceptance run not found.
- **hygiene owed:** Guides demote SF demo; keep open until stamped observational measurement protocol is exercised once.
- **done when:** Guide protocol + caveats (landed); stamped measurement (owed).

### #847 — CI swallows shell and workflow errors in at least five places — add a shellcheck gate
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** No shellcheck CI job/gate in .github/workflows (only inline disable comments). wait-for-quiet-substrate.sh documents prior || echo 0 failure mode as fixed closed, but broader shellcheck + || echo 0 reject gate not present. laplace.yml still has set +e / || true patterns in places.
- **hygiene owed:** Related #841.
- **done when:** shellcheck in CI; grep reject || echo 0 / silenced guard results.

### #121 — Epic D — MKL / Eigen / Spectra / TBB integration + determinism
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** STALE
- **labels now:** area:engine,priority:normal,epic,triage:active
- **verified:** dynamics CMake links MKL+TBB when found; init.cpp sets MKL_CBWR; LAPLACE_TARGET_ISA in engine/CMakeLists.txt — but #164/#165 gates still absent.
- **hygiene owed:** Mark D.4/D.5 landed in code; keep epic open on D.6/D.7 only.
- **done when:** All child stories closed: CBWR init, ISA option, thread-count determinism ctest, ldd CI gate.

### #164 — D.6 — Determinism ctest — same output across thread counts
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** area:engine,area:ci,priority:normal,story,triage:active
- **verified:** engine/dynamics/tests/test_procrustes.cpp has correctness tests but no TBB_NUM_THREADS={1,2,4,8} byte-identical assertion.
- **hygiene owed:** Expand body with exact ctest target name and input fixture.
- **done when:** ctest runs Procrustes at TBB_NUM_THREADS 1/2/4/8; outputs byte-identical or fails.

### #367 — render_native: bulk-fetch render path (zero per-node SPI calls) for converse's serving hot path
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** area:extension,type:enhancement,priority:normal,priority:low,module:laplace_substrate,triage:deferred
- **verified:** No render_native symbol; converse_compose.sql.in still calls render_text per entity; realize_batch already used in chat/infer paths.
- **hygiene owed:** Clarify vs existing realize_batch batching; scope remaining per-node render_text sites.
- **done when:** Converse hot path batch-resolves labels in one SPI round-trip; measured latency drop documented.

### #385 — Model-lane perf: owed measurements (per-phase split, pack-throughput microbench, TinyLlama first timed run, VTune pass, readback-at-scale)
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** area:engine,area:app,type:enhancement,priority:normal,triage:active
- **verified:** No ModelGate/timed TinyLlama gate or pack-throughput microbench in app/Laplace.Decomposers.Tests; only ModelGateFactorReadbackTests exists — measurements still unpaid.
- **hygiene owed:** Replace scratchpad ledger pointers with concrete command/log paths that prove each of the five numbers.
- **done when:** Five named measurements checked into repo or CI logs with host, corpus, and wall times.

### #403 — walk_branches: profile the dominant cost at scale (SPI replan vs qsort/HTAB vs repalloc)
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** read-side,perf,tracker-migration,triage:deferred
- **verified:** walk_branches installed; generate_walk.c now uses qsort (not O(n^2) insertion sort) but no checked-in profile identifying dominant cost at scale.
- **hygiene owed:** Update body: insertion-sort claim is stale; keep profiling ask with a specific depth/breadth workload.
- **done when:** Profile artifact naming top cost component with wall% on a stated corpus size; follow-up issue or fix scoped to that component.

### #412 — evidence/statistics endpoints: extension-side residuals (multi_source_entity_count; retire API inline SQL after deploy)
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** STALE
- **labels now:** read-side,perf,tracker-migration,triage:deferred
- **verified:** multi_source_entity_count() exists but is full GROUP BY DISTINCT scan over attestations (pg_get_functiondef); ReadTopRelationsAsync now calls NpgsqlSubstrateReads.TopRelationsAsync (inline SQL retired).
- **hygiene owed:** Close (b); keep (a)/(c) with current function defs; drop 'after deploy' language.
- **done when:** Honest multi_source estimator or documented exact-only API; evidence_receipt source labels not re-rendered per claim-group hot path.

### #470 — read-side: explicit MATERIALIZED / NOT MATERIALIZED decision across the serving CTEs
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** read-side,perf,triage:deferred
- **verified:** Many serving SQL files use MATERIALIZED (chat/converse/salient/chess) but no repo-wide explicit decision covering all CTE-bearing serving functions.
- **hygiene owed:** Needs audit checklist + policy doc; not a single code fix.
- **done when:** Policy + audit: every serving CTE declares MATERIALIZED/NOT MATERIALIZED with rationale; CI or checklist gate.

### #497 — chess: per-ply DB apply behind one process-wide _writeGate serializes parallel lab games
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** area:app,perf,triage:deferred
- **verified:** ChessLiveGameHost still has SemaphoreSlim _writeGate (lines 24,88,125,213). MatchRunner.cs:193-213 still GetAwaiter().GetResult() on OpenGame/plies/close through that host. Process-wide serial apply unchanged.
- **hygiene owed:** line cites drifted (gate in LiveGameHost not MatchRunner:194)
- **done when:** Parallel lab games apply without one process-wide mutex; barriers explicit; no sync-over-async GetResult on the hot path.

### #498 — ingest: GrammarRowReader.FeedChunkFields allocates a string[] per row for all tabular ingest
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** ingest,perf,triage:deferred
- **verified:** GrammarRowReader.cs:106-137 still builds `new string[spans.Count]` per row and UTF8.GetString per field. Unchanged allocation shape.
- **hygiene owed:** needs measured before/after
- **done when:** Tabular ingest path avoids per-row string[] (spans/bytes) with allocation evidence on a hot source.

### #502 — perf: refactor-audit phase 2 — EXPLAIN pass + VTune/allocation evidence + deep audit of Endpoints/Chess/web/grammar_compose.cpp
- **status:** `NEEDS_SPEC` · **axis:** PERF · **body:** THIN
- **labels now:** perf,triage:deferred
- **verified:** Meta audit campaign, not a single falsifiable defect. No code change owed until scoped measurements exist. Cannot mark fixed/open as a unit.
- **hygiene owed:** split into measurable issues or close as tracker
- **done when:** Operator-scoped measurement plan with named hot paths and recorded EXPLAIN/VTune artifacts; then file concrete defects.

### #517 — read-side: native SPI adjacency walk to retire relation_rank_resolved (~27us/call)
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** area:extension,read-side,perf,triage:deferred
- **verified:** api('relation_rank') still returns relation_rank + relation_rank_resolved. No native SPI adjacency replacement on ranking path. Perf endgame unbuilt.
- **hygiene owed:** re-measure 27us claim before optimize
- **done when:** Ranking path uses native SPI+perfcache walk; relation_rank_resolved retired or not on hot path; measured.

### #526 — model-lane: build the factor perfcache blob (spec 33 table row / spec 19 candidate b)
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** area:engine,perf,model-lane,triage:deferred
- **verified:** api('factor') empty; factor pack/unpack in mantissa.c exists; no factor perfcache blob/loader parallel to t0/highway/chess. Native bulk-mmap over physicalities still absent.
- **hygiene owed:** ok
- **done when:** Factor perfcache blob built, loaded, CRC'd per spec 33; model reads use it.

### #536 — model-lane: B-prime firefly-lens re-measurement owed at real logit scale
- **status:** `UNVERIFIABLE` · **axis:** PERF · **body:** THIN
- **labels now:** priority:low,spike,model-lane,triage:research
- **verified:** Measurement debt / research rerun. No artifact in repo or DB proves or falsifies completion; cannot verify from code/DB alone without the measurement log.
- **hygiene owed:** needs attached measurement path
- **done when:** Recorded logit-scale softmax-KL/recall ladder + hd=32→4 lens numbers checked in.

### #578 — chess: ChessCompose.Gate global lock serializes position-id composition across all concurrent games/requests
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** area:app,perf,triage:deferred
- **verified:** ChessCompose.Gate => LaplaceCoreGate.Native still; lock(ChessCompose.Gate) remains in EngineService, StateValuer, RootBias, LearnedPst, PositionRef, Replay, Trajectory, Openings, Analyze paths.
- **hygiene owed:** triage:deferred; profile-first still owed.
- **done when:** Gate scoped/sharded with measured lab ladder; no process-wide serialize on compose.

### #588 — write path: 5 substrate perf/consolidation gates RED on main against the live DB — entity apply 126k rows/s vs 500k gate
- **status:** `UNVERIFIABLE` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** type:bug,priority:normal,perf,triage:ops-blocked
- **verified:** SqlConsolidation suspect: consensus_upsert installed (prosrc=pg_laplace_consensus_upsert). Throughput gates not re-run — UDDecomposer status=running (one-ingest law). Entity/physicality/warm-reingest rates unknown on current 79M-attestation box.
- **hygiene owed:** ops-blocked; remeasure after UD completes; IsInstalled half likely green now.
- **done when:** Writer/entity/physicality/warm-reingest gates green on live DB; ConsensusUpsert_IsInstalled green.

### #617 — read-side: inlining audit — SET search_path on LANGUAGE sql functions in per-row/per-step hot positions
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** STRONG
- **labels now:** triage:active
- **verified:** Hot fixes landed: senses, consensus_walk_edges, consensus_step_edge, consensus_subject_edges document No SET. Still SET: entity_exists, type_label, relation_in_family (and other labeling scalars per prior sweep).
- **hygiene owed:** Partial completion; refresh ranked table in body.
- **done when:** Named hot functions drop SET/STRICT; live EXPLAIN/MCP proof per #616 playbook.

### #822 — chess: position_id → coord perfcache floor (like t0) — not Syzygy mmap, not ECO-only blob
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** area:app,perf,triage:deferred
- **verified:** Code path exists (ChessPositionFloor, chess_position_table_load, GUC, NativeInterop) and docs/specs/33 list blob — but live laplace_chess_position_ready()=false; no measured shape-peer win vs heap realize on this host.
- **hygiene owed:** Depends #821/#820; deferred label matches incomplete landing.
- **done when:** Blob law + measured win + not Syzygy-as-ROM; ready() true under deploy.

### #839 — chess compose: 14.3 µs per ply is ~40x off — two board clones, a 200-char string and 35x over-generation
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** ChessModality.Apply still Board.Clone + CanonicalKey(nb) string + ImmutableList history (ChessModality.cs:61-75). Needs #838 reseed identity decision for delta/Zobrist path.
- **hygiene owed:** Partial #836 landings noted in body; core Apply cost remains.
- **done when:** Compose µs/ply in competent movegen class without text surface keys on hot path.

### #860 — SET search_path on 232 SQL functions blocks inlining and kills the index path
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** rg finds ~241 .sql.in files / ~250 SET search_path occurrences under extension SQL — still pervasive. consensus_step_edge correctly omits SET for inlining (#617/#871) as the lawful counterexample.
- **hygiene owed:** Structural driver #862 flat schema.
- **done when:** Hot-path functions inlinable (no SET search_path / no STRICT where it kills index); census down from hundreds.

### #907 — converse_compose renders per token; apply the batched form from converse_walk
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** converse_compose.sql.in:256 still string_agg(COALESCE(realize(t), render_text(t,64), '')) over unnest(out_ids) — scalar per token. converse_walk documents batched fix.
- **hygiene owed:** On chat walk shape path.
- **done when:** One realize_batch/render_text_batch pair joined by ordinal.

### #908 — Surviving OR-joins in converse_tiered and consensus_peer; unfenced relation_in_family join qual
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** converse_tiered.sql.in:223-224 still both-directions OR join; relation_in_family at :140 as join qual. consensus_peer.sql.in:52-53 still disjunctive OR. #871 rewrite pattern not applied here.
- **hygiene owed:** Sibling of fixed #871.
- **done when:** UNION ALL / two-probe form; relation_in_family fenced or rewritten.

### #910 — Tier descent pays per-node P/Invoke under a global lock; apply re-parses native PGCOPY blobs — two native API gaps
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** TierTree.GetNode still lock(LaplaceCoreGate.Native) + per-node FFI (TierTree.cs:101-109). Hot descent callers unchanged in structure.
- **hygiene owed:** Ingest hot path; FFI-shape not algorithm duplication.
- **done when:** Batch native node views; apply consumes native buffers without re-parse.

### #911 — plpgsql census follow-ups: per-tier re-unnest in entities_present_ordinals; ord assignment race in sibling
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** entities_present_ordinals.sql.in:24-31 still FOR v_tier LOOP with unnest(p_ids,p_tiers) WHERE u.t=v_tier per iteration — quadratic re-unnest echo of failed form documented in sibling probe.
- **hygiene owed:** Census found zero unjustified RBAR elsewhere; these two remain.
- **done when:** Unnest-once temp-table idiom; ord race fixed.

## Rank 5

### #363 — Drop dead attestations.highway_mask column (native tuple layout cleanup)
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STALE
- **labels now:** area:extension,type:enhancement,priority:low,module:laplace_substrate,triage:deferred
- **verified:** attestations.sql.in:40 still has highway_mask; live sample all non-null; NpgsqlSubstrateWriter.cs still COPY-writes AttestationRow.HighwayMask — column not read-side dead yet.
- **hygiene owed:** Body says unused — update: still written on ingest; drop requires writer+schema+reseed plan.
- **done when:** Column dropped after writer stops emitting it; no readers; greenfield reseed or documented migration.

### #381 — Naming/terminology consolidation: ranking-signal, score-family, model-lane-anatomy, system-partition names (doc 21 Section 8)
- **status:** `NEEDS_SPEC` · **axis:** DEBT · **body:** THIN
- **labels now:** area:docs,type:docs,priority:low,triage:deferred
- **verified:** Docs/debt umbrella; no single code defect — naming clusters across code/docs/SQL.
- **hygiene owed:** Split per cluster or attach canonical glossary PR checklist.
- **done when:** Each of 8 clusters has one canonical name applied across code/docs/SQL.

### #465 — extension: expose math4d_distance_sq as a scalar; retire laplace_l2_sq
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** THIN
- **labels now:** area:extension,triage:active
- **verified:** math4d_distance_sq absent from pg_proc; laplace_l2_sq present; engine math4d.c has log/exp/karcher but no SQL scalar exposure of distance_sq.
- **hygiene owed:** Confirm callers of laplace_l2_sq before retire.
- **done when:** math4d_distance_sq SQL scalar installed; laplace_l2_sq removed or wrapper; one distance definition.

### #468 — read-side: walk_text.sql.in reinvents hash128_lo via encode(...)::bit(64)::bigint
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** THIN
- **labels now:** read-side,triage:active
- **verified:** walk_text.sql.in:23-25 still encode(substring(laplace_hash128_blake3(...))::bit(64)::bigint); hash128_lo not in laplace schema.
- **hygiene owed:** Trivial; keep type:bug or chore.
- **done when:** walk_text uses one shared lo64 helper; no open-coded encode/bit cast.

### #471 — read-side: audit ~30 bytea-id-returning serving functions for 'id WITH label' vs 'id INSTEAD of label'
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** THIN
- **labels now:** read-side,triage:active
- **verified:** Surface still mixed (e.g. structural_neighbors_of returns neighbor_id+neighbor text; walk_branches returns entity_id only) — convention undeclared.
- **hygiene owed:** Produce the inventory table in the issue body.
- **done when:** Documented convention + audited list; outliers fixed or justified.

### #492 — chess: C15 test-weakness cluster — ChessTactics untested, decomposers lack IIngestInventoryProvider, e2e lab test asserts nothing
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** THIN
- **labels now:** area:app,triage:active
- **verified:** ChessTactics.cs exists; zero Chess.Tests references. Chess PGN/Syzygy/Openings/Book decomposers NOW implement IIngestInventoryProvider (partial fix). No Chess*e2e* tests; ChessLabPathsTests only path resolution. Cluster not closed.
- **hygiene owed:** split or rewrite body — inventory half largely landed
- **done when:** ChessTactics has non-vacuous unit tests; lab e2e asserts substrate outcomes; inventory claim removed or verified for remaining chess lanes.

### #499 — area:app: split the monoliths — FoundryCommands 2218 / FoundryExport 2059 / CpuTopology 1517 / ModelTokenEdgeETL 1203 LOC
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** THIN
- **labels now:** area:app,triage:active
- **verified:** wc -l now: FoundryCommands 2195, FoundryExport 1721, CpuTopology 1517, ModelTokenEdgeETL 1283 — still monolith-scale. No structural split landed.
- **hygiene owed:** LOC numbers stale; update or drop exact counts
- **done when:** Named files split below agreed LOC/cohesion bars with clear module boundaries; no behavior change gates red.

### #507 — docs: promote the standing candidate Rule #13 (STABLE-in-filter / non-inlined chains) into the numbered ruleset
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** ADEQUATE
- **labels now:** area:docs,priority:low,substrate-law,triage:active
- **verified:** docs/specs/06_Engineering_Ruleset.txt:243 still '(Standing candidate Rule #13.)'; numbered list ends at #12. Promotion not done.
- **hygiene owed:** docs-only
- **done when:** Rule #13 numbered in 06 with binding text; candidate wording removed.

### #530 — read-side: entity_count(type) helper missing — three C# copies of the same count query
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STALE
- **labels now:** area:extension,priority:low,read-side,triage:deferred
- **verified:** api has entity_type_counts() (all types) and multi_source_entity_count; no entity_count(type) scalar. NpgsqlSubstrateReader.CountEntitiesByTypeAsync still open-codes count(*) WHERE type_id=$1 (:51-57). EntityCountsByTypesAsync comment admits no installed entity_counts_by_types yet.
- **hygiene owed:** partial progress — update body for entity_type_counts
- **done when:** Installed entity_count(type)/counts_by_types with regress pin; C# sites call it.

### #531 — billing: TryConsumeCredit FOR UPDATE + jsonb_set CTE belongs in an app.consume_credit() function
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** ADEQUATE
- **labels now:** area:app,priority:low,triage:deferred
- **verified:** api('consume_credit') empty; pg_proc has no consume_credit. PostgresBillingEntitlementStore.cs:121-144 still inline FOR UPDATE + jsonb_set CTE.
- **hygiene owed:** ok
- **done when:** app.consume_credit() (or laplace equivalent) owns the CTE; C# calls it; pinned test.

### #533 — read-side: api() is blind to public.* functions, has no public/internal flag, and cannot see C#-side substrate operations
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STRONG
- **labels now:** area:extension,read-side,triage:active
- **verified:** api.sql.in:10 filters n.nspname = current_schema() only. Live: laplace_frechet_4d / laplace_hilbert_encode in public but api() blind. No public/internal flag; C# ops invisible.
- **hygiene owed:** ok
- **done when:** api() lists load-bearing public.* and marks visibility; documented map for C#-only ops or they gain SQL façades.

### #534 — scripts: substrate-inference-demo.sql reimplements id hashing and codepoint enumeration instead of calling the substrate
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** ADEQUATE
- **labels now:** area:infra,type:bug,priority:low,triage:active
- **verified:** scripts/substrate-inference-demo.sql:4-14 still pg_temp.cli_id / hand-rolled relation_type_id via laplace_hash128_blake3 and builds cp_map by enumerating codepoints — bypasses word_id()/relation_type_id()/realize.
- **hygiene owed:** ok
- **done when:** Demo uses installed canonical_id/word_id/relation_type_id/realize; no hand-rolled blake3 id construction.

### #621 — P1 native: dedup drift-risk shared math (draw-rule, edge_strength, utf-8, n-gram walkers)
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STALE
- **labels now:** area:engine,tech-debt,triage:active
- **verified:** Draw-rule FIXED (aggregated_build calls outcome_from_totals_fp). edge_strength FIXED (laplace_edge_strength in spi_common.h). UTF-8 centralized in utf8.h. N-gram walkers still dual: steered_walk.c (sw_lcg/postings) vs trajectory_generate.c (splitmix/continuations).
- **hygiene owed:** Update checklist: 3/4 items done; n-gram consolidation remains.
- **done when:** One home per listed fact; n-gram walkers consolidated.

### #627 — P2 native: magic constants -> named/GUC, long-function table dispatch, redundant buffers
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** ADEQUATE
- **labels now:** area:engine,area:extension,tech-debt,triage:active
- **verified:** generate_walk.c still has empirically-untuned 2.0 geometry bonus; other cited long functions/magic constants not cleared in this pass. Debt issue, no correctness gate.
- **hygiene owed:** Spot-check confirms still open; full item audit not exhaustive.
- **done when:** Named/GUC constants; table-driven recall dispatch; stream tensors / scratch reuse; dead allocs gone.

### #764 — standardization: 358 functions, zero recorded dependencies — adopt BEGIN ATOMIC so PostgreSQL enforces the call graph
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STRONG
- **labels now:** area:extension,priority:normal,substrate-law,triage:completion-axis
- **verified:** Live: sql functions=265 with_recorded_deps=0; c=78; plpgsql=35. No BEGIN ATOMIC migration evidenced.
- **hygiene owed:** Counts drifted (247→265 sql); defect class unchanged.
- **done when:** LANGUAGE sql bodies use BEGIN ATOMIC; pg_depend edges nonzero; cycles broken first.

### #902 — Comment integrity: chat() ranking archaeology (~130 lines, mostly superseded) and stale leaf census
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** chat.sql.in still carries long superseded-ranking commentary (e.g. ~175-176 'Every commentary block above about breadth-vs-denote_mu describes that superseded ranking'). Archaeology remains in-file.
- **hygiene owed:** Docs/debt cleanup.
- **done when:** Current-law paragraph only; post-mortems in docs/git history.

### #906 — Read-path render duplication: byte-duplicate walk responder, 3 hand-rolled realize_path copies, open-coded shar
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** recall_fallback_walk.sql.in and recall_walk_response.sql.in both string_agg realize over walk_strongest (depths 6 vs 8). converse_facts still hand-rolls path render. realize_path installed elsewhere.
- **hygiene owed:** Rule #6 duplication.
- **done when:** Single walk responder; realize_path used; no open-coded duplicates.

### #909 — C# hand-rolled substrate SQL: five sites bypassing installed readers, two missing helpers
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** NpgsqlIngestOps.AttestationCountsBySourceAsync still GROUP BY attestations (lines ~230-235). NpgsqlSubstrateReader.CountEvidenceBySourceAsync still SELECT count(*) FROM laplace.attestations WHERE source_id=$1 (~358-361) despite evidence_count helpers used elsewhere in same file.
- **hygiene owed:** W16 C# read-path class; #811 epic.
- **done when:** Replace five sites with installed ops; add missing helpers or delete dead paths.

### #912 — W16 and W15 status re-verified: zero-caller set unchanged, radius_origin duplication live, SET-clause debt
- **status:** `OPEN_DEFECT` · **axis:** DEBT · **body:** STRONG
- **labels now:** (unlabeled)
- **verified:** physicalities.sql.in:38 still sqrt(ST_X^2+...) generated column, not laplace_radius_origin. rg finds no sql.in callers of laplace_radius_origin/centroid_4d/hilbert_encode/distance_4d/dwithin_4d — zero-caller set still empty of callers. SET search_path debt still ~250 occurrences (#860).
- **hygiene owed:** Status tracker; close only when W16 8.3 lands.
- **done when:** Zero-caller natives used or retired; radius_origin duplication gone; SET census addressed.

### #380 — doc 18 Q1-Q5 — Open design questions (WSD operator mechanics, RoPE/role-binding, cross-refutation operator, prose claim-extraction, temporal validity)
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** ADEQUATE
- **labels now:** area:extension,type:enhancement,priority:normal,spike,module:laplace_substrate,triage:research
- **verified:** Bundle of five design questions with no implementation surface; Q6 split to #379.
- **hygiene owed:** Keep research; spawn stories only after decisions.
- **done when:** Each Q1–Q5 resolved to design decision or explicit deferral with rationale.

### #467 — engine: decide the fate of karcher_mean / log_s3 / exp_s3 — test-only callers today
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** THIN
- **labels now:** area:engine,design-decision,triage:decision
- **verified:** math4d_karcher_mean/log_s3/exp_s3 live in engine/core/src/math4d.c with test_math4d.cpp callers; no SQL exposure; fate undecided.
- **hygiene owed:** Decision: expose per spec 09 or delete; triage:decision.
- **done when:** Either public API + regress, or removed from engine with tests deleted.

### #510 — spike: formalize attention-as-SELECT (RASP) in SQL terms
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** ADEQUATE
- **labels now:** area:docs,priority:low,spike,triage:research
- **verified:** docs/specs/09_Substrate_LM_Synthesis.txt:183-185 still 'SQL formalization not yet done'. Research spike; no code depends on it.
- **hygiene owed:** triage:research correct
- **done when:** Written SQL/RASP formalization accepted into binding docs or spike explicitly declined.

### #535 — substrate-law: write the doc 08 amendment — derivable-evidence virtualization law
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** ADEQUATE
- **labels now:** area:docs,substrate-law,design-decision,triage:decision
- **verified:** docs/specs/08_Record_vs_Calculate_Spec.txt has no derivable-evidence/virtualization amendment text (rg 0 hits). Amendment unwritten. Related #451 storage half separate.
- **hygiene owed:** docs decision
- **done when:** Doc 08 amendment landed with versioned-derivation + walk-grain journal law; cross-linked to #451.

### #823 — docs: substrate as digital processor — Gödel firmware / ISA framing (deterministic transformer slots)
- **status:** `OPEN_DEFECT` · **axis:** DESIGN · **body:** ADEQUATE
- **labels now:** area:docs,substrate-law,triage:active
- **verified:** docs/invention/transformer-slot-map.md exists and cites GH #823, but done-means 'framing page still owed' / INDEX binding map update for this framing not verified in docs/INDEX.md (no 823/firmware hit). Guides still mark STEER unfinished — prose must not overclaim; incomplete vs done-means.
- **hygiene owed:** Do not cite memories as authority.
- **done when:** INDEX binding updated; no claim contradicts live isa-gate; framing page complete.

### #862 — One flat laplace schema instead of purpose-driven schemas
- **status:** `NEEDS_SPEC` · **axis:** DESIGN · **body:** ADEQUATE
- **labels now:** (unlabeled)
- **verified:** Confirmed single laplace schema (no CREATE SCHEMA split in extension SQL). Design proposal with high migration cost; not a falsifiable code bug until schema split is specified/authorized. Explains #860 pressure.
- **hygiene owed:** Do not implement without explicit authorization — schema rewrite.
- **done when:** Spec for purpose schemas + migration/reseed cost + api() organizing axis.

### #620 — P1 foundry: move per-element linear algebra to native + bound plane top-k
- **status:** `OPEN_DEFECT` · **axis:** MODEL_EXPORT · **body:** STRONG
- **labels now:** area:engine,area:app,perf,tech-debt,triage:deferred
- **verified:** FoundryExport.ApplyPpmi still managed Math.Log; managed modified Gram-Schmidt fallback still present (~:1343); plane readers still accumulate then cap. Native GramSchmidtOrthonormalize called but fallback retained.
- **hygiene owed:** tech-debt/deferred; still accurate.
- **done when:** Per-element math via laplace_dynamics; no managed GS fallback; bounded top-k during plane build.

### #505 — campaign R5: acquire NCVEC Technician 2026-2030 + General 2023-2027 question pools
- **status:** `OPEN_DEFECT` · **axis:** OPS_CI · **body:** THIN
- **labels now:** task,triage:active
- **verified:** /vault/Data/test-data/electronics/ has ncvec-2024-2028-extra-class-pool.pdf only; no Technician 2026-2030 or General 2023-2027 files present. Acquisition task incomplete.
- **hygiene owed:** task not a code defect; keep as acquisition tracker
- **done when:** Named NCVEC Technician + General pool files present under ingest corpus path and listed for inventory.

### #168 — Story 1.14 — laplace_btree_hash128_ops opclass + benchmark vs stock bytea ops
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** chunk-1,area:extension,priority:low,story,module:laplace_geom,triage:deferred
- **verified:** Repo-wide grep finds no laplace_btree_hash128_ops / hash128_ops opclass sources.
- **hygiene owed:** Keep deferred; cite ADR 0029 section and current index in use.
- **done when:** pg_regress on 1e6 keys + microbench ≥1.5× vs stock bytea equality probes.

### #169 — Story 2.15 — laplace_gist_s3_ops opclass + benchmark vs gist_geometry_ops_nd
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** ADEQUATE
- **labels now:** chunk-2,area:extension,priority:low,story,module:laplace_geom,triage:deferred
- **verified:** No laplace_gist_s3_ops / gist_s3_ops implementation in extension tree.
- **hygiene owed:** Keep deferred; preserve note that this is access-path only not semantic NN.
- **done when:** KNN bench on 1e5 S³ entities shows ≥2× page-read reduction vs gist_geometry_ops_nd.

### #170 — Story 2.16 — laplace_sp_trajectory_ops SP-GiST opclass for trajectory prefix-match
- **status:** `OPEN_DEFECT` · **axis:** PERF · **body:** THIN
- **labels now:** chunk-2,area:extension,priority:low,story,module:laplace_geom,triage:deferred
- **verified:** No laplace_sp_trajectory_ops / sp_trajectory_ops sources in repo.
- **hygiene owed:** Note trajectory_pairs retirement (ARCHITECTURE.md) may change design — needs re-scope.
- **done when:** pg_regress proves O(depth+matches) prefix-match via SP-GiST opclass.

### #353 — astar_path: calibrated (not just admissible) geometric A* heuristic
- **status:** `OPEN_DEFECT` · **axis:** READ_ISA · **body:** ADEQUATE
- **labels now:** area:engine,area:extension,type:enhancement,priority:low,module:laplace_substrate,module:engine_core,triage:deferred
- **verified:** astar_path.c astar_geo_heuristic still returns best/ASTAR_PI (line 209); no calibrated max-hop constant.
- **hygiene owed:** Keep deferred; require measurement SQL in acceptance.
- **done when:** Documented calibration constant; regress proves optimality + fewer expansions vs /pi.

