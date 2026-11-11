/** Consensus-graph controls and key: projection/hops/fanout/capacity toolbar, relation filter and "How to read this". */
import type { ReactNode } from 'react';
import { Field, Input, Muted, SegmentedControl, ToggleButton } from '@ui';
import styles from './ConsensusGraph.module.css';
import { relationWords } from '../../domain/Strand/StrandList';
import { clamp, type Dim } from './graphData';

export function GraphLegend({
  dim,
  onDimChange,
  hops,
  hopsMax,
  onHopsChange,
  fanout,
  fanoutMax,
  onFanoutChange,
  maxNodes,
  maxNodesMax,
  onMaxNodesChange,
  toolbar,
  nodeCount,
  linkCount,
  maxHop,
  expanded,
  relations,
  hidden,
  onToggleRelation,
  beliefMode,
  walkSteps,
}: {
  dim: Dim;
  onDimChange?: (d: Dim) => void;
  hops?: number;
  hopsMax: number;
  onHopsChange?: (n: number) => void;
  fanout?: number;
  fanoutMax: number;
  onFanoutChange?: (n: number) => void;
  maxNodes?: number;
  maxNodesMax: number;
  onMaxNodesChange?: (n: number) => void;
  toolbar?: ReactNode;
  nodeCount: number;
  linkCount: number;
  maxHop: number;
  expanded: boolean;
  relations: [string, number][];
  hidden: ReadonlySet<string>;
  onToggleRelation: (type: string) => void;
  beliefMode: boolean;
  walkSteps: number;
}) {
  return (
    <>
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
          {`${nodeCount.toLocaleString()} ${nodeCount === 1 ? 'subject' : 'subjects'} · ${linkCount.toLocaleString()} ${linkCount === 1 ? 'strand' : 'strands'} · reaches ${maxHop} ${maxHop === 1 ? 'hop' : 'hops'} out`}
          {expanded ? '' : ' · expand to walk further'}
        </Muted>
      </div>
      {relations.length > 0 ? (
        <div className={styles.relations} role="group" aria-label="Relations shown">
          {relations.map(([type, count]) => (
            <ToggleButton
              key={type}
              className={styles.relation}
              pressed={!hidden.has(type)}
              onClick={() => onToggleRelation(type)}
              title={hidden.has(type) ? 'Show these strands' : 'Hide these strands'}
            >
              {type === 'walk' ? 'your walk' : relationWords(type)} <span className={styles.relationCount}>{count}</span>
            </ToggleButton>
          ))}
        </div>
      ) : null}
      <details className={styles.key}>
        <summary>How to read this</summary>
        <ul>
          <li>Each dot is a subject; each line is a strand that sources attest. Thicker, brighter strands pull harder; red strands are disputed or refuted.</li>
          <li>Larger dots have more witnesses. Dot colour shows whether the strands around it are supported or opposed.</li>
          <li>{beliefMode
            ? 'Belief layout: positions come from the standing of the strands themselves, so subjects that sources tie together sit together.'
            : 'Force layout: strands pull subjects together in proportion to their standing.'}</li>
          <li>{dim !== '2d'
            ? 'Move with WASD or the arrow keys, Q/E to turn, PgUp/PgDn for up and down. Drag to orbit, right-drag to pan. Click a subject to fly to it; double-click to open it.'
            : 'Drag to pan, scroll to zoom. Double-click a subject to open it.'}</li>
        </ul>
      </details>
      {walkSteps > 0 ? (
        <Muted className={styles.overlayNote}>Walk path overlay ({walkSteps} steps)</Muted>
      ) : null}
    </>
  );
}
