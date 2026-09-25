import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ComponentType, type ReactNode } from 'react';
import { Field, Input, Muted, SegmentedControl } from '@ui';
import { forceCollide, forceManyBody, forceRadial } from 'd3-force-3d';
import { CanvasTexture, LinearFilter, MOUSE, Object3D, Sprite, SpriteMaterial, type Camera, type Vector3 } from 'three';
import type { ExploreConsensusRow } from '../types';
import type { WalkPathNode } from '../store';
import { ensureVisualizationContrast, lerpColor, rgba, useVisualizationPalette, type VisualizationPalette } from '../visualizationPalette';
import styles from './ConsensusGraph.module.css';
import { useGraphFlyControls } from './useGraphFlyControls';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';

const ForceGraph2D = lazy(() => import('react-force-graph-2d').then((m) => ({ default: m.default }))) as unknown as ComponentType<any>;
const ForceGraph3D = lazy(() => import('react-force-graph-3d').then((m) => ({ default: m.default }))) as unknown as ComponentType<any>;

export interface WebNode {
  id: string;
  label: string;
  hop: number;
  walk?: boolean;
  beliefX?: number;
  beliefY?: number;
  beliefZ?: number;
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
  /** Canonical OP4 complete weight: signed Glicko expectation in [-1, 1]. */
  weight: number;
  /** Canonical optimistic-bound verdict from consensus.refuted(rating, rd). */
  refuted: boolean;
  rating: number;
  rd: number;
  volatility: number;
  walk?: boolean;
}

export interface WebGraph {
  nodes: WebNode[];
  edges: WebEdge[];
}

type Dim = 'belief' | '2d' | '3d';
type GraphData = { nodes: WebNode[]; links: WebEdge[] };

/** World units — stay small vs link length so zoom-in is readable. */
const NODE_REL_SIZE = 0.9;
const SHELL_RADIUS = 78;
const LINK_BASE = 52;
const CHARGE = -420;

function hopColor(hop: number, walk: boolean | undefined, palette: VisualizationPalette): string {
  const colors = [palette.signal, palette.steel, palette.primary, palette.muted, palette.error];
  const candidate = walk ? palette.signal : colors[Math.min(Math.max(hop, 0), colors.length - 1)];
  return ensureVisualizationContrast(candidate, palette.background, palette.primary);
}

function clamp(n: number, lo: number, hi: number) {
  return Math.min(hi, Math.max(lo, n));
}

