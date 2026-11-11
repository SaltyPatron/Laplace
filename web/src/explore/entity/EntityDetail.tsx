import { Suspense, useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { exploreLexicon } from '../../query/api';
import type { ResponseField } from '../../query/types';
import {
  Button,
  ErrorText,
  LoadingText,
  Muted,
  NavTabs,
  Panel,
  cn,
} from '@ui';
import { PaymentRequiredError } from '../../api/client';
import {
  exploreContainers,
  exploreDecompose,
  exploreEntity,
  exploreExport,
  exploreMembers,
  exploreNeighbors,
  explorePeers,
  explorePreview,
} from '../api';
import { GatePrompt } from '../components/GatePrompt';
import { RelationChip } from '../components/RelationChip';
import { entityDestination } from '../components/entityDestination';
import { changeWorld } from '../../world/context';
import { WorldLink as RouterLink, WorldProjection } from '../../world/WorldLink';
import { presentations } from '../../world/presentations';
import { type GlomeNode } from '../glome/GlomeCanvas';
import { useAppStore } from '../../store';
import { useEntityLabels } from '../../shell/inspector';
import { useExploreStore } from '../store';
import type { BillingReceipt, ExploreEntityPreviewResponse, ExploreEntityResponse } from '../types';
import styles from './EntityDetail.module.css';
import { EntityHeader } from './EntityHeader';
import { LexiconEntry } from './LexiconEntry';
import { decomposableSurface, decompositionMatchesEntity } from './decomposeSurface';
import type { EntityTab, NeighborMode } from './tabs/types';
import { EntityTabPanel, TAB_IDS, TAB_PREFETCH } from './EntityTabPanel';

export function EntityDetail({ selectedId, selectedLabel, selectedType }: { selectedId?: string; selectedLabel?: string; selectedType?: string } = {}) {
  const { idHex: routeId = '' } = useParams();
  const idHex = selectedId ?? routeId;
  const nav = useNavigate();
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const { tenant, quoteId, setExploreSeedPrompt } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const walkPath = useExploreStore((s) => s.walkPath);
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const tab: EntityTab = TAB_IDS.includes(params.get('detail') as EntityTab) ? params.get('detail') as EntityTab : 'overview';
  const setTab = (value: EntityTab) => setParams(changeWorld(params, { detail: value === 'overview' ? null : value, inspect: '1' }), { preventScrollReset: true });
  const [preview, setPreview] = useState<ExploreEntityPreviewResponse | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  // A label already seen anywhere (search result, chip, palette) titles the page before its own read returns.
  const knownLabel = useEntityLabels((s) => s.labels[idHex.toLowerCase()]);
  const rememberLabel = useEntityLabels((s) => s.remember);
  const [entity, setEntity] = useState<ExploreEntityResponse | null>(null);
  const [unlocked, setUnlocked] = useState(false);
  const [inspectReceipt, setInspectReceipt] = useState<BillingReceipt | null>(null);
  const [exportReceipt, setExportReceipt] = useState<BillingReceipt | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [exportBusy, setExportBusy] = useState(false);
  const [linksUnlocked, setLinksUnlocked] = useState(false);
  const [containersUnlocked, setContainersUnlocked] = useState(false);
  const [neighborsUnlocked, setNeighborsUnlocked] = useState(false);
  const [neighborMode, setNeighborMode] = useState<NeighborMode>('structural');
  const [members, setMembers] = useState<Awaited<ReturnType<typeof exploreMembers>>['members'] | null>(null);
  const [peers, setPeers] = useState<Awaited<ReturnType<typeof explorePeers>>['peers'] | null>(null);
  const [containers, setContainers] = useState<Awaited<ReturnType<typeof exploreContainers>>['containers'] | null>(null);
  const [neighbors, setNeighbors] = useState<Awaited<ReturnType<typeof exploreNeighbors>>['neighbors'] | null>(null);
  const [decomposeNodes, setDecomposeNodes] = useState<Awaited<ReturnType<typeof exploreDecompose>>['nodes'] | null>(null);
  const [decomposeBusy, setDecomposeBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const [detailLoading, setDetailLoading] = useState(false);
  const [needsGate, setNeedsGate] = useState(false);
  const [lexicon, setLexicon] = useState<ResponseField | null>(null);
  const [lexiconError, setLexiconError] = useState<string | null>(null);
  const [lexiconGated, setLexiconGated] = useState(false);
  const [lexiconAttempt, setLexiconAttempt] = useState(0);
  const inspectOpen = params.get('inspect') === '1' || params.has('detail');

  const quote = exploreQuote || quoteId;
  const walkHighlight = walkPath.map((n) => n.idHex);
  const declaredType = selectedType ?? preview?.type ?? entity?.type;
  const requestedPresentation = params.get('presentation');
  const presentation = presentations.find(item => item.id === requestedPresentation)
    ?? presentations.find(item => declaredType && item.types.includes(declaredType)) ?? presentations[0];
  const availablePresentations = presentations.filter(item => !item.types.length || item.types.includes(declaredType ?? '') || item.id === presentation.id);

  useEffect(() => {
    let stale = false;
    setLexicon(null); setLexiconError(null); setLexiconGated(false);
    if (presentation.id !== 'knowledge') return;
    exploreLexicon(idHex, undefined, { tenant, quoteId: quote })
      .then(value => { if (!stale) setLexicon(value); })
      .catch(failure => {
        if (stale) return;
        if (failure instanceof PaymentRequiredError) setLexiconGated(true);
        else setLexiconError(failure instanceof Error ? failure.message : String(failure));
      });
    return () => { stale = true; };
  }, [idHex, tenant, quote, lexiconAttempt, presentation.id]);

  useEffect(() => {
    setEntity(null);
    setUnlocked(false);
    setPreview(null);
    setPreviewError(null);
    setInspectReceipt(null);
    setExportReceipt(null);
    setErr(null);
    setLinksUnlocked(false);
    setContainersUnlocked(false);
    setNeighborsUnlocked(false);
    setMembers(null);
    setPeers(null);
    setContainers(null);
    setNeighbors(null);
    setDecomposeNodes(null);
    setDetailLoading(false);
    setNeedsGate(false);
  }, [idHex, selectedId, setBreadcrumb]);

  useEffect(() => {
    if (preview || (selectedLabel && !inspectOpen)) return;
    const controller = new AbortController();
    explorePreview(idHex, { tenant, signal: controller.signal }).then(p => {
      if (controller.signal.aborted) return;
      setPreview(p);
      if (p.label) rememberLabel(p.id_hex, p.label);
      setBreadcrumb({ entityLabel: p.label, entityId: p.id_hex });
    }).catch(error => {
      if (!controller.signal.aborted) setPreviewError(error instanceof Error ? error.message : String(error));
    });
    return () => controller.abort();
  }, [idHex, selectedLabel, inspectOpen, preview, tenant, setBreadcrumb, rememberLabel]);

  useEffect(() => {
    if (!inspectOpen || unlocked || detailLoading || needsGate || err) return;
    setDetailLoading(true);
    void exploreEntity(idHex, { tenant, quoteId: quote })
      .then((res) => {
        setEntity(res.entity);
        setInspectReceipt(res.billing ?? null);
        setUnlocked(true);
      })
      .catch((e) => {
        if (e instanceof PaymentRequiredError) {
          setNeedsGate(true);
          return;
        }
        setErr(e instanceof Error ? e.message : String(e));
      })
      .finally(() => setDetailLoading(false));
  }, [inspectOpen, unlocked, detailLoading, needsGate, err, idHex, tenant, quote]);

  async function loadFull() {
    if (detailLoading) return;
    setDetailLoading(true);
    setErr(null);
    try {
      const res = await exploreEntity(idHex, { tenant, quoteId: quote });
      setEntity(res.entity);
      setInspectReceipt(res.billing ?? null);
      setUnlocked(true);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setDetailLoading(false);
    }
  }

  async function loadLinks() {
    setErr(null);
    try {
      const [m, p] = await Promise.all([
        exploreMembers(idHex, 100, { tenant, quoteId: quote }),
        explorePeers(idHex, 100, { tenant, quoteId: quote }),
      ]);
      setMembers(m.members);
      setPeers(p.peers);
      setLinksUnlocked(true);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  async function loadContainers() {
    setErr(null);
    try {
      const c = await exploreContainers(idHex, 3, 50, { tenant, quoteId: quote });
      setContainers(c.containers);
      setContainersUnlocked(true);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  async function loadNeighbors() {
    setErr(null);
    try {
      const n = await exploreNeighbors(idHex, 12, { tenant, quoteId: quote });
      setNeighbors(n.neighbors);
      setNeighborsUnlocked(true);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  async function runDecompose() {
    if (!entity) return;
    // Decompose the entity's own text surface, never its display label: a label
    // can be a lexicalization, a record's parts or a typed short id.
    const surface = decomposableSurface(entity, preview);
    if (surface === null) return;
    setDecomposeBusy(true);
    setErr(null);
    try {
      const res = await exploreDecompose(surface);
      if (!decompositionMatchesEntity(entity.id_hex, res.root_id_hex)) {
        setDecomposeNodes(null);
        setErr(`"${surface}" decomposes to ${res.root_id_hex}, not this entity; its structure is shown under Constituents.`);
        return;
      }
      setDecomposeNodes(res.nodes);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setDecomposeBusy(false);
    }
  }

  async function downloadExport() {
    setExportBusy(true);
    setErr(null);
    try {
      const res = await exploreExport(
        idHex,
        { include_members: true, include_peers: true },
        { tenant, quoteId: quote },
      );
      setExportReceipt(res.billing ?? null);
      const blob = new Blob([JSON.stringify(res.export, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `laplace-export-${idHex.slice(0, 8)}.json`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setExportBusy(false);
    }
  }

  function copyId() {
    void navigator.clipboard.writeText(idHex);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  function askSubstrate() {
    setExploreSeedPrompt(`Tell me about "${preview?.label ?? selectedLabel ?? idHex}" in the substrate consensus graph.`);
    nav('/chat');
  }

  const glomeExtraNodes = useMemo((): GlomeNode[] => {
    if (!neighborsUnlocked || !neighbors) return [];
    if (neighborMode === 'structural') {
      // Real S³ coords from structural_neighbors_of — never decorative Math.sin slots.
      return neighbors.structural
        .filter((n) => n.x != null && n.y != null && n.z != null)
        .map((n) => ({
          id: n.neighbor_id_hex ?? `nn-${n.neighbor}`,
          label: n.neighbor,
          x: n.x!,
          y: n.y!,
          z: n.z!,
          radius: n.radius ?? 0.8,
          kind: 'neighbor' as const,
        }));
    }
    // Semantic facts are consensus strings, not S³ points — do not invent positions.
    return [];
  }, [neighbors, neighborsUnlocked, neighborMode]);

  const show = entity;
  const fillTab = inspectOpen && unlocked && (tab === 'graph' || tab === 'glome');

  return (
    <WorldProjection><div className={cn(styles.detail, fillTab && styles.detailFill)}>
      {preview ? <EntityHeader
        preview={preview}
        copied={copied}
        unlocked={unlocked}
        exportBusy={exportBusy}
        onCopyId={copyId}
        onExport={() => void downloadExport()}
        onAskSubstrate={askSubstrate}
      /> : <header className={styles.header}><div><h2 title={idHex}>{selectedLabel ?? knownLabel ?? entity?.label ?? (previewError ? idHex : 'Loading…')}</h2><Button variant="ghost" size="sm" onClick={copyId}>{copied ? 'Reference copied' : 'Copy reference'}</Button></div><div className={styles.actions}><Button asChild variant="ghost" size="sm"><RouterLink to={`/explore/entity/${idHex}`}>Full entity</RouterLink></Button><Button asChild variant="ghost" size="sm"><RouterLink to={`/proof?q=${encodeURIComponent(selectedLabel ?? idHex)}`}>Prove storage</RouterLink></Button><Button variant="ghost" onClick={askSubstrate}>Ask substrate</Button></div></header>}

      <NavTabs label="Subject presentations" tabs={availablePresentations.map(item => ({ id: item.id, label: item.label, active: item.id === presentation.id,
        href: `${location.pathname}?${changeWorld(params, { presentation: item.id })}`,
        onClick: () => setParams(changeWorld(params, { presentation: item.id === 'knowledge' ? 'knowledge' : item.id }), { preventScrollReset: true }) }))} />
      {preview?.exists === false && <p role="status">This preview did not report the addressed content as present. The reference is retained; calculating an identity alone does not establish recorded knowledge.</p>}
      {requestedPresentation && !presentations.some(item => item.id === requestedPresentation) && <ErrorText>The requested presentation “{requestedPresentation}” is unavailable. Its reference has been retained.</ErrorText>}
      {presentation.render ? <Suspense fallback={<LoadingText>Reading subject presentation…</LoadingText>}>{presentation.render(idHex)}</Suspense>
        : fillTab ? null
        : lexiconGated ? <GatePrompt serviceId="inspect" label="Read recorded meanings, usage and relationships." onReady={() => setLexiconAttempt(value => value + 1)} />
          : <LexiconEntry key={idHex} field={lexicon} error={lexiconError} onRetry={() => setLexiconAttempt(value => value + 1)} />}

      {(preview || selectedLabel) && <NavTabs
        label="Entity inspection views"
        tabs={TAB_IDS.map((t) => ({
          id: t,
          label: t,
          active: inspectOpen && tab === t,
          href: `${location.pathname}?${changeWorld(params, { detail: t === 'overview' ? null : t, inspect: '1' })}`,
          onIntent: () => { void TAB_PREFETCH[t](); },
          onClick: () => setTab(t),
        }))}
      />}

      <details className={styles.inspector} open={inspectOpen} onToggle={event => {
        const open = event.currentTarget.open;
        if (open !== inspectOpen) setParams(changeWorld(params, { inspect: open ? '1' : null, detail: open ? params.get('detail') : null }), { preventScrollReset: true });
      }}>
        <summary>Structure, standing and evidence</summary>
        {previewError && <ErrorText>Entity metadata: {previewError}</ErrorText>}

      {inspectOpen && !unlocked ? (
        <>
          <Panel title="Preview">
            {detailLoading ? <LoadingText>Loading detail…</LoadingText> : null}
            <ul className={styles.previewFacts}>
              {preview?.preview_facts.map((f, i) => (
                <li key={i}>
                  <RelationChip type={f.type} label={f.fact} mu={f.eff_mu} witnesses={f.witnesses} />
                </li>
              ))}
            </ul>
          </Panel>
          {needsGate ? (
            <GatePrompt
              serviceId="inspect"
              label="Payment required for full glass-box detail — consensus neighborhood, geometry, evidence, and training export."
              receipt={inspectReceipt}
              onReady={() => void loadFull()}
            />
          ) : err ? (
            <Button onClick={() => void loadFull()}>Retry entity detail</Button>
          ) : detailLoading ? null : (
            <Muted>Loading entity detail…</Muted>
          )}
        </>
      ) : null}

      {inspectOpen && unlocked && show ? (
        <>
          <div className={fillTab ? styles.tabFill : undefined}>
            <EntityTabPanel
              tab={tab}
              show={show}
              preview={preview}
              walkPath={walkPath}
              onNodeClick={(id) => nav(entityDestination(id, location), { preventScrollReset: true })}
              neighborMode={neighborMode}
              neighborsUnlocked={neighborsUnlocked}
              glomeExtraNodes={glomeExtraNodes}
              walkHighlight={walkHighlight}
              onNeighborModeChange={setNeighborMode}
              onLoadNeighbors={() => void loadNeighbors()}
              decomposeNodes={decomposeNodes}
              decomposeBusy={decomposeBusy}
              containersUnlocked={containersUnlocked}
              containers={containers}
              onDecompose={() => void runDecompose()}
              onLoadContainers={() => void loadContainers()}
              linksUnlocked={linksUnlocked}
              members={members}
              peers={peers}
              onLoadLinks={() => void loadLinks()}
              exportBusy={exportBusy}
              onExport={() => void downloadExport()}
              exportReceipt={exportReceipt}
            />
          </div>
        </>
      ) : null}

      {err ? <ErrorText>{err}</ErrorText> : null}
      </details>
    </div></WorldProjection>
  );
}
