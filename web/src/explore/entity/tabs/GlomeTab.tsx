import { useEffect, useMemo, useState } from 'react';
import { Panel, Range, SegmentedControl, Stack } from '@ui';
import { GatePrompt } from '../../components/GatePrompt';
import {
  GlomeCanvas,
  ordinalColor,
  physicalitiesToNodes,
  type GlomeNode,
} from '../../glome/GlomeCanvas';
import type { ExploreEntityResponse } from '../../types';
import type { NeighborMode } from './types';
import styles from './GlomeTab.module.css';

const identityLabel = (idHex: string) => `Entity · ${idHex.slice(0, 12)}`;

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
    for (const v of packed) m = Math.max(m, v.ordinal + Math.max(v.run_length, 1) - 1);
    for (const v of realized) m = Math.max(m, v.ordinal);
    return m;
  }, [packed, realized]);

  const [selectedOrdinal, setSelectedOrdinal] = useState<number | null>(null);

  const constituentLabels = useMemo(() => {
    const labels = new Map<string, string>();
    for (const c of entity.constituents ?? []) {
      if (c.child_label) labels.set(c.child_id_hex, c.child_label);
    }
    return labels;
  }, [entity.constituents]);

  useEffect(() => {
    setSelectedOrdinal(null);
  }, [entity.id_hex]);

  const placementPrimary = useMemo(
    () => physicalitiesToNodes(entity.physicalities, entity.label, entity.id_hex),
    [entity.physicalities, entity.label, entity.id_hex],
  );

  const placementConstituents = useMemo((): GlomeNode[] => {
    const parentId = placementPrimary[0]?.id;
    return realized.map((v) => ({
      id: `real-${v.ordinal}-${v.child_id_hex}`,
      label: v.child_label || identityLabel(v.child_id_hex),
      x: v.x,
      y: v.y,
      z: v.z,
      m: v.m,
      radius: v.radius,
      ordinal: v.ordinal,
      parentId,
      kind: 'constituent' as const,
      color: ordinalColor(v.ordinal, maxOrd),
    }));
  }, [realized, placementPrimary, maxOrd]);

  const placementNodes = useMemo(() => {
    const extras = neighborMode === 'structural' && neighborsUnlocked ? glomeExtraNodes : [];
    return [...placementPrimary, ...placementConstituents, ...extras];
  }, [placementPrimary, placementConstituents, glomeExtraNodes, neighborMode, neighborsUnlocked]);

  const selectedChip = useMemo(() => {
    if (selectedOrdinal == null) return null;
    const r = realized.find((v) => v.ordinal === selectedOrdinal);
    const p = packed.find((v) => {
      const start = v.ordinal;
      const end = v.ordinal + Math.max(v.run_length, 1) - 1;
      return selectedOrdinal >= start && selectedOrdinal <= end;
    });
    const selectedId = r?.child_id_hex ?? p?.child_id_hex ?? entity.id_hex;
    return {
      label: r?.child_label
        ?? (p ? constituentLabels.get(p.child_id_hex) : null)
        ?? identityLabel(selectedId),
      ordinal: selectedOrdinal,
      runLength: p?.run_length ?? 1,
    };
  }, [selectedOrdinal, realized, packed, constituentLabels, entity.id_hex]);


  return (
    <Panel title="Physicality trajectory" fill>
      <Stack gap={3} className={styles.body} tabIndex={0} role="group" aria-label="Physicality trajectory navigation"
        onKeyDown={(event) => {
          if (event.target !== event.currentTarget || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
          if (maxOrd < 1 || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) return;
          event.preventDefault();
          const direction = event.key === 'ArrowRight' ? 1 : -1;
          setSelectedOrdinal((previous) => Math.max(1, Math.min(maxOrd, (previous ?? (direction > 0 ? 0 : 1)) + direction)));
        }}>
        <div className={styles.panes}>
          {/* Packed path vertices carry ID bits, not geometry; drawing them as coordinates is meaningless. Only real placement is shown. */}
          <section className={styles.pane}>
            <header className={styles.paneHead}>
              <h3 className={styles.paneTitle}>Placement</h3>
              <p className={styles.paneCaption}>4-D B⁴ centroid + sequential realized trajectory path</p>
            </header>
            {placementNodes.length === 0 ? (
              <div className={styles.emptyPane}>No physicality coord for this entity.</div>
            ) : (
              <GlomeCanvas
                nodes={placementNodes}
                projection="placement"
                highlightIds={walkHighlight}
                highlightOrdinal={selectedOrdinal}
                onSelectOrdinal={setSelectedOrdinal}
                fill
                staggerMs={120}
                note="4-D B⁴ projection — sequential trajectory path connects ordered constituents; spoke links connect to the parent centroid."
              />
            )}
          </section>
        </div>

        <div className={styles.scrub}>
          <label className={styles.scrubLabel} htmlFor="fold-ordinal">
            Ordinal
          </label>
          <Range
            id="fold-ordinal"
            className={styles.scrubRange}
            min={1}
            max={Math.max(1, maxOrd)}
            value={selectedOrdinal ?? 1}
            disabled={maxOrd < 1}
            onChange={(e) => setSelectedOrdinal(Number(e.target.value))}
          />
          <div className={styles.chip}>
            {selectedChip
              ? <>{selectedChip.label} · ord {selectedChip.ordinal}
                  {selectedChip.runLength > 1 ? ` · rle ${selectedChip.runLength}` : ''}</>
              : <span className={styles.chipMuted}>Focus the trajectory and use ← →, or scrub to link both panes</span>}
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
