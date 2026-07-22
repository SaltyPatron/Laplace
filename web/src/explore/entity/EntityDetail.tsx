import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import {
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
  exploreResolve,
} from '../api';
import { GatePrompt } from '../components/GatePrompt';
import { RelationChip } from '../components/RelationChip';
import { type GlomeNode } from '../glome/GlomeCanvas';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import type { BillingReceipt, ExploreEntityPreviewResponse, ExploreEntityResponse } from '../types';
import styles from './EntityDetail.module.css';
import { EntityHeader } from './EntityHeader';
import { ExportTab } from './tabs/ExportTab';
import { GlomeTab } from './tabs/GlomeTab';
import { GraphTab } from './tabs/GraphTab';
import { LinksTab } from './tabs/LinksTab';
import { OverviewTab } from './tabs/OverviewTab';
import { ProvenanceTab } from './tabs/ProvenanceTab';
import { StructureTab } from './tabs/StructureTab';
import type { EntityTab, NeighborMode } from './tabs/types';

const TAB_IDS: EntityTab[] = ['overview', 'graph', 'glome', 'structure', 'links', 'provenance', 'export'];

export function EntityDetail() {
  const { idHex = '' } = useParams();
  const nav = useNavigate();
  const { tenant, quoteId, setExploreSeedPrompt } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const walkPath = useExploreStore((s) => s.walkPath);
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [tab, setTab] = useState<EntityTab>('overview');
  const [preview, setPreview] = useState<ExploreEntityPreviewResponse | null>(null);
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

  const quote = exploreQuote || quoteId;
  const walkHighlight = walkPath.map((n) => n.idHex);

  useEffect(() => {
    setEntity(null);
    setUnlocked(false);
    setPreview(null);
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
    explorePreview(idHex).then((p) => {
      setPreview(p);
      setBreadcrumb({ entityLabel: p.label, entityId: p.id_hex });
    }).catch((e) => setErr(e instanceof Error ? e.message : String(e)));
  }, [idHex, setBreadcrumb]);

  useEffect(() => {
    if (!preview || unlocked || detailLoading || needsGate) return;
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
  }, [preview, unlocked, detailLoading, needsGate, idHex, tenant, quote]);

  async function loadFull() {
    setErr(null);
    try {
      const res = await exploreEntity(idHex, { tenant, quoteId: quote });
      setEntity(res.entity);
      setInspectReceipt(res.billing ?? null);
      setUnlocked(true);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
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
    if (!preview) return;
    setDecomposeBusy(true);
    setErr(null);
    try {
      const res = await exploreDecompose(preview.label);
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
    if (!preview) return;
    setExploreSeedPrompt(`Tell me about "${preview.label}" (${preview.id_hex}) in the substrate consensus graph.`);
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

  if (err && !preview) return <ErrorText>{err}</ErrorText>;
  if (!preview) return <LoadingText>Loading entity…</LoadingText>;

  const show = entity;
  const fillTab = unlocked && (tab === 'graph' || tab === 'glome');

  return (
    <div className={cn(styles.detail, fillTab && styles.detailFill)}>
      <EntityHeader
        preview={preview}
        copied={copied}
        unlocked={unlocked}
        exportBusy={exportBusy}
        onCopyId={copyId}
        onExport={() => void downloadExport()}
        onAskSubstrate={askSubstrate}
      />

      {!unlocked ? (
        <>
          <Panel title="Preview">
            {detailLoading ? <LoadingText>Loading detail…</LoadingText> : null}
            <ul className={styles.previewFacts}>
              {preview.preview_facts.map((f, i) => (
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
          ) : detailLoading ? null : (
            <Muted>Loading entity detail…</Muted>
          )}
        </>
      ) : null}

      {unlocked && show ? (
        <>
          <NavTabs
            tabs={TAB_IDS.map((t) => ({
              id: t,
              label: t,
              active: tab === t,
              onClick: () => setTab(t),
            }))}
          />

          <div className={fillTab ? styles.tabFill : undefined}>
            {tab === 'overview' ? <OverviewTab entity={show} /> : null}
            {tab === 'graph' ? (
              <GraphTab
                centerId={show.id_hex}
                centerLabel={show.label}
                edgesOut={show.consensus_out}
                edgesIn={show.consensus_in}
                walkPath={walkPath}
                onNodeClick={(id) => nav(`/explore/entity/${id}`)}
              />
            ) : null}
            {tab === 'glome' ? (
              <GlomeTab
                entity={show}
                neighborMode={neighborMode}
                neighborsUnlocked={neighborsUnlocked}
                glomeExtraNodes={glomeExtraNodes}
                walkHighlight={walkHighlight}
                onNeighborModeChange={setNeighborMode}
                onLoadNeighbors={() => void loadNeighbors()}
              />
            ) : null}
            {tab === 'structure' ? (
              <StructureTab
                entity={show}
                preview={preview}
                decomposeNodes={decomposeNodes}
                decomposeBusy={decomposeBusy}
                containersUnlocked={containersUnlocked}
                containers={containers}
                onDecompose={() => void runDecompose()}
                onLoadContainers={() => void loadContainers()}
              />
            ) : null}
            {tab === 'links' ? (
              <LinksTab
                linksUnlocked={linksUnlocked}
                members={members}
                peers={peers}
                onLoadLinks={() => void loadLinks()}
              />
            ) : null}
            {tab === 'provenance' ? <ProvenanceTab entity={show} /> : null}
            {tab === 'export' ? (
              <ExportTab
                busy={exportBusy}
                onExport={() => void downloadExport()}
                receipt={exportReceipt}
                witnessRows={show.evidence_count}
                consensusRows={show.consensus_out.length + show.consensus_in.length}
              />
            ) : null}
          </div>
        </>
      ) : null}

      {err ? <ErrorText>{err}</ErrorText> : null}
    </div>
  );
}

export function ResolveRedirect() {
  const { ref = '' } = useParams();
  const nav = useNavigate();
  useEffect(() => {
    const surface = decodeURIComponent(ref);
    exploreResolve(surface)
      .then((r) =>
        // A resolvable-but-unwitnessed reference (exists=false) has no entity
        // page to show; route to the not-found explorer, which navigates by the
        // computed geometric anchor instead of a dead id.
        r.exists
          ? nav(`/explore/entity/${r.id_hex}`, { replace: true })
          : nav(`/explore/notfound/${encodeURIComponent(surface)}`, { replace: true }),
      )
      .catch(() => nav('/explore', { replace: true }));
  }, [ref, nav]);
  return <LoadingText>Resolving…</LoadingText>;
}
