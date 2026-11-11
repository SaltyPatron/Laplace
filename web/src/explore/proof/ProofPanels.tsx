/** Storage-proof bit-carrier inspector and Tier-0 distribution experiment panels. */
import { useMemo } from 'react';

import type { StorageProofPackedVertexRow } from '../types';
import { GlomeCanvas } from '../glome/GlomeCanvas';
import { NO_TRAJECTORY, carrierLane, compactId, distributionNodes, type CarrierLane } from './proofGeometry';
import styles from './StorageProofView.module.css';

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

export function BitPackingPanel({ row }: { row: StorageProofPackedVertexRow | null }) {
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

export function DistributionLab({
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
        <div><span>Legacy latitude occupied</span><strong>{(canonicalFraction * 100).toFixed(2)}%</strong></div>
        <div><span>Canonical sampled radial span</span><strong>{(openSpan * 100).toFixed(2)}%</strong></div>
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
              trajectoryPoints={NO_TRAJECTORY}
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
              trajectoryPoints={NO_TRAJECTORY}
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
