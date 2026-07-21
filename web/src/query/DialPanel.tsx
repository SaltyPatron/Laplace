import { Checkbox, Field, Input, SliderField } from '@ui';
import { SHAPE_DIALS, type QueryDials } from './types';
import styles from './DialPanel.module.css';

interface Props {
  shape: string;
  dials: QueryDials;
  onChange: (next: QueryDials) => void;
}

interface DialSpec {
  label: string;
  help: string;
  min?: number;
  max?: number;
  step?: number;
}

/** What each dial does, in the substrate's terms — the tooltip is the manual. */
const SPEC: Record<keyof QueryDials, DialSpec> = {
  depth: { label: 'depth', help: 'How many hops the search may take from the topic.', min: 1, max: 16 },
  breadth: { label: 'breadth', help: 'Beam width — how many candidates survive each hop.', min: 1, max: 32 },
  limit: { label: 'limit', help: 'Rows returned. A plain LIMIT: nothing is scored out, only cut off.', min: 1, max: 500 },
  steps: { label: 'steps', help: 'How many entities the trajectory descent emits.', min: 1, max: 256 },
  spread: { label: 'spread', help: 'How far down the ranked continuations a step may fall. 0 takes the strongest every time.', min: 0, max: 1, step: 0.05 },
  max_stride: { label: 'stride', help: 'Longest n-gram the descent will match before backing off.', min: 1, max: 8 },
  seed: { label: 'seed', help: 'Pin the generator. Same seed and same dials reproduce the run exactly.' },
  directed: { label: 'directed', help: 'Follow edges only in the direction they were witnessed.' },
  use_geometry: { label: 'geometry heuristic', help: 'Admissible A* using S³ distance. Off is plain Dijkstra.' },
};

/**
 * Only the dials the chosen shape consumes. The previous surface accepted
 * top_k, top_p, window and topic_boost on every request and read none of them.
 */
export function DialPanel({ shape, dials, onChange }: Props) {
  const keys = SHAPE_DIALS[shape] ?? [];
  if (keys.length === 0) {
    return <p className={styles.none}>This shape takes no dials — it reads what is witnessed and stops.</p>;
  }

  const set = <K extends keyof QueryDials>(key: K, value: QueryDials[K]) =>
    onChange({ ...dials, [key]: value });

  return (
    <div className={styles.panel}>
      {keys.map((key) => {
        const spec = SPEC[key];
        const id = `dial-${key}`;

        if (key === 'directed' || key === 'use_geometry') {
          return (
            <Field key={key} label={spec.label} help={spec.help} htmlFor={id} layout="row">
              <Checkbox
                id={id}
                checked={dials[key] as boolean}
                onChange={(e) => set(key, e.target.checked as QueryDials[typeof key])}
              />
            </Field>
          );
        }

        if (key === 'seed') {
          return (
            <Field key={key} label={spec.label} help={spec.help} htmlFor={id} layout="row">
              <Input
                id={id}
                type="number"
                placeholder="unpinned"
                value={dials.seed}
                onChange={(e) => set('seed', e.target.value)}
              />
            </Field>
          );
        }

        return (
          <Field
            key={key}
            label={spec.label}
            help={spec.help}
            htmlFor={id}
            layout="row"
            valueDisplay={String(dials[key])}
          >
            <SliderField
              label={spec.label}
              min={spec.min ?? 1}
              max={spec.max ?? 100}
              step={spec.step ?? 1}
              value={String(dials[key])}
              onChange={(v) => set(key, (spec.step ? Number(v) : Math.round(Number(v))) as QueryDials[typeof key])}
            />
          </Field>
        );
      })}
    </div>
  );
}
