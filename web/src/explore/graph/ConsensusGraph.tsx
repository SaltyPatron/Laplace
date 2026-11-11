import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { forceCollide, forceManyBody, forceRadial } from 'd3-force-3d';
import { MOUSE, type Sprite, type SpriteMaterial } from 'three';
import type { ExploreConsensusRow } from '../types';
import type { WalkPathNode } from '../store';
import { useVisualizationPalette } from '../visualizationPalette';
import styles from './ConsensusGraph.module.css';
import { useGraphFlyControls } from './useGraphFlyControls';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';
import {
  binding,
  CHARGE,
  fromStar,
  fromWeb,
  graphForDimension,
  LINK_BASE,
  NODE_REL_SIZE,
  relationCounts,
  SHELL_RADIUS,
  withoutPackaging,
  withoutRelations,
  type Dim,
  type WebEdge,
  type WebGraph,
  type WebNode,
} from './graphData';
import { buildNodeVisuals, MAX_VISIBLE_LABELS } from './graphVisuals';
import { Graph3D, type Fg3dApi } from './Graph3D';
import { Graph2D, type Fg2dApi } from './Graph2D';
import { GraphLegend } from './GraphLegend';

export { graphForDimension, withoutPackaging, withoutRelations } from './graphData';
export type { WebEdge, WebGraph, WebNode } from './graphData';

