import { useEffect, useMemo, useState, type CSSProperties, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted } from '@ui';

import { explorePreview, exploreStorageProof } from '../api';
import type {
  ExploreEntityPreviewResponse,
  StorageProofNodeRow,
  StorageProofPackedVertexRow,
  StorageProofResponse,
} from '../types';
import { GlomeCanvas, type GlomeNode } from '../glome/GlomeCanvas';
import styles from './StorageProofView.module.css';

const TWO_POW_53_MINUS_1 = (1n << 53n) - 1n;
const MANTISSA_MASK = (1n << 52n) - 1n;
const TWO_PI = 6.2831853071795864769252867665590057683943387987502;
const SUPER_FIB_PHI = 1.4142135623730951454746218587388284504413604736328125;
const SUPER_FIB_PSI = 1.5337511687552042888118041448362171649932861328125;

type WalkDirection = 'trunk' | 'leaf';

interface CarrierLane {
  slot: bigint;
  bits: string;
  rawHex: string;
}

function carrierLane(value: number): CarrierLane {
  const buffer = new ArrayBuffer(8);
  const view = new DataView(buffer);
  view.setFloat64(0, value, false);
  const raw = view.getBigUint64(0, false);
  const slot = (((raw >> 63n) & 1n) << 52n) | (raw & MANTISSA_MASK);
  return {
    slot,
    bits: slot.toString(2).padStart(53, '0'),
    rawHex: raw.toString(16).padStart(16, '0'),
  };
}

function carrierCoordinate(value: number): number {
  const slot = carrierLane(value).slot;
  return Number(slot) / Number(TWO_POW_53_MINUS_1) * 2 - 1;
}

function packedToCarrierNode(row: StorageProofPackedVertexRow): GlomeNode {
  return {
    id: `${row.child_id_hex}-carrier-${row.vertex}`,
    label: row.child_label || row.child_id_hex,
    x: carrierCoordinate(row.x),
    y: carrierCoordinate(row.y),
    z: carrierCoordinate(row.z),
    m: carrierCoordinate(row.m),
    radius: 1,
    ordinal: row.logical_ordinal,
    runLength: row.run_length,
    kind: 'constituent',
  };
}

function realizedToGlomeNode(
  row: StorageProofNodeRow['realized_vertices'][number],
): GlomeNode {
  return {
    id: `${row.child_id_hex}-realized-${row.ordinal}`,
    label: row.child_label || row.child_id_hex,
    x: row.x,
    y: row.y,
    z: row.z,
    m: row.m,
    radius: row.radius,
    ordinal: row.ordinal,
    kind: 'constituent',
  };
}

function nodeToGlomeNode(node: StorageProofNodeRow, selected: boolean): GlomeNode {
  return {
    id: node.id_hex,
    label: node.label || node.id_hex,
    x: node.x,
    y: node.y,
    z: node.z,
    m: node.m,
    radius: node.radius,
    kind: selected ? 'primary' : node.tier === 0 ? 'constituent' : 'walk',
  };
}

function depthOf(node: StorageProofNodeRow, byOrdinal: Map<number, StorageProofNodeRow>): number {
  let depth = 0;
  let parent = node.parent_ordinal;
  const seen = new Set<number>();
  while (parent != null && !seen.has(parent)) {
    seen.add(parent);
    const p = byOrdinal.get(parent);
    if (!p) break;
    depth++;
    parent = p.parent_ordinal;
  }
  return depth;
}

function compactId(id: string): string {
  return id.length <= 18 ? id : `${id.slice(0, 8)}…${id.slice(-8)}`;
}

function prettyNumber(value: number, digits = 6): string {
  return Number.isFinite(value) ? value.toFixed(digits) : String(value);
}

function radicalInverseBase2(value: number): number {
  let n = Math.max(0, Math.floor(value));
  let fraction = 0.5;
  let out = 0;
  while (n > 0) {
    if (n % 2 === 1) out += fraction;
    n = Math.floor(n / 2);
    fraction *= 0.5;
  }
  return out;
}

function superFibonacciPoint(rank: number, n: number): [number, number, number, number] {
  const s = rank + 0.5;
  const t = s / n;
  const r = Math.sqrt(t);
  const cap = Math.sqrt(1 - t);
  const alpha = s * (TWO_PI / SUPER_FIB_PHI);
  const beta = s * (TWO_PI / SUPER_FIB_PSI);
  return [r * Math.sin(alpha), r * Math.cos(alpha), cap * Math.sin(beta), cap * Math.cos(beta)];
}

