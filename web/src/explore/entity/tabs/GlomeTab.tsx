import { useEffect, useMemo, useState } from 'react';
import { Panel, SegmentedControl, Stack } from '@ui';
import { GatePrompt } from '../../components/GatePrompt';
import {
  GlomeCanvas,
  ordinalColor,
  packedDisplayPos,
  placementBallPos,
  physicalitiesToNodes,
  type GlomeNode,
} from '../../glome/GlomeCanvas';
import type { ExploreEntityResponse } from '../../types';
import type { NeighborMode } from './types';
import styles from './GlomeTab.module.css';

export function GlomeTab({
  entity,
  neighborMode,
  neighborsUnlocked,
  glomeExtraNodes,
  walkHighlight,
  onNeighborModeChange,
  onLoadNeighbors,
}: {
  entity: ExploreEntityResponse;
  neighborMode: NeighborMode;
  neighborsUnlocked: boolean;
  glomeExtraNodes: GlomeNode[];
  walkHighlight: string[];
  onNeighborModeChange: (mode: NeighborMode) => void;
  onLoadNeighbors: () => void;
}) {
  // A composite cannot be its own child. Suppress corrupt/stale recursive entries at
  // the visualization boundary while retaining every legitimate constituent.
  const packed = useMemo(
    () => (entity.packed_vertices ?? []).filter((v) => v.child_id_hex !== entity.id_hex),
    [entity.packed_vertices, entity.id_hex],
  );
  const realized = useMemo(
    () => (entity.realized_vertices ?? []).filter((v) => v.child_id_hex !== entity.id_hex),
    [entity.realized_vertices, entity.id_hex],
  );
  const maxOrd = useMemo(() => {
    let m = 0;
    for (const v of packed) m = Math.max(m, v.ordinal);
    for (const v of realized) m = Math.max(m, v.ordinal);
    return Math.max(m, 1);
  }, [packed, realized]);

  const [selectedOrdinal, setSelectedOrdinal] = useState<number | null>(null);

  useEffect(() => {
    setSelectedOrdinal(null);
  }, [entity.id_hex]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      if (maxOrd < 1) return;
      e.preventDefault();
      setSelectedOrdinal((prev) => {
        const cur = prev ?? (e.key === 'ArrowRight' ? 0 : 1);
        if (e.key === 'ArrowRight') return Math.min(maxOrd, cur + 1);
        return Math.max(1, cur - 1);
      });
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [maxOrd]);

  const packedNodes = useMemo((): GlomeNode[] => {
    return packed.map((v) => ({
      id: `packed-${v.ordinal}-${v.child_id_hex}`,
      label: v.child_id_hex.slice(0, 12),
      x: v.x,
      y: v.y,
      z: v.z,
      m: v.m,
      radius: 1,
      ordinal: v.ordinal,
      runLength: v.run_length,
      kind: 'constituent' as const,
      color: ordinalColor(v.ordinal, maxOrd),
    }));
  }, [packed, maxOrd]);

  const packedTrajectory = useMemo(
    () => packedNodes
      .slice()
      .sort((a, b) => (a.ordinal ?? 0) - (b.ordinal ?? 0))
      .map((n) => packedDisplayPos(n)),
    [packedNodes],
  );

  const placementPrimary = useMemo(
    () => physicalitiesToNodes(entity.physicalities, entity.label, entity.id_hex),
    [entity.physicalities, entity.label, entity.id_hex],
  );

  const placementConstituents = useMemo((): GlomeNode[] => {
    return realized.map((v) => ({
      id: `real-${v.ordinal}-${v.child_id_hex}`,
      label: v.child_label || v.child_id_hex.slice(0, 12),
      x: v.x,
      y: v.y,
      z: v.z,
      m: v.m,
      radius: v.radius,
      ordinal: v.ordinal,
      kind: 'constituent' as const,
    }));
  }, [realized]);

  const placementNodes = useMemo(() => {
    const extras = neighborMode === 'structural' && neighborsUnlocked ? glomeExtraNodes : [];
    return [...placementPrimary, ...placementConstituents, ...extras];
  }, [placementPrimary, placementConstituents, glomeExtraNodes, neighborMode, neighborsUnlocked]);

  const placementTrajectory = useMemo(
    () => placementConstituents
      .slice()
      .sort((a, b) => (a.ordinal ?? 0) - (b.ordinal ?? 0))
      .map((n) => placementBallPos(n)),
    [placementConstituents],
  );

  const selectedChip = useMemo(() => {
    if (selectedOrdinal == null) return null;
    const r = realized.find((v) => v.ordinal === selectedOrdinal);
    const p = packed.find((v) => {
      const start = v.ordinal;
      const end = v.ordinal + Math.max(v.run_length, 1) - 1;
      return selectedOrdinal >= start && selectedOrdinal <= end;
    });
    return {
      label: r?.child_label ?? p?.child_id_hex.slice(0, 16) ?? `ord ${selectedOrdinal}`,
      ordinal: selectedOrdinal,
      runLength: p?.run_length ?? 1,
    };
  }, [selectedOrdinal, realized, packed]);

  // Packed highlight: RLE vertex covering the expanded ordinal.
  const packedHighlightOrdinal = useMemo(() => {
    if (selectedOrdinal == null) return null;
    const hit = packed.find((v) => {
      const end = v.ordinal + Math.max(v.run_length, 1) - 1;
      return selectedOrdinal >= v.ordinal && selectedOrdinal <= end;
    });
    return hit?.ordinal ?? selectedOrdinal;
  }, [selectedOrdinal, packed]);

  return (
    <Panel title="Physicality trajectory" fill>
      <Stack gap={3} className={styles.body}>
        <div className={styles.panes}>
          <section className={styles.pane}>
            <header className={styles.paneHead}>
              <h3 className={styles.paneTitle}>Packed</h3>
              <p className={styles.paneCaption}>Identity packed as XYZ · M / RLE paint</p>
            </header>
            {packedNodes.length === 0 ? (
              <div className={styles.emptyPane}>
                No trajectory — this entity is a leaf or has no witnessed path.
              </div>
            ) : (
              <GlomeCanvas
                nodes={packedNodes}
                trajectoryPoints={packedTrajectory}
                projection="packed"
                highlightOrdinal={packedHighlightOrdinal}
                onSelectOrdinal={setSelectedOrdinal}
                fill
                note="Hash-space trajectory on a display shell. Not S³ placement."
              />
            )}
          </section>

          <section className={styles.pane}>
            <header className={styles.paneHead}>
              <h3 className={styles.paneTitle}>Placement</h3>
              <p className={styles.paneCaption}>Live centroid + realized path (entity_curve)</p>
            </header>
            {placementNodes.length === 0 ? (
              <div className={styles.emptyPane}>No physicality coord for this entity.</div>
            ) : (
              <GlomeCanvas
                nodes={placementNodes}
                trajectoryPoints={placementTrajectory.length > 1 ? placementTrajectory : undefined}
                projection="placement"
                highlightIds={walkHighlight}
                highlightOrdinal={selectedOrdinal}
                onSelectOrdinal={setSelectedOrdinal}
                fill
                staggerMs={120}
                note="Glome ball — radius is coherence. Ribbon = realized child coords."
              />
            )}
          </section>
        </div>

        <div className={styles.scrub}>
          <label className={styles.scrubLabel} htmlFor="fold-ordinal">
            Ordinal
          </label>
          <input
            id="fold-ordinal"
            className={styles.scrubRange}
            type="range"
            min={1}
            max={maxOrd}
            value={selectedOrdinal ?? 1}
            disabled={maxOrd < 1}
            onChange={(e) => setSelectedOrdinal(Number(e.target.value))}
          />
          <div className={styles.chip}>
            {selectedChip
              ? <>{selectedChip.label} · ord {selectedChip.ordinal}
                  {selectedChip.runLength > 1 ? ` · rle ${selectedChip.runLength}` : ''}</>
              : <span className={styles.chipMuted}>← → or scrub to link both panes</span>}
          </div>
        </div>

        <div className={styles.overlayRow}>
          <SegmentedControl
            value={neighborMode}
            onValueChange={(v) => onNeighborModeChange(v as NeighborMode)}
            options={['structural', 'semantic']}
            label="Placement overlay"
          />
          {!neighborsUnlocked ? (
            <GatePrompt
              serviceId="nn"
              label="Load structural nearest neighbors on Placement only."
              receipt={null}
              onReady={onLoadNeighbors}
            />
          ) : neighborMode === 'semantic' ? (
            <p className={styles.hint}>
              Semantic mode lists consensus facts; it does not plant points on the glome.
            </p>
          ) : (
            <p className={styles.hint}>
              Structural NN overlaid on Placement ({glomeExtraNodes.length} neighbors).
            </p>
          )}
        </div>
      </Stack>
    </Panel>
  );
}
