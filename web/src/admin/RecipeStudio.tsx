import { useMemo, useState } from 'react';
import {
  Badge, Banner, Button, ErrorText, Field, Input, LoadingText,
  Modal, Muted, Panel, ReadStatus, Select, Table, Td, TextArea, useReadResource,
} from '@ui';
import { apiGet, apiPost } from '../api/client';
import { useInPlaceFullscreen } from '../explore/glome/useInPlaceFullscreen';
import { useAppStore } from '../store';
import { listIngestSources, startIngest } from './ingestControlApi';
import styles from './RecipeStudio.module.css';

interface EstateRecipe { name: string; fileName: string; relativePath: string; grammar: string; format: string; like: string; matches: string[] }
interface EstateSource {
  order: number; name: string; witness: string; witnessId: string | null; trustClass: string; held: string;
  after: string[]; reads: string[]; stagedOnDisk: boolean; stageStatus: string; matchedPath: string | null; recipes: EstateRecipe[];
}
interface EstateReport {
  cli_available: boolean; data_dir: string | null; compiled_grammars: string[]; native_grammars: string[]; sources: EstateSource[];
}
interface CatalogRecipe { name: string; source: string | null; relative_path: string; matches: string[]; content: string }
interface StudioCatalog { recipes: CatalogRecipe[] }
interface CliOutcome { ok: boolean; exit_code: number | null; timed_out: boolean; elapsed_ms: number; stdout: string; stderr: string }
interface LintSummary { name: string | null; grammar: string | null; format: string | null; has_content: boolean; has_thing: boolean; has_attest: boolean }
interface LintResult { valid: boolean; errors: string[]; warnings: string[]; summary: LintSummary }
interface InspectRecord { record_index: number; held: string[]; claims: string[] }
interface InspectResult extends CliOutcome {
  filename: string; lint: LintResult; records_count: number; claims_count: number;
  unresolved_obligations_count: number | null; compositions_count: number | null;
  records: InspectRecord[]; claims: string[]; obligations: string[];
}
interface DraftResult { recipe_dsl: string; lint: LintResult }
interface AdmitResult extends CliOutcome {
  filename: string; source: string; witness: string; trust_class: string;
  lint: LintResult; executed: boolean;
}

const DEFAULT_SAMPLE_NAME = 'sample-lexicon.tsv';
const DEFAULT_SAMPLE_TEXT = 'word\tpos\tgloss\ndog\tn\ta domesticated carnivorous mammal\nrun\tv\tmove swiftly on foot\n';
const DEFAULT_DSL = 'name sample-lexicon\nmatch *.tsv\n\ntier row by \\n\nheader\ntier field by \\t\n\nthing row word\ncontent word pos gloss\nvalue - pos\nrelation "documented definition of the word" gloss\nattest row pos gloss\n';

const stageBadgeClass = (status: string) =>
  status === 'staged_on_disk' ? styles.statusStaged : status === 'held' ? styles.statusHeld : styles.statusMissing;