function superFibonacciOpenPoint(rank: number): [number, number, number, number] {
  const s = rank + 0.5;
  const t = radicalInverseBase2(rank);
  const r = Math.sqrt(t);
  const cap = Math.sqrt(1 - t);
  const alpha = s * (TWO_PI / SUPER_FIB_PHI);
  const beta = s * (TWO_PI / SUPER_FIB_PSI);
  return [r * Math.sin(alpha), r * Math.cos(alpha), cap * Math.sin(beta), cap * Math.cos(beta)];
}

function distributionNodes(
  occupied: number,
  atomWindow: number,
  interleaved: boolean,
): GlomeNode[] {
  const sampleCount = Math.min(1600, Math.max(1, occupied));
  const step = occupied / sampleCount;
  const nodes: GlomeNode[] = [];
  let previous = -1;
  for (let i = 0; i < sampleCount; i++) {
    const rank = Math.min(occupied - 1, Math.floor((i + 0.5) * step));
    if (rank === previous) continue;
    previous = rank;
    const [x, y, z, m] = interleaved
      ? superFibonacciOpenPoint(rank)
      : superFibonacciPoint(rank, atomWindow);
    nodes.push({
      id: `${interleaved ? 'open' : 'canonical'}-${rank}`,
      label: `DUCET rank ${rank.toLocaleString()}`,
      x, y, z, m,
      radius: 1,
      kind: 'constituent',
    });
  }
  return nodes;
}

function BitLane({
  axis,
  lane,
  segments,
}: {
  axis: string;
  lane: CarrierLane;
  segments: { label: string; bits: string }[];
}) {
  return (
    <div className={styles.bitLane}>
      <div className={styles.bitLaneHead}>
        <strong>{axis}</strong>
        <code>float64 0x{lane.rawHex}</code>
        <span>fixed exponent · 53 payload bits</span>
      </div>
      <div className={styles.bitSegments}>
        {segments.map((segment) => (
          <div className={styles.bitSegment} key={segment.label}>
            <span>{segment.label}</span>
            <code>{segment.bits}</code>
          </div>
        ))}
      </div>
    </div>
  );
}

function BitPackingPanel({ row }: { row: StorageProofPackedVertexRow | null }) {
  if (!row) {
    return (
      <div className={styles.emptyPanel}>
        Select a compositional node with packed vertices to inspect its 212-bit carrier.
      </div>
    );
  }

  const x = carrierLane(row.x);
  const y = carrierLane(row.y);
  const z = carrierLane(row.z);
  const m = carrierLane(row.m);

  return (
    <div className={styles.bitPacking}>
      <div className={styles.carrierSummary}>
        <div>
          <span>Decoded child</span>
          <code title={row.child_id_hex}>{compactId(row.child_id_hex)}</code>
        </div>
        <div>
          <span>Logical ordinal</span>
          <strong>{row.logical_ordinal.toLocaleString()}</strong>
        </div>
        <div>
          <span>Run</span>
          <strong>{row.run_length.toLocaleString()}</strong>
        </div>
        <div>
          <span>Flags</span>
          <code>0x{Math.trunc(row.flags).toString(16)}</code>
        </div>
      </div>

      <BitLane axis="X" lane={x} segments={[
        { label: 'entity_id.lo [52:0]', bits: x.bits },
      ]} />
      <BitLane axis="Y" lane={y} segments={[
        { label: 'entity_id.hi [41:0]', bits: y.bits.slice(0, 42) },
        { label: 'entity_id.lo [63:53]', bits: y.bits.slice(42) },
      ]} />
      <BitLane axis="Z" lane={z} segments={[
        { label: 'flags [30:0]', bits: z.bits.slice(0, 31) },
        { label: 'entity_id.hi [63:42]', bits: z.bits.slice(31) },
      ]} />
      <BitLane axis="M" lane={m} segments={[
        { label: 'flags [51:31]', bits: m.bits.slice(0, 21) },
        { label: 'run_length [15:0]', bits: m.bits.slice(21, 37) },
        { label: 'ordinal [15:0]', bits: m.bits.slice(37) },
      ]} />
    </div>
  );
}

