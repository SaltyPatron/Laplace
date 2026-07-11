import { Panel, SegmentedControl, Stack } from '@ui';
import { GatePrompt } from '../../components/GatePrompt';
import { GlomeCanvasFromPhysicalities, type GlomeNode } from '../../glome/GlomeCanvas';
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
  return (
    <Panel title="S³ geometry" fill>
      <Stack gap={3} className={styles.body}>
        <SegmentedControl
          value={neighborMode}
          onValueChange={(v) => onNeighborModeChange(v as NeighborMode)}
          options={['structural', 'semantic']}
          label="Overlay mode"
        />
        {!neighborsUnlocked ? (
          <GatePrompt
            serviceId="nn"
            label="Load nearest-neighbor overlay (structural axis on S³)."
            receipt={null}
            onReady={onLoadNeighbors}
          />
        ) : (
          <div className={styles.viewer}>
            <GlomeCanvasFromPhysicalities
              physicalities={entity.physicalities}
              label={entity.label}
              idHex={entity.id_hex}
              extraNodes={glomeExtraNodes}
              highlightIds={walkHighlight}
              fill
            />
          </div>
        )}
      </Stack>
    </Panel>
  );
}