export function RecipeStudio({ canIngest = false, canMaintain = false }: { canIngest?: boolean; canMaintain?: boolean }) {
  const { tenant } = useAppStore();
  const { containerRef: workbenchRef, isFullscreen, cssFullscreen, toggleFullscreen } = useInPlaceFullscreen<HTMLDivElement>();
  const estateRead = useReadResource({
    key: JSON.stringify(['ingest-estate', tenant]),
    read: (signal) => apiGet<EstateReport>('/v1/admin/ingest/estate', { tenant, signal }),
  });
  const catalogRead = useReadResource({
    key: JSON.stringify(['recipe-studio-catalog', tenant]),
    read: (signal) => apiGet<StudioCatalog>('/v1/recipe-studio/catalog', { tenant, signal }),
  });
  const liveSourcesRead = useReadResource({
    key: JSON.stringify(['recipe-studio-live-sources', tenant]),
    read: (signal) => listIngestSources({ tenant, signal }),
  });
  const admittedByName = useMemo(() => {
    const map = new Map<string, number>();
    for (const entry of liveSourcesRead.data?.entries ?? []) {
      if (entry.admitted_attestations != null)
        map.set(entry.name.toLowerCase(), entry.admitted_attestations);
    }
    return map;
  }, [liveSourcesRead.data]);

  const [query, setQuery] = useState('');
  const [stageFilter, setStageFilter] = useState<'all' | 'staged_on_disk' | 'not_staged' | 'held'>('all');
  const [planBusy, setPlanBusy] = useState<string | null>(null);
  const [ingestBusy, setIngestBusy] = useState<string | null>(null);
  const [ingestStartedMsg, setIngestStartedMsg] = useState<string | null>(null);
  const [planResult, setPlanResult] = useState<(CliOutcome & { source: string }) | null>(null);
  const [estateErr, setEstateErr] = useState<string | null>(null);
  const [maintNotice, setMaintNotice] = useState<(CliOutcome & { action: string; source?: string }) | null>(null);
  const [forgetTarget, setForgetTarget] = useState<EstateSource | null>(null);
  const [confirmSweep, setConfirmSweep] = useState(false);
  const [maintBusy, setMaintBusy] = useState(false);

  const [selectedPreset, setSelectedPreset] = useState('');
  const [filename, setFilename] = useState(DEFAULT_SAMPLE_NAME);
  const [sampleText, setSampleText] = useState(DEFAULT_SAMPLE_TEXT);
  const [recipeDsl, setRecipeDsl] = useState(DEFAULT_DSL);
  const [lintResult, setLintResult] = useState<LintResult | null>(null);
  const [inspectResult, setInspectResult] = useState<InspectResult | null>(null);
  const [admitResult, setAdmitResult] = useState<AdmitResult | null>(null);
  const [studioBusy, setStudioBusy] = useState<'draft' | 'lint' | 'inspect' | 'admit' | null>(null);
  const [studioErr, setStudioErr] = useState<string | null>(null);

  const filteredSources = useMemo(() => {
    const term = query.trim().toLowerCase();
    return (estateRead.data?.sources ?? []).filter((row) => {
      if (stageFilter !== 'all' && row.stageStatus !== stageFilter) return false;
      if (!term) return true;
      return row.name.toLowerCase().includes(term) || row.witness.toLowerCase().includes(term)
        || row.trustClass.toLowerCase().includes(term) || row.recipes.some((r) => r.name.toLowerCase().includes(term));
    });
  }, [estateRead.data, query, stageFilter]);
  const stagedCount = useMemo(() => (estateRead.data?.sources ?? []).filter((s) => s.stagedOnDisk).length, [estateRead.data]);

  async function runPlan(source: string) {
    setPlanBusy(source); setEstateErr(null);
    try { setPlanResult(await apiPost<CliOutcome & { source: string }>('/v1/admin/ingest/plan', { source }, { tenant })); }
    catch (e) { setEstateErr(e instanceof Error ? e.message : String(e)); }
    finally { setPlanBusy(null); }
  }
  async function runStartEstateIngest(source: string) {
    setIngestBusy(source); setEstateErr(null); setIngestStartedMsg(null);
    try {
      const rec = await startIngest({ source }, { tenant });
      setIngestStartedMsg(`Started ${rec.source} (pid ${rec.pid}).`);
      void liveSourcesRead.refresh();
    } catch (e) { setEstateErr(e instanceof Error ? e.message : String(e)); }
    finally { setIngestBusy(null); }
  }
  async function runForget(source: string) {
    setMaintBusy(true); setEstateErr(null);
    try {
      const res = await apiPost<CliOutcome & { source: string }>('/v1/admin/ingest/forget', { source }, { tenant });
      setMaintNotice({ ...res, action: 'forget', source }); setForgetTarget(null); await estateRead.refresh();
    } catch (e) { setEstateErr(e instanceof Error ? e.message : String(e)); }
    finally { setMaintBusy(false); }
  }
  async function runSweep() {
    setMaintBusy(true); setEstateErr(null);
    try {
      const res = await apiPost<CliOutcome>('/v1/admin/ingest/sweep', {}, { tenant });
      setMaintNotice({ ...res, action: 'sweep' }); setConfirmSweep(false);
    } catch (e) { setEstateErr(e instanceof Error ? e.message : String(e)); }
    finally { setMaintBusy(false); }
  }

  function loadRecipeIntoEditor(relativePath: string) {
    const match = catalogRead.data?.recipes.find((r) => r.relative_path === relativePath);
    if (!match) return;
    setSelectedPreset(relativePath); setRecipeDsl(match.content); setInspectResult(null);
    let nextFile = filename;
    if (match.matches.length > 0 && match.matches[0].startsWith('*.')) {
      nextFile = `sample${match.matches[0].slice(1)}`; setFilename(nextFile);
    }
    void runLint(match.content, nextFile);
  }
  async function runDraft() {
    setStudioBusy('draft'); setStudioErr(null);
    try {
      const res = await apiPost<DraftResult>('/v1/recipe-studio/draft', { filename, sample: sampleText }, { tenant });
      setRecipeDsl(res.recipe_dsl); setLintResult(res.lint); setInspectResult(null);
    } catch (e) { setStudioErr(e instanceof Error ? e.message : String(e)); }
    finally { setStudioBusy(null); }
  }
  async function runLint(dslOverride?: string, fileOverride?: string) {
    setStudioBusy('lint'); setStudioErr(null);
    try {
      setLintResult(await apiPost<LintResult>('/v1/recipe-studio/lint',
        { recipe_dsl: dslOverride ?? recipeDsl, filename: fileOverride ?? filename }, { tenant }));
    } catch (e) { setStudioErr(e instanceof Error ? e.message : String(e)); }
    finally { setStudioBusy(null); }
  }
  async function runInspect() {
    setStudioBusy('inspect'); setStudioErr(null); setAdmitResult(null);
    try {
      const res = await apiPost<InspectResult>('/v1/recipe-studio/inspect',
        { filename, sample: sampleText, recipe_dsl: recipeDsl }, { tenant });
      setInspectResult(res); setLintResult(res.lint);
    } catch (e) { setStudioErr(e instanceof Error ? e.message : String(e)); }
    finally { setStudioBusy(null); }
  }
  async function runAdmit() {
    setStudioBusy('admit'); setStudioErr(null);
    try {
      const res = await apiPost<AdmitResult>('/v1/recipe-studio/admit',
        { filename, sample: sampleText, recipe_dsl: recipeDsl }, { tenant });
      setAdmitResult(res); setLintResult(res.lint);
      if (res.ok) void liveSourcesRead.refresh();
    } catch (e) { setStudioErr(e instanceof Error ? e.message : String(e)); }
    finally { setStudioBusy(null); }
  }

  return (
    <div className={styles.studio}>
      <Panel title="Corpus estate manager" expandable actions={
        <div className={styles.toolbar}>
          <Button variant="ghost" size="sm" onClick={() => void estateRead.refresh()}>Refresh estate</Button>
          {canMaintain && <Button variant="ghost" size="sm" disabled={!estateRead.data?.cli_available || maintBusy} onClick={() => setConfirmSweep(true)}>Sweep orphans…</Button>}
        </div>
      }>
        <ReadStatus label="Corpus estate" resource={estateRead} />
        {estateRead.data && <>
          <div className={styles.metaStrip}>
            <span><strong>{estateRead.data.sources.length}</strong> sources in <code>recipes/order</code></span>
            <span><strong>{stagedCount}</strong> staged on disk</span>
            <span>CLI: <strong>{estateRead.data.cli_available ? 'ready' : 'unavailable'}</strong></span>
            <span>Data: <code>{estateRead.data.data_dir ?? 'LAPLACE_DATA unset'}</code></span>
            <span>Grammars: <strong>{estateRead.data.compiled_grammars.length}</strong> compiled + {estateRead.data.native_grammars.length} native</span>
          </div>
          <div className={styles.toolbar}>
            <Input className={styles.filterInput} aria-label="Filter corpus estate" placeholder="Filter by source, witness, trust class, or recipe…"
              value={query} onChange={(e) => setQuery(e.target.value)} />
            <Select aria-label="Filter by staging status" value={stageFilter} onChange={(e) => setStageFilter(e.target.value as typeof stageFilter)}>
              <option value="all">All statuses ({estateRead.data.sources.length})</option>
              <option value="staged_on_disk">Staged on disk ({stagedCount})</option>
              <option value="not_staged">Not staged</option>
              <option value="held">Held</option>
            </Select>
          </div>
        </>}
        {estateErr && <ErrorText role="alert">{estateErr}</ErrorText>}
        {ingestStartedMsg && <Banner variant="info">{ingestStartedMsg}</Banner>}
        {maintNotice && <Banner variant={maintNotice.ok ? 'info' : 'warning'}>
          <strong>{maintNotice.action === 'sweep' ? 'Sweep' : `Forget ${maintNotice.source}`}</strong> exited {maintNotice.exit_code ?? 'timeout'} in {maintNotice.elapsed_ms}ms:
          <pre className={styles.preBox}>{maintNotice.stdout || maintNotice.stderr || '(no output)'}</pre>
        </Banner>}
        {estateRead.data && (
          <div className={styles.tableWrap}>
            <Table>
              <thead><tr><th scope="col">#</th><th scope="col">Source</th><th scope="col">Class</th><th scope="col">Recipes</th><th scope="col">Status</th><th scope="col">Actions</th></tr></thead>
              <tbody>
                {filteredSources.map((src) => {
                  const admitted = admittedByName.get(src.name.toLowerCase());
                  return (
                  <tr key={src.name}>
                    <Td>{src.order}</Td>
                    <Td>
                      <strong>{src.name}</strong>
                      {src.witness && <span className={styles.subMeta}>witness: {src.witness}</span>}
                      {src.witnessId && <code className={styles.subMeta} title={src.witnessId}>{src.witnessId.slice(0, 12)}…</code>}
                      {src.after.length > 0 && <span className={styles.subMeta}>after: {src.after.join(', ')}</span>}
                    </Td>
                    <Td><Badge>{src.trustClass || 'Unassigned'}</Badge></Td>
                    <Td><div className={styles.recipeChips}>
                      {src.recipes.length === 0 ? <Muted>{src.reads.length ? `reads ${src.reads.join(', ')}` : '—'}</Muted>
                        : src.recipes.map((r) => <Button key={r.relativePath} variant="ghost" size="sm" onClick={() => loadRecipeIntoEditor(r.relativePath)}>{r.name}</Button>)}
                    </div></Td>
                    <Td>
                      <Badge className={stageBadgeClass(src.stageStatus)}>{src.stageStatus}</Badge>
                      {admitted != null && <Badge className={styles.statusStaged}>{admitted.toLocaleString()} attested</Badge>}
                      {src.held && <span className={styles.subMeta}>{src.held}</span>}
                      {src.matchedPath && <code className={styles.subMeta}>{src.matchedPath}</code>}
                    </Td>
                    <Td><div className={styles.toolbar}>
                      {canIngest && <Button variant="ghost" size="sm" loading={planBusy === src.name} disabled={!estateRead.data?.cli_available || planBusy !== null} onClick={() => void runPlan(src.name)}>Plan</Button>}
                      {canIngest && src.stagedOnDisk && <Button size="sm" loading={ingestBusy === src.name} disabled={!estateRead.data?.cli_available || ingestBusy !== null} onClick={() => void runStartEstateIngest(src.name)}>Start ingest</Button>}
                      {canMaintain && <Button variant="ghost" size="sm" disabled={!estateRead.data?.cli_available || maintBusy} onClick={() => { setEstateErr(null); setForgetTarget(src); }}>Forget…</Button>}
                    </div></Td>
                  </tr>
                  );
                })}
              </tbody>
            </Table>
          </div>
        )}
        {planResult && (
          <section className={styles.drawer} aria-label={`Dry-run plan for ${planResult.source}`}>
            <div className={styles.drawerHead}>
              <div className={styles.badgeRow}>
                <strong>Dry-run plan: {planResult.source}</strong>
                <Badge className={planResult.ok ? styles.statusStaged : styles.statusError}>{planResult.timed_out ? 'timed out' : `exit ${planResult.exit_code}`}</Badge>
                <Muted>{planResult.elapsed_ms}ms</Muted>
              </div>
              <Button variant="ghost" size="sm" onClick={() => setPlanResult(null)}>Close drawer</Button>
            </div>
            <pre className={styles.preBox}>{planResult.stdout || planResult.stderr || '(no output)'}</pre>
            {planResult.stdout && planResult.stderr && <pre className={styles.preBox}>{planResult.stderr}</pre>}
          </section>
        )}
      </Panel>

      <div ref={workbenchRef} className={cssFullscreen ? `${styles.workbenchShell} ${styles.workbenchFullscreen}` : styles.workbenchShell} data-fullscreen={isFullscreen ? 'true' : undefined}>
        <Panel title="Recipe studio workbench" actions={
          <div className={styles.toolbar}>
            <Select aria-label="Load existing recipe preset" value={selectedPreset} onChange={(e) => loadRecipeIntoEditor(e.target.value)} disabled={!catalogRead.data}>
              <option value="">Load existing .recipe…</option>
              {(catalogRead.data?.recipes ?? []).map((r) => <option key={r.relative_path} value={r.relative_path}>{r.source ? `${r.source} / ${r.name}` : `stock / ${r.name}`}</option>)}
            </Select>
            <Button variant="ghost" size="sm" aria-pressed={isFullscreen} onClick={toggleFullscreen}>{isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}</Button>
          </div>
        }>
          <ReadStatus label="Recipe catalog" resource={catalogRead} />
          <div className={styles.workbenchGrid}>
            <div className={styles.pane}>
              <Field label="Sample filename" help="Determines extension/format matching (path separators disallowed).">
                <Input value={filename} onChange={(e) => setFilename(e.target.value)} placeholder="sample.tsv" spellCheck={false} />
              </Field>
              <Field label="Sample input content" help="Sent only to isolated temp storage for offline --claims --no-load inspection.">
                <TextArea className={styles.codeArea} rows={12} value={sampleText} onChange={(e) => setSampleText(e.target.value)} spellCheck={false} />
              </Field>
              <div className={styles.toolbar}>
                <Button variant="ghost" loading={studioBusy === 'draft'} disabled={studioBusy !== null || !filename.trim()} onClick={() => void runDraft()}>Auto-draft .recipe</Button>
              </div>
            </div>
            <div className={styles.pane}>
              <Field label=".recipe DSL" help="Checked against Recording Law invariants before running the native C decomposer.">
                <TextArea className={styles.codeArea} rows={14} value={recipeDsl} onChange={(e) => setRecipeDsl(e.target.value)} spellCheck={false} />
              </Field>
              <div className={styles.toolbar}>
                <Button variant="ghost" loading={studioBusy === 'lint'} disabled={studioBusy !== null || !recipeDsl.trim()} onClick={() => void runLint()}>Lint Recording Law</Button>
                <Button loading={studioBusy === 'inspect'} disabled={studioBusy !== null || !recipeDsl.trim() || !filename.trim()} onClick={() => void runInspect()}>Run --claims --no-load</Button>
                {canIngest && (
                  <Button loading={studioBusy === 'admit'} disabled={studioBusy !== null || !recipeDsl.trim() || !filename.trim()} onClick={() => void runAdmit()}>
                    Admit sample to live DB
                  </Button>
                )}
              </div>
            </div>
          </div>
          {studioBusy && <LoadingText>Running {studioBusy}…</LoadingText>}
          {studioErr && <ErrorText role="alert">{studioErr}</ErrorText>}
          {admitResult && (
            <section className={styles.drawer} aria-label="Live database sample admission result">
              <div className={styles.badgeRow}>
                <strong>{`Live Admit (${admitResult.source})`}</strong>
                <Badge className={admitResult.ok ? styles.statusStaged : styles.statusError}>
                  {admitResult.executed ? (admitResult.timed_out ? 'timed out' : `exit ${admitResult.exit_code}`) : 'blocked by lint'}
                </Badge>
                <Badge>{admitResult.trust_class}</Badge>
                <Muted>{`${admitResult.witness} · ${admitResult.elapsed_ms}ms`}</Muted>
              </div>
              {admitResult.stdout && <pre className={styles.preBox}>{admitResult.stdout}</pre>}
              {admitResult.stderr && <pre className={styles.preBox}>{admitResult.stderr}</pre>}
            </section>
          )}
          {lintResult && (
            <section className={styles.drawer} aria-label="Recording Law linter report">
              <div className={styles.badgeRow}>
                <strong>Recording Law Linter</strong>
                <Badge className={lintResult.valid ? styles.statusStaged : styles.statusError}>{lintResult.valid ? 'valid' : `${lintResult.errors.length} error(s)`}</Badge>
                {lintResult.warnings.length > 0 && <Badge className={styles.statusHeld}>{lintResult.warnings.length} warning(s)</Badge>}
                {lintResult.summary.name && <Badge>name: {lintResult.summary.name}</Badge>}
                {lintResult.summary.grammar && <Badge>grammar: {lintResult.summary.grammar}</Badge>}
                {lintResult.summary.format && <Badge>format: {lintResult.summary.format}</Badge>}
                {lintResult.summary.has_content && <Badge className={styles.statusStaged}>content</Badge>}
                {lintResult.summary.has_thing && <Badge className={styles.statusStaged}>thing</Badge>}
                {lintResult.summary.has_attest && <Badge className={styles.statusStaged}>attest</Badge>}
              </div>
              {lintResult.errors.map((err, i) => <ErrorText key={i}>{err}</ErrorText>)}
              {lintResult.warnings.map((warn, i) => <Banner key={i} variant="warning">{warn}</Banner>)}
            </section>
          )}
          {inspectResult && (
            <section className={styles.drawer} aria-label="Dry-run claims and entities preview">
              <div className={styles.badgeRow}>
                <strong>Dry-run Packaging Inspector</strong>
                <Badge className={inspectResult.ok ? styles.statusStaged : styles.statusError}>{inspectResult.timed_out ? 'timed out' : `exit ${inspectResult.exit_code}`}</Badge>
                <Badge>{inspectResult.records_count} records</Badge>
                <Badge>{inspectResult.claims_count} claims</Badge>
                {inspectResult.compositions_count != null && <Badge>{inspectResult.compositions_count} compositions</Badge>}
                {inspectResult.unresolved_obligations_count != null && (
                  <Badge className={inspectResult.unresolved_obligations_count === 0 ? styles.statusStaged : styles.statusHeld}>
                    {inspectResult.unresolved_obligations_count} obligations
                  </Badge>
                )}
                <Muted>{inspectResult.elapsed_ms}ms · offline (--no-load)</Muted>
              </div>
              {inspectResult.obligations.length > 0 && <Banner variant="warning">Unresolved obligations detected:<pre className={styles.preBox}>{inspectResult.obligations.join('\n')}</pre></Banner>}
              <div className={styles.previewGrid}>
                {inspectResult.records.slice(0, 12).map((rec) => (
                  <div key={rec.record_index} className={styles.recordCard}>
                    <strong>Record #{rec.record_index}</strong>
                    {rec.held.length > 0 && <div className={styles.mono}>Held: {rec.held.join(' · ')}</div>}
                    <ul className={styles.claimList}>{rec.claims.map((c, idx) => <li key={idx}>{c}</li>)}</ul>
                  </div>
                ))}
              </div>
              {inspectResult.records.length === 0 && inspectResult.claims.length > 0 && (
                <ul className={styles.claimList}>{inspectResult.claims.slice(0, 30).map((c, idx) => <li key={idx}>{c}</li>)}</ul>
              )}
              <details>
                <summary>Native engine stdout & stderr</summary>
                {inspectResult.stdout && <pre className={styles.preBox}>{inspectResult.stdout}</pre>}
                {inspectResult.stderr && <pre className={styles.preBox}>{inspectResult.stderr}</pre>}
              </details>
            </section>
          )}
        </Panel>
      </div>

      <Modal open={forgetTarget !== null} onClose={() => { if (!maintBusy) setForgetTarget(null); }} title={`Forget source ${forgetTarget?.name ?? ''}?`} actions={
        <>
          <Button variant="ghost" disabled={maintBusy} onClick={() => setForgetTarget(null)}>Cancel</Button>
          <Button loading={maintBusy} onClick={() => forgetTarget && void runForget(forgetTarget.name)}>Forget witness</Button>
        </>
      }>
        <p>Runs <code>laplace forget {forgetTarget?.name}</code> to retract witness authority{forgetTarget?.witness ? ` (${forgetTarget.witness})` : ''} and recompute surviving consensus standings.</p>
      </Modal>
      <Modal open={confirmSweep} onClose={() => { if (!maintBusy) setConfirmSweep(false); }} title="Sweep unreferenced entities?" actions={
        <>
          <Button variant="ghost" disabled={maintBusy} onClick={() => setConfirmSweep(false)}>Cancel</Button>
          <Button loading={maintBusy} onClick={() => void runSweep()}>Run sweep</Button>
        </>
      }>
        <p>Runs <code>laplace sweep</code> in a maintenance transaction to remove unreferenced entities left behind by retracted sources.</p>
      </Modal>
    </div>
  );
}