function hash32(text: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

/**
 * Deterministic point on a 3-D shell. ForceGraph mutates graphData coordinates,
 * so this seed is intentionally regenerated from identity whenever the 3-D
 * renderer gets its own copy. A prior 2-D simulation can therefore never hand
 * 3-D a set of z=0 nodes and trap the force system in a planar symmetry.
 */
function volumetricSeed(id: string, hop: number): [number, number, number] {
  const u = (hash32(`${id}\0u`) + 0.5) / 0x100000000;
  const v = (hash32(`${id}\0v`) + 0.5) / 0x100000000;
  const z = 1 - 2 * u;
  const phi = 2 * Math.PI * v;
  const xy = Math.sqrt(Math.max(0, 1 - z * z));
  const radius = Math.max(1, hop) * SHELL_RADIUS;
  return [
    Math.cos(phi) * xy * radius,
    Math.sin(phi) * xy * radius,
    z * radius,
  ];
}

/**
 * react-force-graph mutates both node coordinates and link endpoints in place.
 * Never give the 2-D and 3-D engines the same objects: toggling dimensions used
 * to leave the 3-D engine starting from the 2-D sheet it had just inherited.
 */
export function graphForDimension(base: GraphData, dim: Dim, centerId: string): GraphData {
  const spectral = base.nodes.filter((n) =>
    Number.isFinite(n.beliefX) && Number.isFinite(n.beliefY) && Number.isFinite(n.beliefZ));
  const beliefReady = dim === 'belief' && spectral.length > 0;
  const center = spectral.find((n) => n.id === centerId) ?? spectral[0];
  const cx = center?.beliefX ?? 0, cy = center?.beliefY ?? 0, cz = center?.beliefZ ?? 0;
  let spectralRadius = 0;
  if (beliefReady)
    for (const n of spectral)
      spectralRadius = Math.max(spectralRadius, Math.hypot(
        (n.beliefX ?? 0) - cx, (n.beliefY ?? 0) - cy, (n.beliefZ ?? 0) - cz));
  const targetRadius = Math.max(
    SHELL_RADIUS * 1.5,
    Math.min(SHELL_RADIUS * 5, SHELL_RADIUS * Math.sqrt(Math.max(2, spectral.length)) / 2),
  );
  // Translation and one uniform display scale preserve the native spectral geometry.
  const spectralScale = spectralRadius > 0 ? targetRadius / spectralRadius : 1;

  const nodes = base.nodes.map((node) => {
    const clean: WebNode = {
      id: node.id, label: node.label, hop: node.hop, walk: node.walk,
      beliefX: node.beliefX, beliefY: node.beliefY, beliefZ: node.beliefZ,
    };
    if (beliefReady) {
      if (Number.isFinite(node.beliefX) && Number.isFinite(node.beliefY) && Number.isFinite(node.beliefZ)) {
        const x = ((node.beliefX ?? 0) - cx) * spectralScale;
        const y = ((node.beliefY ?? 0) - cy) * spectralScale;
        const z = ((node.beliefZ ?? 0) - cz) * spectralScale;
        return { ...clean, x, y, z, fx: x, fy: y, fz: z };
      }
      // A vertex with no positive conductance is outside the positive belief
      // manifold. Keep it visible, but scatter it deterministically beyond the
      // coherent spectral basin rather than pinning every dead end at (0,0,0).
      const [sx, sy, sz] = volumetricSeed(node.id, node.hop);
      const sr = Math.max(1e-9, Math.hypot(sx, sy, sz));
      const outerRadius = targetRadius * (1.18 + Math.min(3, Math.max(0, node.hop)) * 0.12);
      const outerScale = outerRadius / sr;
      const x = sx * outerScale;
      const y = sy * outerScale;
      const z = sz * outerScale;
      return { ...clean, x, y, z, fx: x, fy: y, fz: z };
    }
    if (node.id === centerId) {
      return dim !== '2d'
        ? { ...clean, x: 0, y: 0, z: 0, fx: 0, fy: 0, fz: 0 }
        : { ...clean, x: 0, y: 0, fx: 0, fy: 0 };
    }
    if (dim !== '2d') {
      const [x, y, z] = volumetricSeed(node.id, node.hop);
      return { ...clean, x, y, z };
    }
    return clean;
  });
  return { nodes, links: base.links.map((link) => ({ ...link })) };
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
const MAX_VISIBLE_LABELS = 48;

interface NodeVisual {
  color: string;
  val: number;
  support: number;
  opposition: number;
  witnesses: number;
}

function endpointId(endpoint: string | WebNode): string {
  return typeof endpoint === 'string' ? endpoint : endpoint.id;
}

/**
 * Nodes are not anonymous topology dots. Summarize the canonical signed Glicko
 * testimony touching each retained entity:
 *   hue  = positive support vs negative/refuted opposition
 *   mass = witness volume (log-scaled) plus standing magnitude
 *
 * This is presentation only. The belief coordinates still come from the native
 * normalized-Laplacian projection; witness counts are not multiplied back into
 * the Glicko edge weight.
 */
function buildNodeVisuals(
  data: GraphData,
  centerId: string,
  palette: VisualizationPalette,
): Map<string, NodeVisual> {
  const raw = new Map<string, { support: number; opposition: number; witnesses: number }>();
  for (const node of data.nodes)
    raw.set(node.id, { support: 0, opposition: 0, witnesses: 0 });

  for (const edge of data.links) {
    const source = endpointId(edge.source as unknown as string | WebNode);
    const target = endpointId(edge.target as unknown as string | WebNode);
    const signed = Number.isFinite(edge.weight) ? edge.weight : 0;
    const opposition = edge.refuted || signed < 0 ? Math.abs(signed) : 0;
    const support = edge.refuted ? 0 : Math.max(0, signed);
    const witnesses = Number.isFinite(edge.witnesses) ? Math.max(0, edge.witnesses) : 0;
    for (const id of [source, target]) {
      const value = raw.get(id);
      if (!value) continue;
      value.support += support;
      value.opposition += opposition;
      value.witnesses += witnesses;
    }
  }

  const visuals = new Map<string, NodeVisual>();
  for (const node of data.nodes) {
    const value = raw.get(node.id) ?? { support: 0, opposition: 0, witnesses: 0 };
    const standing = value.support + value.opposition;
    const balance = standing > 0 ? (value.support - value.opposition) / standing : 0;
    const intensity = Math.min(1, standing / 3);
    let color = palette.muted;
    if (node.id === centerId || node.walk) {
      color = palette.signal;
    } else if (standing > 0 && balance < -0.08) {
      color = lerpColor(palette.steel, palette.error, Math.min(1, Math.abs(balance) * 0.55 + intensity * 0.45));
    } else if (standing > 0 && balance > 0.08) {
      color = lerpColor(palette.steel, palette.signal, Math.min(1, balance * 0.55 + intensity * 0.45));
    } else if (standing > 0) {
      color = palette.steel;
    }
    color = ensureVisualizationContrast(color, palette.background, palette.primary);

    const evidenceMass = Math.log2(value.witnesses + 1);
    const val = node.id === centerId
      ? 8
      : Math.max(1, Math.min(10, 1 + evidenceMass * 0.72 + Math.min(3, standing)));

    visuals.set(node.id, {
      color,
      val,
      support: value.support,
      opposition: value.opposition,
      witnesses: value.witnesses,
    });
  }
  return visuals;
}

/** Label height as a fraction of the viewport height. */
const LABEL_SCREEN_HEIGHT = 0.034;

function labelSprite(
  text: string,
  color: string,
  background: string,
  cache: Map<string, Sprite>,
  materials: Set<SpriteMaterial>,
): Sprite | null {
  const key = `${text}\0${color}\0${background}`;
  const hit = cache.get(key);
  if (hit) return hit.clone() as Sprite;

  const canvas = document.createElement('canvas');
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  const font = `${LABEL_FONT_PX}px system-ui, sans-serif`;
  ctx.font = font;
  const width = Math.ceil(ctx.measureText(text).width) + 16;
  canvas.width = width;
  canvas.height = Math.ceil(LABEL_FONT_PX * 1.4);
  ctx.font = font;
  ctx.textBaseline = 'middle';
  ctx.fillStyle = rgba(background, 0.78);
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = color;
  ctx.fillText(text, 8, canvas.height / 2);

  const texture = new CanvasTexture(canvas);
  texture.minFilter = LinearFilter;
  // Labels keep one on-screen size at every zoom (sizeAttenuation off, scale in
  // viewport units). A world-sized label grows with the camera, so in the dense belief
  // cluster every name stacked into one unreadable pile; now zooming in separates them.
  const material = new SpriteMaterial({
    map: texture, transparent: true, depthWrite: false, sizeAttenuation: false,
  });
  materials.add(material);
  const sprite = new Sprite(material);
  sprite.center.set(0.5, 0);
  sprite.scale.set(LABEL_SCREEN_HEIGHT * (canvas.width / canvas.height), LABEL_SCREEN_HEIGHT, 1);
  cache.set(key, sprite);
  return sprite.clone() as Sprite;
}

/** Positive signed Glicko standing binds the layout; neutral/refuted strands do not. */
function binding(weight: number): number {
  if (!Number.isFinite(weight)) return 0;
  return Math.min(1, Math.max(0, weight));
}

/** Witness mass is visual evidence only; Glicko already folded it into standing. */
function witnessMass(witnesses: number): number {
  if (!(witnesses > 0) || !Number.isFinite(witnesses)) return 0;
  return Math.min(1, Math.log2(witnesses + 1) / 6);
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
  const ref2d = useRef<{
    zoomToFit: (ms?: number) => void;
    d3Force?: (name: string, force?: unknown) => unknown;
  } | null>(null);
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

  const baseData = useMemo(
    () => (web ? fromWeb(web, walkPath) : fromStar(centerId, centerLabel, edges, walkPath)),
    [web, centerId, centerLabel, edges, walkPath],
  );
  const data = useMemo(
    () => graphForDimension(baseData, dim, centerId),
    [baseData, dim, centerId],
  );

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

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      <div className={styles.toolbar}>
        {onDimChange ? (
          <SegmentedControl
            value={dim}
            onValueChange={(v) => onDimChange(v as Dim)}
            options={['belief', '3d', '2d']}
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
              aria-label="Fanout per parent"
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
            ? beliefMode
              ? `${maxHop}-hop · ${data.nodes.length}n / ${data.links.length}e · native normalized-Laplacian belief geometry · node hue = support/refutation · node mass = witnesses · refutations do not bind`
              : `${maxHop}-hop · ${data.nodes.length}n / ${data.links.length}e · force projection · node hue = support/refutation · node mass = witnesses · signed Glicko drives attraction`
            : `1-hop · ${data.nodes.length}n · expand for native belief geometry`}
          {' · '}
          {dim !== '2d'
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
        {webGlReady && dim !== '2d' ? (
          <Suspense fallback={<Muted>Loading 3-D renderer…</Muted>}>
          <ForceGraph3D
            ref={ref3d as never}
            width={size.width}
            height={size.height}
            graphData={data}
            backgroundColor={palette.background}
            showNavInfo
            controlType="orbit"
            enableNavigationControls
            linkDirectionalParticles={0}
            linkOpacity={0.45}
            rendererConfig={{
              antialias: data.nodes.length < 512,
              powerPreference: 'high-performance',
              failIfMajorPerformanceCaveat: false,
            }}
            linkWidth={(l: WebEdge) => 0.05 + binding(l.weight) * 0.48 + witnessMass(l.witnesses) * 0.22}
            linkColor={(l: WebEdge) => {
              if (l.walk) return palette.signal;
              if (l.refuted || l.weight < 0) return rgba(palette.error, 0.42 + 0.42 * Math.abs(l.weight));
              return rgba(palette.steel, 0.14 + 0.8 * binding(l.weight));
            }}
            nodeLabel={(n: WebNode) => {
              const visual = nodeVisuals.get(n.id);
              return visual
                ? `${n.label} · hop ${n.hop} · +${visual.support.toFixed(2)} / −${visual.opposition.toFixed(2)} standing · ${visual.witnesses.toLocaleString()} witnesses`
                : `${n.label} · hop ${n.hop}`;
            }}
            linkLabel={(l: WebEdge) => `${l.type} · Glicko ${l.weight.toFixed(3)} · rating ${l.rating.toFixed(1)} · RD ${l.rd.toFixed(1)} · σ ${l.volatility.toFixed(3)} · μ=${l.mu.toFixed(1)} · ${l.witnesses} wit${l.refuted ? ' · refuted' : ''}`}
            nodeRelSize={1.55}
            nodeVal={(n: WebNode) => nodeVisuals.get(n.id)?.val ?? 1}
            nodeColor={(n: WebNode) => nodeVisuals.get(n.id)?.color ?? palette.muted}
            nodeThreeObjectExtend
            nodeThreeObject={(n: WebNode) => {
              const root = new Object3D();
              if (labelledIds.has(n.id)) {
                const label = n.label.length > 22 ? `${n.label.slice(0, 21)}…` : n.label;
                const sprite = labelSprite(
                  label,
                  n.id === centerId ? palette.primary : palette.muted,
                  palette.background,
                  labelCache.current,
                  labelMaterials.current,
                );
                if (sprite) {
                  const visual = nodeVisuals.get(n.id);
                  const radius = 1.55 * Math.cbrt(Math.max(1, visual?.val ?? 1));
                  sprite.position.set(0, radius + NODE_REL_SIZE * 2.4, 0);
                  root.add(sprite);
                }
              }
              return root;
            }}
            onNodeClick={(n: WebNode) => handleNodeClick(n)}
            onNodeDragEnd={(n: WebNode) => {
              // Pin after drag so layout doesn't yank the new origin back.
              (n as WebNode & { fx?: number; fy?: number; fz?: number }).fx = n.x;
              (n as WebNode & { fx?: number; fy?: number; fz?: number }).fy = n.y;
              (n as WebNode & { fx?: number; fy?: number; fz?: number }).fz = n.z;
            }}
            cooldownTicks={beliefMode ? 0 : 120}
            d3AlphaDecay={0.028}
            d3VelocityDecay={0.32}
          />
          </Suspense>
        ) : null}
        {size.width > 0 && size.height > 0 && dim === '2d' ? (
          <Suspense fallback={<Muted>Loading 2-D renderer…</Muted>}>
          <ForceGraph2D
            ref={ref2d as never}
            width={size.width}
            height={size.height}
            graphData={data}
            backgroundColor={palette.background}
            enablePanInteraction
            enableZoomInteraction
            nodeLabel={(n: WebNode) => {
              const visual = nodeVisuals.get(n.id);
              return visual
                ? `${n.label} · hop ${n.hop} · +${visual.support.toFixed(2)} / −${visual.opposition.toFixed(2)} standing · ${visual.witnesses.toLocaleString()} witnesses`
                : `${n.label} · hop ${n.hop}`;
            }}
            linkLabel={(l: WebEdge) => `${l.type} · Glicko ${l.weight.toFixed(3)} · rating ${l.rating.toFixed(1)} · RD ${l.rd.toFixed(1)} · σ ${l.volatility.toFixed(3)} · μ=${l.mu.toFixed(1)} · ${l.witnesses} wit${l.refuted ? ' · refuted' : ''}`}
            linkWidth={(l: WebEdge) => 0.3 + binding(l.weight) * 1.25 + witnessMass(l.witnesses) * 0.65}
            linkColor={(l: WebEdge) => {
              if (l.walk) return palette.signal;
              if (l.refuted || l.weight < 0) return palette.error;
              return rgba(palette.steel, 0.22 + 0.72 * binding(l.weight));
            }}
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
            nodeCanvasObject={(node: WebNode, ctx: CanvasRenderingContext2D, globalScale: number) => {
              // World-space radius (not /globalScale) — zoom-in reveals gaps instead of ballooning.
              const visual = nodeVisuals.get(node.id);
              const r = 1.7 + Math.cbrt(Math.max(1, visual?.val ?? 1)) * 1.15;
              const x = (node as WebNode & { x: number; y: number }).x;
              const y = (node as WebNode & { x: number; y: number }).y;
              ctx.beginPath();
              ctx.arc(x, y, r, 0, 2 * Math.PI);
              ctx.fillStyle = visual?.color ?? hopColor(node.hop, node.walk || node.id === centerId, palette);
              ctx.fill();
              if (globalScale >= 1.15) {
                const label = node.label.length > 18 ? `${node.label.slice(0, 17)}…` : node.label;
                const fontSize = 10 / globalScale;
                ctx.font = `${fontSize}px sans-serif`;
                ctx.fillStyle = palette.muted;
                ctx.fillText(label, x + r + 1.2, y + fontSize * 0.35);
              }
            }}
            nodePointerAreaPaint={(node: WebNode, color: string, ctx: CanvasRenderingContext2D) => {
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
          </Suspense>
        ) : null}
      </div>
    </div>
  );
}

function fromWeb(web: WebGraph, walkPath: WalkPathNode[]): GraphData {
  const unique = new Map<string, WebNode>();
  for (const n of web.nodes) {
    if (unique.has(n.id)) continue;
    unique.set(n.id, {
      id: n.id,
      label: n.label,
      hop: n.hop,
      beliefX: n.beliefX,
      beliefY: n.beliefY,
      beliefZ: n.beliefZ,
      walk: walkIdsHas(walkPath, n.id),
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
      weight: 1,
      refuted: false,
      rating: 0,
      rd: 0,
      volatility: 0,
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
): GraphData {
  const nodes = new Map<string, WebNode>();
  nodes.set(centerId, {
    id: centerId,
    label: centerLabel,
    hop: 0,
    walk: walkIdsHas(walkPath, centerId),
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
      // The entity detail surface carries display μ only. Do not synthesize a
      // browser-side Glicko weight from it; the expanded native web supplies the
      // canonical signed fold and immediately becomes the standing-field layout.
      weight: 0,
      refuted: false,
      rating: 0,
      rd: 0,
      volatility: 0,
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
      weight: 1,
      refuted: false,
      rating: 0,
      rd: 0,
      volatility: 0,
      walk: true,
    });
  }
  return { nodes: [...nodes.values()], links };
}

function walkIdsHas(walkPath: WalkPathNode[], id: string) {
  return walkPath.some((n) => n.idHex === id);
}
