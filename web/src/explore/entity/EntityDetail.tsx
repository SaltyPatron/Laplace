import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
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
import { ExportPanel } from '../components/ExportPanel';
import { GatePrompt } from '../components/GatePrompt';
import { EntityLink } from '../components/EntityLink';
import { MuBadge } from '../components/MuBadge';
import { RelationChip } from '../components/RelationChip';
import { GlomeCanvasFromPhysicalities, type GlomeNode } from '../glome/GlomeCanvas';
import { ConsensusGraph } from '../graph/ConsensusGraph';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import type { BillingReceipt, ExploreEntityPreviewResponse, ExploreEntityResponse } from '../types';

type Tab = 'overview' | 'graph' | 'glome' | 'structure' | 'links' | 'provenance' | 'export';
type NeighborMode = 'structural' | 'semantic';

export function EntityDetail() {
  const { idHex = '' } = useParams();
  const nav = useNavigate();
  const { tenant, quoteId, setExploreSeedPrompt } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const walkPath = useExploreStore((s) => s.walkPath);
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [tab, setTab] = useState<Tab>('overview');
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
    explorePreview(idHex).then((p) => {
      setPreview(p);
      setBreadcrumb({ entityLabel: p.label, entityId: p.id_hex });
    }).catch((e) => setErr(e instanceof Error ? e.message : String(e)));
  }, [idHex, setBreadcrumb]);

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
    nav('/');
  }

  const glomeExtraNodes = useMemo((): GlomeNode[] => {
    if (!neighborsUnlocked || !neighbors) return [];
    if (neighborMode === 'structural') {
      return neighbors.structural.map((n, i) => ({
        id: `nn-${i}-${n.neighbor}`,
        label: n.neighbor,
        x: Math.sin(i) * 0.7,
        y: Math.cos(i * 1.3) * 0.7,
        z: Math.sin(i * 0.7) * 0.5,
        radius: 0.8,
        kind: 'neighbor' as const,
      }));
    }
    return neighbors.semantic.map((s, i) => ({
      id: `sem-${i}-${s.fact}`,
      label: s.fact,
      x: Math.cos(i * 0.9) * 0.65,
      y: Math.sin(i * 0.5) * 0.65,
      z: Math.cos(i * 1.1) * 0.4,
      radius: 0.7,
      mu: s.eff_mu,
      kind: 'peer' as const,
    }));
  }, [neighbors, neighborsUnlocked, neighborMode]);

  if (err && !preview) return <p className="error">{err}</p>;
  if (!preview) return <p className="muted">Loading…</p>;

  const show = entity ?? null;

  return (
    <div className="entity-detail">
      <header className="entity-header">
        <div>
          <h2>{preview.label}</h2>
          <p className="muted mono">
            {preview.id_hex} · tier {preview.tier ?? '—'} · {preview.type ?? 'unknown'}
            {' '}
            <button type="button" className="linkish" onClick={copyId}>{copied ? 'Copied' : 'Copy id'}</button>
          </p>
          <MuBadge witnesses={preview.evidence_count} />
        </div>
        <div className="entity-actions">
          <button type="button" className="primary" disabled={!unlocked || exportBusy} onClick={() => void downloadExport()}>
            Export for training
          </button>
          <button type="button" onClick={askSubstrate}>Ask substrate</button>
        </div>
      </header>

      {!unlocked ? (
        <>
          <section className="preview-facts">
            <h3>Preview</h3>
            <ul>
              {preview.preview_facts.map((f, i) => (
                <li key={i}><RelationChip type={f.type} label={f.fact} mu={f.eff_mu} witnesses={f.witnesses} /></li>
              ))}
            </ul>
          </section>
          <GatePrompt
            serviceId="inspect"
            label="Unlock the full glass-box detail — consensus neighborhood, geometry, evidence, and training export."
            receipt={inspectReceipt}
            onReady={() => void loadFull()}
          />
        </>
      ) : null}

      {unlocked && show ? (
        <>
          <nav className="detail-tabs">
            {(['overview', 'graph', 'glome', 'structure', 'links', 'provenance', 'export'] as Tab[]).map((t) => (
              <button key={t} type="button" className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>{t}</button>
            ))}
          </nav>

          {tab === 'overview' ? (
            <section>
              <h3>Salient facts</h3>
              <ul>{show.salient_facts.map((f, i) => (
                <li key={i}><RelationChip type={f.type} label={f.fact} mu={f.eff_mu} witnesses={f.witnesses} /></li>
              ))}</ul>
              {show.senses.length > 0 ? (
                <>
                  <h3>Senses</h3>
                  <table className="explore-table"><tbody>
                    {show.senses.map((s) => (
                      <tr key={s.sense_id_hex}>
                        <td><EntityLink idHex={s.synset_id_hex} label={s.synset_label} /></td>
                        <td><MuBadge mu={s.eff_mu} witnesses={s.witnesses} /></td>
                      </tr>
                    ))}
                  </tbody></table>
                </>
              ) : null}
            </section>
          ) : null}

          {tab === 'graph' ? (
            <>
              <ConsensusGraph
                centerId={show.id_hex}
                centerLabel={show.label}
                edges={show.consensus_out}
                walkPath={walkPath}
                onNodeClick={(id) => nav(`/explore/entity/${id}`)}
              />
            </>
          ) : null}

          {tab === 'glome' ? (
            <>
              <div className="glome-controls row">
                <span>Overlay:</span>
                <button type="button" className={neighborMode === 'structural' ? 'active' : ''} onClick={() => setNeighborMode('structural')}>Structural (nn)</button>
                <button type="button" className={neighborMode === 'semantic' ? 'active' : ''} onClick={() => setNeighborMode('semantic')}>Semantic peers</button>
              </div>
              {!neighborsUnlocked ? (
                <GatePrompt serviceId="nn" label="Load nearest-neighbor overlay (structural axis on S³)." receipt={null} onReady={() => void loadNeighbors()} />
              ) : (
                <GlomeCanvasFromPhysicalities
                  physicalities={show.physicalities}
                  label={show.label}
                  idHex={show.id_hex}
                  extraNodes={glomeExtraNodes}
                  highlightIds={walkHighlight}
                />
              )}
            </>
          ) : null}

          {tab === 'structure' ? (
            <section>
              <h3>Constituents</h3>
              {show.constituents.length === 0 ? <p className="muted">None</p> : (
                <ul>{show.constituents.map((c) => (
                  <li key={c.ordinal}><EntityLink idHex={c.child_id_hex} label={c.child_label} /></li>
                ))}</ul>
              )}
              <h3>Decompose (free)</h3>
              <button type="button" disabled={decomposeBusy} onClick={() => void runDecompose()}>
                {decomposeBusy ? 'Decomposing…' : `Decompose "${preview.label}"`}
              </button>
              {decomposeNodes ? (
                <table className="explore-table">
                  <thead><tr><th>Ord</th><th>Tier</th><th>Label</th><th>Id</th></tr></thead>
                  <tbody>
                    {decomposeNodes.map((n) => (
                      <tr key={n.ordinal}>
                        <td>{n.ordinal}</td>
                        <td>{n.tier}</td>
                        <td>{n.label}</td>
                        <td className="mono"><EntityLink idHex={n.id_hex} label={n.id_hex.slice(0, 8)} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              ) : null}
              <h3>Containers</h3>
              {containersUnlocked && containers ? (
                containers.containers.length === 0 ? <p className="muted">None</p> : (
                  <ul>{containers.containers.map((c) => (
                    <li key={c.entity_id_hex}>
                      <EntityLink idHex={c.entity_id_hex} label={c.entity_label} />
                      {' '}tier {c.tier} · {c.type} · {c.hops} hop{c.hops === 1 ? '' : 's'}
                    </li>
                  ))}</ul>
                )
              ) : (
                <GatePrompt
                  serviceId="visualization.deep_export"
                  label="Expand container documents (multi-hop fan-out)."
                  onReady={() => void loadContainers()}
                />
              )}
            </section>
          ) : null}

          {tab === 'links' ? (
            linksUnlocked && members && peers ? (
              <section>
                <h3>Members</h3>
                {members.members.length === 0 ? <p className="muted">None</p> : (
                  <ul>{members.members.map((m) => (
                    <li key={m.member_id_hex}>
                      <EntityLink idHex={m.member_id_hex} label={m.member_label} />
                      {' '}({m.kind}) <MuBadge mu={m.eff_mu} witnesses={m.witnesses} />
                    </li>
                  ))}</ul>
                )}
                <h3>Peers</h3>
                {peers.peers.length === 0 ? <p className="muted">None</p> : (
                  <ul>{peers.peers.map((p) => (
                    <li key={`${p.peer}-${p.kind}`}>{p.peer} ({p.kind}) — {p.strength.toFixed(2)}</li>
                  ))}</ul>
                )}
              </section>
            ) : (
              <GatePrompt
                serviceId="visualization.deep_export"
                label="Expand cross-links — members and semantic peers (billable fan-out)."
                onReady={() => void loadLinks()}
              />
            )
          ) : null}

          {tab === 'provenance' ? (
            <table className="explore-table">
              <thead><tr><th>Type</th><th>Object</th><th>μ</th><th>Witnesses</th></tr></thead>
              <tbody>
                {show.evidence.map((e, i) => (
                  <tr key={i}>
                    <td>{e.type_label}</td>
                    <td>{e.object_label}</td>
                    <td>{e.eff_mu != null ? Number(e.eff_mu).toFixed(1) : '—'}</td>
                    <td>{e.observation_count}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}

          {tab === 'export' ? (
            <ExportPanel
              busy={exportBusy}
              onExport={() => void downloadExport()}
              receipt={exportReceipt}
              witnessRows={show.evidence_count}
              consensusRows={show.consensus_out.length + show.consensus_in.length}
            />
          ) : null}
        </>
      ) : null}

      {err ? <p className="error">{err}</p> : null}
    </div>
  );
}

export function ResolveRedirect() {
  const { ref = '' } = useParams();
  const nav = useNavigate();
  useEffect(() => {
    exploreResolve(decodeURIComponent(ref))
      .then((r) => nav(`/explore/entity/${r.id_hex}`, { replace: true }))
      .catch(() => nav('/explore', { replace: true }));
  }, [ref, nav]);
  return <p className="muted">Resolving…</p>;
}
