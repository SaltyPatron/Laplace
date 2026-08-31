import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Field, Input, Muted, SegmentedControl } from '@ui';
import { forceCollide, forceManyBody, forceRadial } from 'd3-force-3d';
import ForceGraph2D from 'react-force-graph-2d';
import ForceGraph3D from 'react-force-graph-3d';
import { CanvasTexture, LinearFilter, MOUSE, Object3D, Sprite, SpriteMaterial, type Camera, type Vector3 } from 'three';
import type { ExploreConsensusRow } from '../types';
import type { WalkPathNode } from '../store';
import styles from './ConsensusGraph.module.css';
import { useGraphFlyControls } from './useGraphFlyControls';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';

export interface WebNode {
  id: string;
  label: string;
  hop: number;
  walk?: boolean;
  x?: number;
  y?: number;
  z?: number;
  fx?: number;
  fy?: number;
  fz?: number;
}

export interface WebEdge {
  source: string;
  target: string;
  type: string;
  mu: number;
  witnesses: number;
  hop: number;
  walk?: boolean;
}

export interface WebGraph {
  nodes: WebNode[];
  edges: WebEdge[];
}

type Dim = '2d' | '3d';

/** World units — stay small vs link length so zoom-in is readable. */
const NODE_REL_SIZE = 0.9;
const SHELL_RADIUS = 78;
const LINK_BASE = 52;
const CHARGE = -420;

const HOP_COLOR = ['#4f8cff', '#3ecf8e', '#e8b339', '#9b7bff', '#f07178'];

function hopColor(hop: number, walk?: boolean): string {
  if (walk) return '#3ecf8e';
  return HOP_COLOR[Math.min(Math.max(hop, 0), HOP_COLOR.length - 1)];
}

function clamp(n: number, lo: number, hi: number) {
  return Math.min(hi, Math.max(lo, n));
}

/**
 * A node's name, drawn as a camera-facing sprite.
 *
 * The 3-D web previously carried labels only in the hover tooltip, so the graph
 * opened as an unreadable constellation of dots — you had to hunt with the
 * pointer to learn what anything was, while the 2-D projection labelled itself
 * once zoomed in. Names are drawn for real so the web is legible on arrival.
 */
const LABEL_FONT_PX = 44;
const labelCache = new Map<string, Sprite>();

function labelSprite(text: string, color: string): Sprite | null {
  const key = `${text}\0${color}`;
  const hit = labelCache.get(key);
  if (hit) return hit.clone() as Sprite;

  const canvas = document.createElement('canvas');
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  const font = `${LABEL_FONT_PX}px system-ui, sans-serif`;
  ctx.font = font;
  const width = Math.ceil(ctx.measureText(text).width) + 16;
  canvas.width = width;
  canvas.height = Math.ceil(LABEL_FONT_PX * 1.4);

  // Re-set after the resize — sizing a canvas resets its 2-D state.
  ctx.font = font;
  ctx.textBaseline = 'middle';
  ctx.fillStyle = 'rgba(11,18,32,0.72)';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = color;
  ctx.fillText(text, 8, canvas.height / 2);

  const texture = new CanvasTexture(canvas);
  texture.minFilter = LinearFilter;
  const sprite = new Sprite(new SpriteMaterial({ map: texture, transparent: true, depthWrite: false }));
  // World units per canvas pixel. Sized against SHELL_RADIUS (78) rather than
  // by eye: at 0.09 a name rendered ~4 world units tall against a ~160-unit
  // span and was unreadable at the fitted camera distance.
  const scale = 0.22;
  sprite.scale.set(canvas.width * scale, canvas.height * scale, 1);
  labelCache.set(key, sprite);
  return sprite.clone() as Sprite;
}

/** Map eff_μ into a 0–1 tension for stroke/opacity (chess-heatmap style). */
function tension(mu: number, maxMu: number): number {
  if (!(maxMu > 0) || !Number.isFinite(mu)) return 0.25;
  return Math.min(1, Math.max(0.12, mu / maxMu));
}

