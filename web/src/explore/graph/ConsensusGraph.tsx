import { useEffect, useRef } from 'react';
import { Muted } from '@ui';
import ForceGraph2D from 'react-force-graph-2d';
import type { ExploreConsensusRow } from '../types';
import type { WalkPathNode } from '../store';
import styles from './ConsensusGraph.module.css';

interface GraphNode { id: string; label: string; walk?: boolean; }
interface GraphLink { source: string; target: string; type: string; mu: number; walk?: boolean; }

export function ConsensusGraph({
  centerId,
  centerLabel,
  edges,
  walkPath = [],
  onNodeClick,
}: {
  centerId: string;
  centerLabel: string;
  edges: ExploreConsensusRow[];
  walkPath?: WalkPathNode[];
  onNodeClick?: (id: string) => void;
}) {
  const ref = useRef<{ zoomToFit: (ms?: number) => void } | null>(null);

  const data = {
    nodes: buildNodes(centerId, centerLabel, edges, walkPath),
    links: buildLinks(centerId, edges, walkPath),
  };

  useEffect(() => {
    const t = setTimeout(() => ref.current?.zoomToFit(400), 300);
    return () => clearTimeout(t);
  }, [centerId, edges.length, walkPath.length]);

  return (
    <div className={styles.root}>
      {walkPath.length > 0 ? (
        <Muted>Walk path overlay ({walkPath.length} steps)</Muted>
      ) : null}
      <ForceGraph2D
        ref={ref as never}
        graphData={data}
        nodeLabel={(n: GraphNode) => n.label}
        linkLabel={(l: GraphLink) => `${l.type} μ=${l.mu.toFixed(1)}`}
        linkWidth={(l: GraphLink) => Math.max(0.5, l.mu / 400)}
        linkColor={(l: GraphLink) => (l.walk ? '#3ecf8e' : '#4f8cff')}
        onNodeClick={(n: GraphNode) => {
          if (n.id !== centerId && onNodeClick && n.id.length === 32) onNodeClick(n.id);
        }}
        nodeCanvasObject={(node: GraphNode, ctx, globalScale) => {
          const label = node.label.length > 18 ? `${node.label.slice(0, 17)}…` : node.label;
          const fontSize = 12 / globalScale;
          ctx.font = `${fontSize}px sans-serif`;
          ctx.fillStyle = node.walk ? '#3ecf8e' : node.id === centerId ? '#4f8cff' : '#d7e0f0';
          ctx.fillText(label, (node as GraphNode & { x: number; y: number }).x, (node as GraphNode & { y: number }).y);
        }}
      />
    </div>
  );
}

function buildNodes(
  centerId: string,
  centerLabel: string,
  edges: ExploreConsensusRow[],
  walkPath: WalkPathNode[],
): GraphNode[] {
  const nodes = new Map<string, GraphNode>();
  nodes.set(centerId, { id: centerId, label: centerLabel, walk: walkIdsHas(walkPath, centerId) });
  for (const e of edges) {
    const id = e.entity_id_hex || e.entity_label;
    if (!nodes.has(id)) nodes.set(id, { id, label: e.entity_label || e.type, walk: walkIdsHas(walkPath, id) });
  }
  for (const step of walkPath) {
    if (!nodes.has(step.idHex)) nodes.set(step.idHex, { id: step.idHex, label: step.label, walk: true });
  }
  return [...nodes.values()];
}

function buildLinks(centerId: string, edges: ExploreConsensusRow[], walkPath: WalkPathNode[]): GraphLink[] {
  const links = edges.map((e) => ({
    source: centerId,
    target: e.entity_id_hex || e.entity_label || e.type,
    type: e.type,
    mu: e.eff_mu,
    walk: false,
  }));
  for (let i = 1; i < walkPath.length; i++) {
    links.push({
      source: walkPath[i - 1].idHex,
      target: walkPath[i].idHex,
      type: 'walk',
      mu: 400,
      walk: true,
    });
  }
  return links;
}

function walkIdsHas(walkPath: WalkPathNode[], id: string) {
  return walkPath.some((n) => n.idHex === id);
}