function DistributionLab({
  atomWindow,
  occupied,
  setOccupied,
}: {
  atomWindow: number;
  occupied: number;
  setOccupied: (value: number) => void;
}) {
  const bounded = Math.min(atomWindow, Math.max(1, occupied));
  const canonical = useMemo(
    () => distributionNodes(bounded, atomWindow, false),
    [bounded, atomWindow],
  );
  const interleaved = useMemo(
    () => distributionNodes(bounded, atomWindow, true),
    [bounded, atomWindow],
  );

  const canonicalFraction = bounded / atomWindow;
  const openT = interleaved.map((node) => (node.x * node.x + node.y * node.y));
  const openSpan = openT.length
    ? Math.max(...openT) - Math.min(...openT)
    : 0;

  return (
    <section className={styles.section}>
      <div className={styles.sectionHead}>
        <div>
          <h3>Tier-0 distribution experiment</h3>
          <p>
            Compare the retired bounded/latitude mapping with the canonical open/radical-inverse
            mapping. Both preserve DUCET identity order; only the canonical mapping spreads every
            early prefix across the whole shell.
          </p>
        </div>
        <div className={styles.rankControl}>
          <label htmlFor="occupied-ranks">Occupied DUCET prefix</label>
          <input
            id="occupied-ranks"
            type="range"
            min={1000}
            max={atomWindow}
            step={1000}
            value={bounded}
            onChange={(event) => setOccupied(Number(event.target.value))}
          />
          <output>{bounded.toLocaleString()} / {atomWindow.toLocaleString()}</output>
        </div>
      </div>

      <div className={styles.quickRanks}>
        {[150_000, 250_000, 300_000, atomWindow].map((value) => (
          <button
            type="button"
            key={value}
            className={bounded === value ? styles.smallButtonActive : styles.smallButton}
            onClick={() => setOccupied(value)}
          >
            {value === atomWindow ? 'full window' : value.toLocaleString()}
          </button>
        ))}
      </div>

      <div className={styles.metrics}>
        <div><span>Canonical x²+y² prefix</span><strong>{(canonicalFraction * 100).toFixed(2)}%</strong></div>
        <div><span>Interleaved sampled x²+y² span</span><strong>{(openSpan * 100).toFixed(2)}%</strong></div>
        <div><span>Rendered sample</span><strong>{canonical.length.toLocaleString()} points</strong></div>
      </div>

      <div className={styles.glomePair}>
        <article className={styles.visualCard}>
          <header>
            <strong>Legacy · rank/N latitude</strong>
            <span>Retired band-filling law</span>
          </header>
          <div className={styles.canvasTall}>
            <GlomeCanvas
              nodes={canonical}
              projection="placement"
              fill
              note="Retired bounded placement: early DUCET ranks occupy a narrow Hopf-latitude band."
            />
          </div>
        </article>
        <article className={styles.visualCard}>
          <header>
            <strong>Canonical · rank → radical inverse</strong>
            <span>Current Tier-0 law after reseed</span>
          </header>
          <div className={styles.canvasTall}>
            <GlomeCanvas
              nodes={interleaved}
              projection="placement"
              fill
              note="Canonical placement: same DUCET ranks, bit-reversed radial parameter, full-shell prefix coverage."
            />
          </div>
        </article>
      </div>

      <p className={styles.caveat}>
        This slider models a contiguous occupied DUCET prefix; it is not a measured census of
        every codepoint humanity actually uses. The reseed intentionally changes Tier-0
        coordinates, Hilbert keys, and all composed centroids so the new substrate starts with
        full-shell coverage rather than inheriting the old latitude band.
      </p>
    </section>
  );
}

