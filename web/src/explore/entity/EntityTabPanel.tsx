/** Entity inspector tabs: lazy tab loaders, prefetch table and the body of the selected tab. */
import { lazy, Suspense } from 'react';
import { LoadingText } from '@ui';
import type { GlomeNode } from '../glome/GlomeCanvas';
import type { WalkPathNode } from '../store';
import type { BillingReceipt, ExploreEntityPreviewResponse, ExploreEntityResponse } from '../types';
import type { exploreContainers, exploreDecompose, exploreMembers, explorePeers } from '../api';
import type { EntityTab, NeighborMode } from './tabs/types';

const loadOverviewTab = () => import('./tabs/OverviewTab');
const loadGraphTab = () => import('./tabs/GraphTab');
const loadGlomeTab = () => import('./tabs/GlomeTab');
const loadStructureTab = () => import('./tabs/StructureTab');
const loadLinksTab = () => import('./tabs/LinksTab');
const loadProvenanceTab = () => import('./tabs/ProvenanceTab');
const loadExportTab = () => import('./tabs/ExportTab');

const OverviewTab = lazy(() => loadOverviewTab().then((m) => ({ default: m.OverviewTab })));
const GraphTab = lazy(() => loadGraphTab().then((m) => ({ default: m.GraphTab })));
const GlomeTab = lazy(() => loadGlomeTab().then((m) => ({ default: m.GlomeTab })));
const StructureTab = lazy(() => loadStructureTab().then((m) => ({ default: m.StructureTab })));
const LinksTab = lazy(() => loadLinksTab().then((m) => ({ default: m.LinksTab })));
const ProvenanceTab = lazy(() => loadProvenanceTab().then((m) => ({ default: m.ProvenanceTab })));
const ExportTab = lazy(() => loadExportTab().then((m) => ({ default: m.ExportTab })));

export const TAB_PREFETCH: Record<EntityTab, () => Promise<unknown>> = {
  overview: loadOverviewTab,
  graph: loadGraphTab,
  glome: loadGlomeTab,
  structure: loadStructureTab,
  links: loadLinksTab,
  provenance: loadProvenanceTab,
  export: loadExportTab,
};

export const TAB_IDS: EntityTab[] = ['overview', 'graph', 'glome', 'structure', 'links', 'provenance', 'export'];

export function EntityTabPanel({
  tab,
  show,
  preview,
  walkPath,
  onNodeClick,
  neighborMode,
  neighborsUnlocked,
  glomeExtraNodes,
  walkHighlight,
  onNeighborModeChange,
  onLoadNeighbors,
  decomposeNodes,
  decomposeBusy,
  containersUnlocked,
  containers,
  onDecompose,
  onLoadContainers,
  linksUnlocked,
  members,
  peers,
  onLoadLinks,
  exportBusy,
  onExport,
  exportReceipt,
}: {
  tab: EntityTab;
  show: ExploreEntityResponse;
  preview: ExploreEntityPreviewResponse | null;
  walkPath: WalkPathNode[];
  onNodeClick: (id: string) => void;
  neighborMode: NeighborMode;
  neighborsUnlocked: boolean;
  glomeExtraNodes: GlomeNode[];
  walkHighlight: string[];
  onNeighborModeChange: (mode: NeighborMode) => void;
  onLoadNeighbors: () => void;
  decomposeNodes: Awaited<ReturnType<typeof exploreDecompose>>['nodes'] | null;
  decomposeBusy: boolean;
  containersUnlocked: boolean;
  containers: Awaited<ReturnType<typeof exploreContainers>>['containers'] | null;
  onDecompose: () => void;
  onLoadContainers: () => void;
  linksUnlocked: boolean;
  members: Awaited<ReturnType<typeof exploreMembers>>['members'] | null;
  peers: Awaited<ReturnType<typeof explorePeers>>['peers'] | null;
  onLoadLinks: () => void;
  exportBusy: boolean;
  onExport: () => void;
  exportReceipt: BillingReceipt | null;
}) {
  return (
    <Suspense fallback={<LoadingText>Loading entity workspace…</LoadingText>}>
    {tab === 'overview' ? <OverviewTab entity={show} /> : null}
    {tab === 'graph' ? (
      <GraphTab
        centerId={show.id_hex}
        centerLabel={show.label}
        edgesOut={show.consensus_out}
        edgesIn={show.consensus_in}
        walkPath={walkPath}
        onNodeClick={onNodeClick}
      />
    ) : null}
    {tab === 'glome' ? (
      <GlomeTab
        entity={show}
        neighborMode={neighborMode}
        neighborsUnlocked={neighborsUnlocked}
        glomeExtraNodes={glomeExtraNodes}
        walkHighlight={walkHighlight}
        onNeighborModeChange={onNeighborModeChange}
        onLoadNeighbors={onLoadNeighbors}
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
        onDecompose={onDecompose}
        onLoadContainers={onLoadContainers}
      />
    ) : null}
    {tab === 'links' ? (
      <LinksTab
        linksUnlocked={linksUnlocked}
        members={members}
        peers={peers}
        onLoadLinks={onLoadLinks}
      />
    ) : null}
    {tab === 'provenance' ? <ProvenanceTab entity={show} /> : null}
    {tab === 'export' ? (
      <ExportTab
        busy={exportBusy}
        onExport={onExport}
        receipt={exportReceipt}
        witnessRows={show.evidence_count}
        consensusRows={show.consensus_out.length + show.consensus_in.length}
      />
    ) : null}
    </Suspense>
  );
}
