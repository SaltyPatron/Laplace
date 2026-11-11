/** View controls above the glome: colour metric, overlay toggles and the 4-D rotation sliders. */
import { Checkbox, Range, Select } from '@ui';
import type { GlomeColorBy } from './glomeProjection';
import styles from './GlomeCanvas.module.css';

export function GlomeViewControls({
  colorBy,
  onColorBy,
  hasTier,
  hasTrajectory,
  hasParents,
  showTrajectory,
  onShowTrajectory,
  showEdges,
  onShowEdges,
  showLabels,
  onShowLabels,
}: {
  colorBy: GlomeColorBy;
  onColorBy: (value: GlomeColorBy) => void;
  hasTier: boolean;
  hasTrajectory: boolean;
  hasParents: boolean;
  showTrajectory: boolean;
  onShowTrajectory: (value: boolean) => void;
  showEdges: boolean;
  onShowEdges: (value: boolean) => void;
  showLabels: boolean;
  onShowLabels: (value: boolean) => void;
}) {
  return (
    <div className={styles.viewControls}>
      <label>
        <span>Color</span>
        <Select value={colorBy} onChange={(event) => onColorBy(event.target.value as GlomeColorBy)}>
          <option value="kind">role</option>
          {hasTier ? <option value="tier">tier</option> : null}
          <option value="depth">r₄ depth</option>
          <option value="ordinal">ordinal</option>
        </Select>
      </label>
      {hasTrajectory ? (
        <label><Checkbox checked={showTrajectory} onChange={(e) => onShowTrajectory(e.target.checked)} /> trajectory path</label>
      ) : null}
      {hasParents ? (
        <label><Checkbox checked={showEdges} onChange={(e) => onShowEdges(e.target.checked)} /> parent centroid links</label>
      ) : null}
      <label><Checkbox checked={showLabels} onChange={(e) => onShowLabels(e.target.checked)} /> labels when zoomed</label>
      <span className={styles.viewHint}>Scroll zooms toward the cursor; points keep their screen size.</span>
    </div>
  );
}

export function GlomeRotationControls({
  xmDegrees,
  onXmDegrees,
  zmDegrees,
  onZmDegrees,
}: {
  xmDegrees: number;
  onXmDegrees: (value: number) => void;
  zmDegrees: number;
  onZmDegrees: (value: number) => void;
}) {
  return (
    <div className={styles.rotationControls}>
      <label className={styles.rotationControl}>
        <span>X–M rotation</span>
        <Range
          min="-180"
          max="180"
          step="1"
          value={xmDegrees}
          onChange={(event) => onXmDegrees(Number(event.target.value))}
        />
        <output>{xmDegrees}°</output>
      </label>
      <label className={styles.rotationControl}>
        <span>Z–M depth</span>
        <Range
          min="-180"
          max="180"
          step="1"
          value={zmDegrees}
          onChange={(event) => onZmDegrees(Number(event.target.value))}
        />
        <output>{zmDegrees}°</output>
      </label>
    </div>
  );
}