type Fg3dApi = {
  zoomToFit: (ms?: number, padding?: number) => void;
  cameraPosition: (pos: { x: number; y: number; z: number }, lookAt?: { x: number; y: number; z: number }, ms?: number) => void;
  camera: () => Camera;
  d3Force: (name: string, force?: unknown) => unknown;
  pauseAnimation?: () => void;
  resumeAnimation?: () => void;
  _destructor?: () => void;
  controls: () => {
    target: Vector3;
    update?: () => void;
    enablePan?: boolean;
    screenSpacePanning?: boolean;
    panSpeed?: number;
    mouseButtons?: { LEFT?: number; MIDDLE?: number; RIGHT?: number };
    listenToKeyEvents?: (dom: HTMLElement | Window) => void;
    noPan?: boolean;
  };
};

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
  dim = '3d',
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
  const ref2d = useRef<{
    zoomToFit: (ms?: number) => void;
    d3Force?: (name: string, force?: unknown) => unknown;
  } | null>(null);
  const ref3d = useRef<Fg3dApi | null>(null);
  const shellRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const fittedKey = useRef<string>('');
  const clickTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useGraphFlyControls(shellRef, ref3d, dim === '3d' && size.width > 0);
  const webGlReady = useDeferredWebGlMount(dim === '3d' && size.width > 0 && size.height > 0);

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

  const data = useMemo(
    () => (web ? fromWeb(web, walkPath) : fromStar(centerId, centerLabel, edges, walkPath)),
    [web, centerId, centerLabel, edges, walkPath],
  );

  const maxMu = useMemo(
    () => Math.max(1, ...data.links.map((l) => l.mu)),
    [data.links],
  );

  const maxHop = useMemo(
    () => Math.max(0, ...data.nodes.map((n) => n.hop)),
    [data.nodes],
  );

  const expanded = web != null && web.nodes.length > 0;

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

    fg.d3Force(
      'charge',
      forceManyBody()
        .strength(CHARGE)
        .distanceMax(SHELL_RADIUS * (maxHop + 2) * 2),
    );
    const linkForce = fg.d3Force('link') as {
      distance?: (fn: (l: WebEdge) => number) => unknown;
      strength?: (n: number) => unknown;
    } | undefined;
    linkForce?.distance?.((l) => LINK_BASE + Math.max(1, l.hop || 1) * 22);
    linkForce?.strength?.(0.35);
    fg.d3Force(
      'radial',
      forceRadial((n: unknown) => ((n as WebNode).hop || 0) * SHELL_RADIUS).strength(0.9),
    );
    fg.d3Force('collide', forceCollide(NODE_REL_SIZE * 2.2).strength(1));
    // Drop the default centering force so pan/recenter isn't fighting the layout.
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
  }, [maxHop]);

  useEffect(() => {
    if (dim !== '3d') return;
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
      strength?: (n: number) => unknown;
    } | undefined;
    linkForce?.distance?.((l) => LINK_BASE + Math.max(1, l.hop || 1) * 18);
    linkForce?.strength?.(0.4);
    fg.d3Force?.(
      'radial',
      forceRadial((n: unknown) => ((n as WebNode).hop || 0) * (SHELL_RADIUS * 0.85)).strength(0.75),
    );
    fg.d3Force?.('collide', forceCollide(5).strength(1));
  }, [dim, data.nodes.length, data.links.length]);

  useEffect(() => {
    if (size.width === 0 || size.height === 0) return;
    const key = `${dim}:${centerId}:${data.nodes.length}:${data.links.length}`;
    if (fittedKey.current === key) return;
    const t = setTimeout(() => {
      if (dim === '3d') {
        configureForces3d();
        // Padding is in screen px around the fitted bounds. At 140 a 20-node
        // web sat as a faint speck in the middle of a large panel; enough room
        // that labels do not clip, not so much that the graph is unreadable.
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
    if (dim === '3d') focusNode(node);
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

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      <div className={styles.toolbar}>
        {onDimChange ? (
          <SegmentedControl
            value={dim}
            onValueChange={(v) => onDimChange(v as Dim)}
            options={['3d', '2d']}
            label="Projection"
          />
        ) : null}
        {onHopsChange && hops != null ? (
          <Field label="hops" layout="row" htmlFor="web-hops">
            <Input
              id="web-hops"
              type="number"
              min={1}
              max={hopsMax}
              value={hops}
              onChange={(e) => onHopsChange(clamp(Number(e.target.value) || 1, 1, hopsMax))}
              aria-label="Hop depth"
            />
          </Field>
        ) : null}
        {onFanoutChange && fanout != null ? (
          <Field label="fanout" layout="row" htmlFor="web-fanout">
            <Input
              id="web-fanout"
              type="number"
              min={2}
              max={fanoutMax}
              value={fanout}
              onChange={(e) => onFanoutChange(clamp(Number(e.target.value) || 8, 2, fanoutMax))}
              aria-label="Fanout per hop"
            />
          </Field>
        ) : null}
        {onMaxNodesChange && maxNodes != null ? (
          <Field label="capacity" layout="row" htmlFor="web-max-nodes">
            <Input
              id="web-max-nodes"
              type="number"
              min={32}
              max={maxNodesMax}
              step={32}
              value={maxNodes}
              onChange={(e) => onMaxNodesChange(clamp(Number(e.target.value) || 32, 32, maxNodesMax))}
              aria-label="Maximum graph nodes"
            />
          </Field>
        ) : null}
        {toolbar}
        <Muted className={styles.legend}>
          {expanded
            ? `${maxHop}-hop · ${data.nodes.length}n / ${data.links.length}e · μ = strand tension`
            : `1-hop · ${data.nodes.length}n · μ = strand tension`}
          {' · '}
          {dim === '3d'
            ? 'WASD/arrows move · Q/E turn · PgUp/PgDn up/down · Home origin · End antipode · Shift sprint · LMB orbit · MMB/RMB pan · click recenter · dbl-click open'
            : 'drag pan · scroll zoom · double-click open'}
        </Muted>
      </div>
      {walkPath.length > 0 ? (
        <Muted className={styles.overlayNote}>Walk path overlay ({walkPath.length} steps)</Muted>
      ) : null}
      <div
        className={fill ? `${styles.shell} ${styles.shellFill}` : styles.shell}
        ref={shellRef}
        tabIndex={0}
        onPointerDown={() => shellRef.current?.focus({ preventScroll: true })}
        aria-label="Consensus web viewer"
      >
        {webGlReady && dim === '3d' ? (
          <ForceGraph3D
            ref={ref3d as never}
            width={size.width}
            height={size.height}
            graphData={data}
            backgroundColor="#0b1220"
            showNavInfo
            controlType="orbit"
            enableNavigationControls
            nodeRelSize={NODE_REL_SIZE}
            nodeVal={1}
            nodeOpacity={0.95}
            nodeResolution={10}
            linkDirectionalParticles={0}
            linkOpacity={0.45}
            rendererConfig={{
              antialias: true,
              powerPreference: 'default',
              failIfMajorPerformanceCaveat: false,
            }}
            linkWidth={(l: WebEdge) => 0.06 + tension(l.mu, maxMu) * 0.55}
            linkColor={(l: WebEdge) => {
              if (l.walk) return '#3ecf8e';
              const t = tension(l.mu, maxMu);
              return `rgba(79, 140, 255, ${0.2 + 0.65 * t})`;
            }}
            nodeLabel={(n: WebNode) => `${n.label} · hop ${n.hop}`}
            linkLabel={(l: WebEdge) => `${l.type} μ=${l.mu.toFixed(1)} · ${l.witnesses} wit`}
            nodeColor={(n: WebNode) => hopColor(n.hop, n.walk || n.id === centerId)}
            nodeThreeObjectExtend
            nodeThreeObject={(n: WebNode) => {
              const label = n.label.length > 22 ? `${n.label.slice(0, 21)}…` : n.label;
              const sprite = labelSprite(label, n.id === centerId ? '#ffffff' : 'rgba(215,224,240,0.92)');
              // `nodeThreeObjectExtend` still needs an Object3D when a label
              // cannot be rasterised (no 2-D context); an empty group adds
              // nothing and leaves the default sphere alone.
              if (!sprite) return new Object3D();
              sprite.position.set(0, NODE_REL_SIZE * 3.2, 0);
              return sprite;
            }}
            onNodeClick={(n: WebNode) => handleNodeClick(n)}
            onNodeDragEnd={(n: WebNode) => {
              // Pin after drag so layout doesn't yank the new origin back.
              (n as WebNode & { fx?: number; fy?: number; fz?: number }).fx = n.x;
              (n as WebNode & { fx?: number; fy?: number; fz?: number }).fy = n.y;
              (n as WebNode & { fx?: number; fy?: number; fz?: number }).fz = n.z;
            }}
            cooldownTicks={120}
            d3AlphaDecay={0.028}
            d3VelocityDecay={0.32}
          />
        ) : null}
        {size.width > 0 && size.height > 0 && dim === '2d' ? (
          <ForceGraph2D
            ref={ref2d as never}
            width={size.width}
            height={size.height}
            graphData={data}
            enablePanInteraction
            enableZoomInteraction
            nodeLabel={(n: WebNode) => `${n.label} · hop ${n.hop}`}
            linkLabel={(l: WebEdge) => `${l.type} μ=${l.mu.toFixed(1)}`}
            linkWidth={(l: WebEdge) => 0.35 + tension(l.mu, maxMu) * 1.4}
            linkColor={(l: WebEdge) => (l.walk ? '#3ecf8e' : hopColor(l.hop))}
            onNodeClick={(n: WebNode) => {
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
            }}
            nodeCanvasObject={(node: WebNode, ctx, globalScale) => {
              // World-space radius (not /globalScale) — zoom-in reveals gaps instead of ballooning.
              const r = node.hop === 0 ? 3.2 : Math.max(1.6, 2.6 - node.hop * 0.25);
              const x = (node as WebNode & { x: number; y: number }).x;
              const y = (node as WebNode & { x: number; y: number }).y;
              ctx.beginPath();
              ctx.arc(x, y, r, 0, 2 * Math.PI);
              ctx.fillStyle = hopColor(node.hop, node.walk || node.id === centerId);
              ctx.fill();
              if (globalScale >= 1.15) {
                const label = node.label.length > 18 ? `${node.label.slice(0, 17)}…` : node.label;
                const fontSize = 10 / globalScale;
                ctx.font = `${fontSize}px sans-serif`;
                ctx.fillStyle = 'rgba(215,224,240,0.9)';
                ctx.fillText(label, x + r + 1.2, y + fontSize * 0.35);
              }
            }}
            nodePointerAreaPaint={(node: WebNode, color, ctx) => {
              const r = node.hop === 0 ? 4 : 3;
              const x = (node as WebNode & { x: number; y: number }).x;
              const y = (node as WebNode & { x: number; y: number }).y;
              ctx.beginPath();
              ctx.arc(x, y, r, 0, 2 * Math.PI);
              ctx.fillStyle = color;
              ctx.fill();
            }}
            cooldownTicks={100}
            d3AlphaDecay={0.03}
            d3VelocityDecay={0.3}
          />
        ) : null}
      </div>
    </div>
  );
}

function fromWeb(web: WebGraph, walkPath: WalkPathNode[]) {
  const unique = new Map<string, WebNode>();
  for (const n of web.nodes) {
    if (unique.has(n.id)) continue;
    unique.set(n.id, {
      ...n,
      walk: walkIdsHas(walkPath, n.id),
      // Anchor seed at origin so hop shells stay true radii.
      ...(n.hop === 0 ? { fx: 0, fy: 0, fz: 0, x: 0, y: 0, z: 0 } : {}),
    });
  }
  const nodes = [...unique.values()];
  const seen = new Set(nodes.map((n) => n.id));
  for (const step of walkPath) {
    if (!seen.has(step.idHex)) {
      nodes.push({ id: step.idHex, label: step.label, hop: 0, walk: true });
      seen.add(step.idHex);
    }
  }
  const links: WebEdge[] = web.edges
    .filter((e) => e.source !== e.target)
    .map((e) => ({ ...e, walk: false }));
  for (let i = 1; i < walkPath.length; i++) {
    if (walkPath[i - 1].idHex === walkPath[i].idHex) continue;
    links.push({
      source: walkPath[i - 1].idHex,
      target: walkPath[i].idHex,
      type: 'walk',
      mu: 400,
      witnesses: 0,
      hop: 0,
      walk: true,
    });
  }
  return { nodes, links };
}

function fromStar(
  centerId: string,
  centerLabel: string,
  edges: ExploreConsensusRow[],
  walkPath: WalkPathNode[],
) {
  const nodes = new Map<string, WebNode>();
  nodes.set(centerId, {
    id: centerId,
    label: centerLabel,
    hop: 0,
    walk: walkIdsHas(walkPath, centerId),
    fx: 0,
    fy: 0,
    fz: 0,
    x: 0,
    y: 0,
    z: 0,
  });
  const links: WebEdge[] = [];
  for (const e of edges) {
    const id = e.entity_id_hex || e.entity_label;
    if (id === centerId) continue;
    if (!nodes.has(id)) {
      nodes.set(id, { id, label: e.entity_label || e.type, hop: 1, walk: walkIdsHas(walkPath, id) });
    }
    const source = e.direction === 'in' ? id : centerId;
    const target = e.direction === 'in' ? centerId : id;
    links.push({
      source,
      target,
      type: e.type,
      mu: e.eff_mu,
      witnesses: e.witnesses,
      hop: 1,
      walk: false,
    });
  }
  for (const step of walkPath) {
    if (!nodes.has(step.idHex)) nodes.set(step.idHex, { id: step.idHex, label: step.label, hop: 0, walk: true });
  }
  for (let i = 1; i < walkPath.length; i++) {
    if (walkPath[i - 1].idHex === walkPath[i].idHex) continue;
    links.push({
      source: walkPath[i - 1].idHex,
      target: walkPath[i].idHex,
      type: 'walk',
      mu: 400,
      witnesses: 0,
      hop: 0,
      walk: true,
    });
  }
  return { nodes: [...nodes.values()], links };
}

function walkIdsHas(walkPath: WalkPathNode[], id: string) {
  return walkPath.some((n) => n.idHex === id);
}