export function ConsensusGraph({
  centerId,
  centerLabel,
  edges,
  web,
  walkPath = [],
  onNodeClick,
  fill = false,
  hops,
  fanout,
  hopsMax = 4,
  fanoutMax = 16,
  maxNodes,
  maxNodesMax = 2048,
  onHopsChange,
  onFanoutChange,
  onMaxNodesChange,
  dim = 'belief',
  onDimChange,
  toolbar,
}: {
  centerId: string;
  centerLabel: string;
  edges: ExploreConsensusRow[];
  web?: WebGraph | null;
  walkPath?: WalkPathNode[];
  onNodeClick?: (id: string) => void;
  fill?: boolean;
  hops?: number;
  fanout?: number;
  hopsMax?: number;
  fanoutMax?: number;
  maxNodes?: number;
  maxNodesMax?: number;
  onHopsChange?: (n: number) => void;
  onFanoutChange?: (n: number) => void;
  onMaxNodesChange?: (n: number) => void;
  dim?: Dim;
  onDimChange?: (d: Dim) => void;
  toolbar?: ReactNode;
}) {
  const ref2d = useRef<Fg2dApi | null>(null);
  const ref3d = useRef<Fg3dApi | null>(null);
  const shellRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const fittedKey = useRef<string>('');
  const clickTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const palette = useVisualizationPalette();
  const labelCache = useRef(new Map<string, Sprite>());
  const labelMaterials = useRef(new Set<SpriteMaterial>());

  useEffect(() => {
    const cache = labelCache.current;
    const materials = labelMaterials.current;
    return () => {
      cache.clear();
      for (const material of materials) {
        material.map?.dispose();
        material.dispose();
      }
      materials.clear();
    };
  }, [centerId]);

  useGraphFlyControls(shellRef, ref3d, dim !== '2d' && size.width > 0);
  const webGlReady = useDeferredWebGlMount(dim !== '2d' && size.width > 0 && size.height > 0);

  // Tear down the WebGL renderer before React drops the DOM node — tab switches
  // Graph↔Glome otherwise race and surface "WebGL context was lost".
  useEffect(() => {
    return () => {
      const fg = ref3d.current;
      try {
        fg?.pauseAnimation?.();
        fg?._destructor?.();
      } catch {
        /* already disposed */
      }
      ref3d.current = null;
    };
  }, []);

  const rawData = useMemo(
    () => withoutPackaging(web ? fromWeb(web, walkPath) : fromStar(centerId, centerLabel, edges, walkPath), centerId),
    [web, centerId, centerLabel, edges, walkPath],
  );
  const relations = useMemo(() => relationCounts(rawData.links), [rawData.links]);
  const [hidden, setHidden] = useState<ReadonlySet<string>>(() => new Set());
  useEffect(() => setHidden(new Set()), [centerId]);
  const baseData = useMemo(() => withoutRelations(rawData, hidden, centerId), [rawData, hidden, centerId]);
  const data = useMemo(
    () => graphForDimension(baseData, dim, centerId),
    [baseData, dim, centerId],
  );
  const toggleRelation = (type: string) => setHidden((prev) => {
    const next = new Set(prev);
    if (next.has(type)) next.delete(type); else next.add(type);
    return next;
  });

  const maxHop = useMemo(
    () => Math.max(0, ...data.nodes.map((n) => n.hop)),
    [data.nodes],
  );

  const expanded = web != null && web.nodes.length > 0;
  const beliefAvailable = useMemo(
    () => data.nodes.some((n) =>
      Number.isFinite(n.beliefX) && Number.isFinite(n.beliefY) && Number.isFinite(n.beliefZ)),
    [data.nodes],
  );
  const beliefMode = dim === 'belief' && beliefAvailable;
  const nodeVisuals = useMemo(
    () => buildNodeVisuals(data, centerId, palette),
    [data, centerId, palette],
  );
  const labelledIds = useMemo(() => {
    if (data.nodes.length <= MAX_VISIBLE_LABELS) return new Set(data.nodes.map((node) => node.id));
    const ordered = data.nodes.slice().sort((a, b) => {
      const ap = a.id === centerId ? Number.POSITIVE_INFINITY
        : a.walk ? Number.MAX_SAFE_INTEGER
          : nodeVisuals.get(a.id)?.val ?? 0;
      const bp = b.id === centerId ? Number.POSITIVE_INFINITY
        : b.walk ? Number.MAX_SAFE_INTEGER
          : nodeVisuals.get(b.id)?.val ?? 0;
      return bp - ap || a.hop - b.hop || a.id.localeCompare(b.id);
    });
    return new Set(ordered.slice(0, MAX_VISIBLE_LABELS).map((node) => node.id));
  }, [data.nodes, centerId, nodeVisuals]);

  useEffect(() => {
    const el = shellRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const cr = entries[0]?.contentRect;
      if (!cr) return;
      const width = Math.max(0, Math.floor(cr.width));
      const height = Math.max(0, Math.floor(cr.height));
      setSize((prev) => (prev.width === width && prev.height === height ? prev : { width, height }));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const configureForces3d = useCallback(() => {
    const fg = ref3d.current;
    if (!fg) return;
    const linkForce = fg.d3Force('link') as {
      distance?: (fn: (l: WebEdge) => number) => unknown;
      strength?: (fn: (l: WebEdge) => number) => unknown;
    } | undefined;

    if (beliefMode) {
      // Native eigenmap coordinates are pinned. Decorative forces would destroy
      // the diffusion geometry this mode exists to inspect.
      fg.d3Force('charge', forceManyBody().strength(0));
      linkForce?.strength?.(() => 0);
      fg.d3Force('radial', forceRadial(() => 0).strength(0));
      fg.d3Force('collide', forceCollide(0).strength(0));
    } else {
      fg.d3Force('charge',
        forceManyBody().strength(CHARGE).distanceMax(SHELL_RADIUS * (maxHop + 2) * 2));
      linkForce?.distance?.((l) => {
        const bind = l.walk ? 1 : binding(l.weight);
        return LINK_BASE + Math.max(1, l.hop || 1) * 10 + (1 - bind) * 86 + (l.refuted ? 48 : 0);
      });
      linkForce?.strength?.((l) => (l.walk ? 0.82 : binding(l.weight) * 0.9));
      fg.d3Force('radial',
        forceRadial((n: unknown) => ((n as WebNode).hop || 0) * SHELL_RADIUS).strength(0.06));
      fg.d3Force('collide', forceCollide(NODE_REL_SIZE * 2.2).strength(1));
    }
    (fg.d3Force as (name: string, force: null) => void)('center', null);
    const controls = fg.controls();
    // Orbit: LMB rotate, MMB/RMB translate (move origin), wheel zoom.
    if (controls) {
      controls.noPan = false;
      controls.enablePan = true;
      controls.screenSpacePanning = true;
      controls.panSpeed = 1.35;
      controls.mouseButtons = {
        LEFT: MOUSE.ROTATE,
        MIDDLE: MOUSE.PAN,
        RIGHT: MOUSE.PAN,
      };
      // Keyboard fly is handled by useGraphFlyControls — don't let Orbit steal arrows.
    }
  }, [maxHop, beliefMode]);

  useEffect(() => {
    if (dim === '2d') return;
    configureForces3d();
  }, [dim, data.nodes.length, data.links.length, configureForces3d]);

  useEffect(() => {
    if (dim !== '2d' || !ref2d.current?.d3Force) return;
    const fg = ref2d.current;
    fg.d3Force?.(
      'charge',
      forceManyBody().strength(CHARGE * 0.85).distanceMax(600),
    );
    const linkForce = fg.d3Force?.('link') as {
      distance?: (fn: (l: WebEdge) => number) => unknown;
      strength?: (fn: (l: WebEdge) => number) => unknown;
    } | undefined;
    linkForce?.distance?.((l) => {
      const bind = l.walk ? 1 : binding(l.weight);
      return LINK_BASE + Math.max(1, l.hop || 1) * 8 + (1 - bind) * 72 + (l.refuted ? 40 : 0);
    });
    linkForce?.strength?.((l) => (l.walk ? 0.85 : binding(l.weight) * 0.92));
    fg.d3Force?.(
      'radial',
      forceRadial((n: unknown) => ((n as WebNode).hop || 0) * (SHELL_RADIUS * 0.85)).strength(0.05),
    );
    fg.d3Force?.('collide', forceCollide(5).strength(1));
  }, [dim, data.nodes.length, data.links.length]);

  useEffect(() => {
    if (size.width === 0 || size.height === 0) return;
    const key = `${dim}:${centerId}:${data.nodes.length}:${data.links.length}`;
    if (fittedKey.current === key) return;
    const t = setTimeout(() => {
      if (dim !== '2d') {
        configureForces3d();
        // Padding in screen px around the fitted bounds: room for labels without shrinking the web.
        ref3d.current?.zoomToFit(500, 24);
      } else {
        ref2d.current?.zoomToFit(400);
      }
      fittedKey.current = key;
    }, 480);
    return () => clearTimeout(t);
  }, [centerId, data.nodes.length, data.links.length, size.width, size.height, dim, configureForces3d]);

  function focusNode(node: WebNode) {
    if (node.x == null || node.y == null || node.z == null || !ref3d.current) return;
    const dist = 55 + Math.max(1, node.hop) * 12;
    ref3d.current.cameraPosition(
      { x: node.x + dist, y: node.y + dist * 0.35, z: node.z + dist },
      { x: node.x, y: node.y, z: node.z },
      550,
    );
  }

  function handleNodeClick(node: WebNode) {
    if (dim !== '2d') focusNode(node);
    if (!onNodeClick || node.id === centerId || node.id.length !== 32) return;
    if (clickTimer.current) {
      clearTimeout(clickTimer.current);
      clickTimer.current = null;
      onNodeClick(node.id);
      return;
    }
    clickTimer.current = setTimeout(() => {
      clickTimer.current = null;
    }, 280);
  }

  function handleNodeClick2d(n: WebNode) {
    if (!onNodeClick || n.id === centerId || n.id.length !== 32) return;
    if (clickTimer.current) {
      clearTimeout(clickTimer.current);
      clickTimer.current = null;
      onNodeClick(n.id);
      return;
    }
    clickTimer.current = setTimeout(() => {
      clickTimer.current = null;
    }, 280);
  }

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      <GraphLegend
        dim={dim}
        onDimChange={onDimChange}
        hops={hops}
        hopsMax={hopsMax}
        onHopsChange={onHopsChange}
        fanout={fanout}
        fanoutMax={fanoutMax}
        onFanoutChange={onFanoutChange}
        maxNodes={maxNodes}
        maxNodesMax={maxNodesMax}
        onMaxNodesChange={onMaxNodesChange}
        toolbar={toolbar}
        nodeCount={data.nodes.length}
        linkCount={data.links.length}
        maxHop={maxHop}
        expanded={expanded}
        relations={relations}
        hidden={hidden}
        onToggleRelation={toggleRelation}
        beliefMode={beliefMode}
        walkSteps={walkPath.length}
      />
      <div
        className={fill ? `${styles.shell} ${styles.shellFill}` : styles.shell}
        ref={shellRef}
        tabIndex={0}
        onPointerDown={() => shellRef.current?.focus({ preventScroll: true })}
        aria-label="Consensus web viewer"
      >
        {webGlReady && dim !== '2d' ? (
          <Graph3D
            graphRef={ref3d}
            size={size}
            data={data}
            palette={palette}
            nodeVisuals={nodeVisuals}
            labelledIds={labelledIds}
            centerId={centerId}
            labelCache={labelCache}
            labelMaterials={labelMaterials}
            beliefMode={beliefMode}
            onNodeClick={handleNodeClick}
          />
        ) : null}
        {size.width > 0 && size.height > 0 && dim === '2d' ? (
          <Graph2D
            graphRef={ref2d}
            size={size}
            data={data}
            palette={palette}
            nodeVisuals={nodeVisuals}
            labelledIds={labelledIds}
            centerId={centerId}
            onNodeClick={handleNodeClick2d}
          />
        ) : null}
      </div>
    </div>
  );
}