export function StorageProofView() {
  const [searchParams, setSearchParams] = useSearchParams();
  const query = searchParams.get('q') ?? '';
  const [draft, setDraft] = useState(query);
  const [proof, setProof] = useState<StorageProofResponse | null>(null);
  const [selectedOrdinal, setSelectedOrdinal] = useState<number | null>(null);
  const [selectedPacked, setSelectedPacked] = useState(0);
  const [direction, setDirection] = useState<WalkDirection>('trunk');
  const [preview, setPreview] = useState<ExploreEntityPreviewResponse | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [occupiedRanks, setOccupiedRanks] = useState(250_000);

  useEffect(() => {
    setDraft(query);
    if (!query.trim()) {
      setProof(null);
      setSelectedOrdinal(null);
      setError(null);
      return;
    }

    let cancelled = false;
    setBusy(true);
    setError(null);
    void exploreStorageProof(query)
      .then((result) => {
        if (cancelled) return;
        setProof(result);
        setSelectedOrdinal(result.natural_unit_ordinal);
        setSelectedPacked(0);
        setOccupiedRanks((value) => Math.min(result.atom_window, value));
      })
      .catch((reason: unknown) => {
        if (cancelled) return;
        setProof(null);
        setError(reason instanceof Error ? reason.message : String(reason));
      })
      .finally(() => {
        if (!cancelled) setBusy(false);
      });

    return () => {
      cancelled = true;
    };
  }, [query]);

  const byOrdinal = useMemo(
    () => new Map((proof?.nodes ?? []).map((node) => [node.ordinal, node])),
    [proof],
  );
  const selected = selectedOrdinal == null ? null : byOrdinal.get(selectedOrdinal) ?? null;

  useEffect(() => {
    if (!selected) {
      setPreview(null);
      return;
    }
    let cancelled = false;
    setPreviewBusy(true);
    setPreview(null);
    void explorePreview(selected.id_hex)
      .then((result) => {
        if (!cancelled) setPreview(result);
      })
      .catch(() => {
        if (!cancelled) setPreview(null);
      })
      .finally(() => {
        if (!cancelled) setPreviewBusy(false);
      });
    return () => {
      cancelled = true;
    };
  }, [selected?.id_hex]);

  const orderedNodes = useMemo(() => {
    if (!proof) return [];
    return proof.nodes.slice().sort((a, b) => {
      const da = depthOf(a, byOrdinal);
      const db = depthOf(b, byOrdinal);
      const depthOrder = direction === 'trunk' ? da - db : db - da;
      if (depthOrder !== 0) return depthOrder;
      if (a.text_offset !== b.text_offset) return a.text_offset - b.text_offset;
      return a.ordinal - b.ordinal;
    });
  }, [proof, byOrdinal, direction]);

  const placementNodes = useMemo(
    () => (proof?.nodes ?? []).map((node) => nodeToGlomeNode(node, node.ordinal === selectedOrdinal)),
    [proof, selectedOrdinal],
  );
  const carrierNodes = useMemo(
    () => (selected?.packed_vertices ?? []).map(packedToCarrierNode),
    [selected],
  );
  const realizedNodes = useMemo(
    () => (selected?.realized_vertices ?? []).map(realizedToGlomeNode),
    [selected],
  );

  const selectedPackedRow = selected?.packed_vertices[selectedPacked] ?? null;
  const maxTier = proof ? Math.max(...proof.nodes.map((node) => node.tier), 0) : 0;
  const tier0Count = proof?.nodes.filter((node) => node.tier === 0).length ?? 0;

  function submit(event: FormEvent) {
    event.preventDefault();
    const value = draft.trim();
    if (!value) return;
    setSearchParams({ q: value });
  }

  return (
    <div className={styles.root}>
      <header className={styles.hero}>
        <div>
          <span className={styles.eyebrow}>Executable storage proof</span>
          <h2>Finite coordinate address · exact reversible trajectory</h2>
          <p>
            One prompt, one native decomposition: identity, O(Tier) trunk/leaf structure,
            4-D placement, 128-bit Hilbert locality, and the exact 212-bit packed carrier
            emitted for each composition.
          </p>
        </div>
        <form className={styles.promptForm} onSubmit={submit}>
          <label htmlFor="storage-proof-prompt">Prompt / surface</label>
          <textarea
            id="storage-proof-prompt"
            rows={3}
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            placeholder="Paste any text, or link here with ?q=..."
          />
          <div className={styles.formActions}>
            <span>Shareable: /proof?q=…</span>
            <button type="submit" disabled={!draft.trim() || busy}>
              {busy ? 'Computing…' : 'Prove storage'}
            </button>
          </div>
        </form>
      </header>

      {error ? <ErrorText>{error}</ErrorText> : null}
      {busy && !proof ? <LoadingText>Running native decomposition and storage packing…</LoadingText> : null}

      {!query.trim() && !busy ? (
        <section className={styles.emptyIntro}>
          <h3>What this page proves</h3>
          <p>
            The endpoint runs the same TextDecomposer, HashComposer and flagged-RLE trajectory
            builder used by ingestion. It does not invent browser-only coordinates. Enter text
            above, or navigate here with a URL-encoded <code>q</code> parameter.
          </p>
        </section>
      ) : null}

      {proof ? (
        <>
          <section className={styles.summaryStrip}>
            <div><span>Root</span><code title={proof.root_id_hex}>{compactId(proof.root_id_hex)}</code></div>
            <div><span>Emitted nodes</span><strong>{proof.nodes.length.toLocaleString()}</strong></div>
            <div><span>Max tier</span><strong>{maxTier}</strong></div>
            <div><span>Tier-0 leaves</span><strong>{tier0Count.toLocaleString()}</strong></div>
            <div><span>Atom window</span><strong>{proof.atom_window.toLocaleString()}</strong></div>
            <div>
              <span>T0 ROM receipt</span>
              <code title={proof.perfcache_receipt_hex}>{compactId(proof.perfcache_receipt_hex)}</code>
            </div>
            <div>
              <span>DB uses same ROM</span>
              <strong>
                {proof.perfcache_aligned == null ? 'unknown' : proof.perfcache_aligned ? 'yes' : 'NO'}
              </strong>
            </div>
          </section>

          <section className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <h3>Storage law</h3>
                <p>
                  A fixed finite geometric domain carries an unbounded family of finite recursive
                  compositions. Identity, placement, locality, and reversible sequence storage are
                  separate mechanisms and are shown separately below.
                </p>
              </div>
            </div>
            <div className={styles.lawGrid}>
              <article className={styles.lawCard}>
                <span className={styles.lawStep}>01 · finite floor ROM</span>
                <strong>Tier 0 is a fixed address basis</strong>
                <p>
                  The v4 perfcache stores every Unicode codepoint&apos;s content ID, DUCET/UCA
                  order, 4-D S³ coordinate, 128-bit Hilbert key, and segmentation properties.
                  DUCET order feeds the open Super-Fibonacci map; early ranks are interleaved
                  across the full shell instead of filling one latitude band.
                </p>
                <code>codepoint → {'{'} id, uca_order, x, y, z, m, hilbert, flags {'}'}</code>
              </article>

              <article className={styles.lawCard}>
                <span className={styles.lawStep}>02 · recursive identity</span>
                <strong>Ordered children determine content identity</strong>
                <p>
                  A single-child wrapper collapses to the child identity. A multi-child node is
                  the Merkle-domain BLAKE3 content address of the ordered child-ID sequence.
                  Tier, source, ordinal, worker, and container are not identity salt.
                </p>
                <code>n = 1 → id(child)</code>
                <code>n &gt; 1 → BLAKE3(domain ∥ child₁.id ∥ … ∥ childₙ.id)[0..127]</code>
              </article>

              <article className={styles.lawCard}>
                <span className={styles.lawStep}>03 · bounded placement</span>
                <strong>Every composition stays inside one finite 4-D ball</strong>
                <p>
                  HashComposer places a parent at the Euclidean centroid of its immediate child
                  coordinates. The closed unit 4-ball is convex, so finite recursive composition
                  cannot escape it. Hilbert encodes that resulting 4-D point for locality.
                </p>
                <code>coord(parent) = (Σ childᵢ.coord) / n</code>
                <code>hilbert(parent) = Hilbert4D(coord(parent))</code>
              </article>

              <article className={styles.lawCard}>
                <span className={styles.lawStep}>04 · exact sequence manifest</span>
                <strong>Order and identity are not thrown away by the centroid</strong>
                <p>
                  The physicality trajectory stores each child identity plus logical position,
                  run length, tier/atom metadata, and flags in four 53-bit float payload slots.
                  This carrier is exactly reversible identity cargo; it is not a path of positions.
                </p>
                <code>vertex = pack(child.id, ordinal, run_length, flags) → 4 × 53 bits</code>
              </article>

              <article className={styles.lawCard}>
                <span className={styles.lawStep}>05 · realized geometry</span>
                <strong>Spatial shape is reconstructed from live child coordinates</strong>
                <p>
                  When geometry is required, Laplace resolves the stored child IDs through their
                  physicalities and orders those real coordinates by ordinal. Fréchet/Hausdorff
                  operate on this realized curve, never on the mantissa carrier.
                </p>
                <code>manifest child IDs → live PointZM → ordered realized curve</code>
              </article>

              <article className={styles.lawCard}>
                <span className={styles.lawStep}>06 · recursive addressability</span>
                <strong>Complexity grows by composition, not by enlarging the coordinate domain</strong>
                <p>
                  Every finite node has a finite content address, one bounded 4-D placement, and a
                  finite exact child manifest. Those nodes become children of higher nodes without
                  allocating a larger geometric space. The limiting resource is computation and
                  materialization, not exhaustion of the 4-D coordinate domain.
                </p>
                <code>finite basis → finite nodes → finite parents → …</code>
              </article>
            </div>
          </section>

          <section className={styles.proofGrid}>
            <article className={styles.treePanel}>
              <div className={styles.panelHead}>
                <div>
                  <h3>O(Tier) composition walk</h3>
                  <p>Collapsed storage spine only; parser-only unary wrappers are omitted.</p>
                </div>
                <div className={styles.segmented}>
                  <button
                    type="button"
                    className={direction === 'trunk' ? styles.segmentActive : styles.segment}
                    onClick={() => setDirection('trunk')}
                  >
                    trunk → leaf
                  </button>
                  <button
                    type="button"
                    className={direction === 'leaf' ? styles.segmentActive : styles.segment}
                    onClick={() => setDirection('leaf')}
                  >
                    leaf → trunk
                  </button>
                </div>
              </div>
              <div className={styles.nodeList}>
                {orderedNodes.map((node) => {
                  const depth = depthOf(node, byOrdinal);
                  const isSelected = node.ordinal === selectedOrdinal;
                  return (
                    <button
                      type="button"
                      key={node.ordinal}
                      className={isSelected ? styles.nodeRowSelected : styles.nodeRow}
                      onClick={() => {
                        setSelectedOrdinal(node.ordinal);
                        setSelectedPacked(0);
                      }}
                    >
                      <span className={styles.depthMark} style={{ '--depth': depth } as CSSProperties} />
                      <span className={styles.tierBadge}>T{node.tier}</span>
                      <span className={styles.nodeLabel} title={node.label}>{node.label || '∅'}</span>
                      <code>{compactId(node.id_hex)}</code>
                      <span className={styles.radius}>r {node.radius.toFixed(3)}</span>
                    </button>
                  );
                })}
              </div>
            </article>

            <article className={styles.detailPanel}>
              <div className={styles.panelHead}>
                <div>
                  <h3>Selected storage address</h3>
                  <p>Identity, 4-D placement and 1-D Hilbert locality are distinct fields.</p>
                </div>
                {selected ? <span className={styles.tierBadge}>T{selected.tier}</span> : null}
              </div>

              {selected ? (
                <>
                  <div className={styles.addressGrid}>
                    <div><span>Entity ID</span><code title={selected.id_hex}>{selected.id_hex}</code></div>
                    <div><span>Hilbert 128</span><code title={selected.hilbert_hex}>{selected.hilbert_hex}</code></div>
                    <div><span>X</span><code>{prettyNumber(selected.x, 10)}</code></div>
                    <div><span>Y</span><code>{prettyNumber(selected.y, 10)}</code></div>
                    <div><span>Z</span><code>{prettyNumber(selected.z, 10)}</code></div>
                    <div><span>M</span><code>{prettyNumber(selected.m, 10)}</code></div>
                    <div><span>4-D radius</span><code>{prettyNumber(selected.radius, 10)}</code></div>
                    <div><span>Span</span><code>{selected.text_offset}+{selected.text_length} bytes</code></div>
                    {selected.atom != null ? (
                      <div><span>Codepoint atom</span><code>U+{selected.atom.toString(16).toUpperCase().padStart(4, '0')}</code></div>
                    ) : null}
                    {selected.ducet_rank != null ? (
                      <div><span>Recovered DUCET rank</span><code>{selected.ducet_rank.toLocaleString()}</code></div>
                    ) : null}
                  </div>

                  <div className={styles.dbWitness}>
                    <div>
                      <strong>PostgreSQL witness</strong>
                      {previewBusy ? <span>checking…</span> : preview ? (
                        <span>{preview.exists ? 'stored' : 'computed only'} · evidence {preview.evidence_count.toLocaleString()}</span>
                      ) : <span>unavailable</span>}
                    </div>
                    {preview?.exists ? (
                      <Link to={`/explore/entity/${selected.id_hex}`}>open persisted entity →</Link>
                    ) : null}
                  </div>
                  <div className={styles.romWitness}>
                    <div>
                      <span>App mmap</span>
                      <code title={proof.perfcache_receipt_hex}>{proof.perfcache_receipt_hex}</code>
                    </div>
                    <div>
                      <span>PostgreSQL mmap</span>
                      <code title={proof.database_perfcache_receipt_hex ?? ''}>
                        {proof.database_perfcache_receipt_hex ?? 'unavailable'}
                      </code>
                    </div>
                    <strong className={
                      proof.perfcache_aligned === false ? styles.romMismatch : styles.romMatch
                    }>
                      {proof.perfcache_aligned == null
                        ? 'alignment unknown'
                        : proof.perfcache_aligned
                          ? 'same exact T0 ROM'
                          : 'T0 ROM MISMATCH'}
                    </strong>
                  </div>
                </>
              ) : (
                <Muted>Select a node from the composition walk.</Muted>
              )}
            </article>
          </section>

          <section className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <h3>Real 4-D placement</h3>
                <p>
                  Every emitted node from this prompt, projected with X–M and Z–M rotations.
                  Radius is the actual distance from the 4-D origin, so composed centroids stay interior.
                </p>
              </div>
            </div>
            <div className={styles.canvasWide}>
              <GlomeCanvas
                nodes={placementNodes}
                projection="placement"
                highlightIds={selected ? [selected.id_hex] : []}
                fill
                note="Actual HashComposer coordinates. Tier-0 atoms are on S³; composed entities are bounded centroids inside the 4-D ball."
              />
            </div>
          </section>

          <section className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <h3>One composition, two non-interchangeable views</h3>
                <p>
                  Left: the exact packed identity carrier transformed into a 4-D bit-space display.
                  Right: the same ordered constituents at their real live coordinates.
                </p>
              </div>
            </div>

            {selected && selected.packed_vertices.length > 0 ? (
              <>
                <div className={styles.vertexPicker} aria-label="Packed vertex">
                  {selected.packed_vertices.map((vertex, index) => (
                    <button
                      type="button"
                      key={vertex.vertex}
                      className={selectedPacked === index ? styles.vertexActive : styles.vertex}
                      onClick={() => setSelectedPacked(index)}
                    >
                      v{vertex.vertex} · ord {vertex.logical_ordinal}
                      {vertex.run_length > 1 ? ` · ×${vertex.run_length}` : ''}
                    </button>
                  ))}
                </div>
                <div className={styles.glomePair}>
                  <article className={styles.visualCard}>
                    <header>
                      <strong>212-bit carrier projection</strong>
                      <span>53 bits × X/Y/Z/M</span>
                    </header>
                    <div className={styles.canvasTall}>
                      <GlomeCanvas
                        nodes={carrierNodes}
                        projection="placement"
                        highlightOrdinal={selectedPackedRow?.logical_ordinal ?? null}
                        fill
                        note="Visualization of the four 53-bit carrier slots after decoding sign+mantissa. This is not geometric placement."
                      />
                    </div>
                  </article>
                  <article className={styles.visualCard}>
                    <header>
                      <strong>Realized constituent curve</strong>
                      <span>Actual child PointZM coordinates</span>
                    </header>
                    <div className={styles.canvasTall}>
                      <GlomeCanvas
                        nodes={realizedNodes}
                        projection="placement"
                        highlightOrdinal={selectedPackedRow?.logical_ordinal ?? null}
                        fill
                        note="The spatial operand: child coordinates in logical order. This is the curve geometry metrics may use."
                      />
                    </div>
                  </article>
                </div>
                <BitPackingPanel row={selectedPackedRow} />
              </>
            ) : (
              <div className={styles.emptyPanel}>
                This node is a leaf or has no emitted constituent trajectory. Select a compositional node.
              </div>
            )}
          </section>

          <DistributionLab
            atomWindow={proof.atom_window}
            occupied={occupiedRanks}
            setOccupied={setOccupiedRanks}
          />
        </>
      ) : null}
    </div>
  );
}
