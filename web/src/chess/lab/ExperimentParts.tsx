/** Presentational pieces of the experiment runner: experiment cards, parameter fields, job state styling and live-feed rows. */
import {
  Button,
  Field,
  Input,
  Muted,
  SegmentedControl,
  Select,
  SliderField,
  Table,
  Toggle,
  UnstyledButton,
} from '@ui';
import type { LabExperiment } from './experiments';
import type { LabEvent } from './sse';
import type { FieldDef } from './jobFields';
import styles from './ExperimentRunner.module.css';

export function ExperimentCard({
  exp,
  selected,
  blocked,
  onSelect,
}: {
  exp: LabExperiment;
  selected: boolean;
  blocked: boolean;
  onSelect: () => void;
}) {
  return (
    <UnstyledButton
      type="button"
      role="option"
      aria-selected={selected}
      className={[styles.experimentCard, selected && styles.experimentCardSelected, blocked && styles.experimentCardBlocked].filter(Boolean).join(' ')}
      onClick={onSelect}
    >
      <span className={styles.experimentTitle}>{exp.title}</span>
      <span className={styles.experimentTagline}>{exp.tagline}</span>
      {exp.recordsLive && <span className={styles.experimentBadge}>records</span>}
      {blocked && <span className={styles.experimentBadgeWarn}>needs setup</span>}
    </UnstyledButton>
  );
}

export function LabField({ field, value, onChange }: { field: FieldDef; value: string; onChange: (v: string) => void }) {
  if (field.type === 'bool') {
    const on = value === 'true';
    return (
      <Field label={field.label} help={field.help} layout="row" className={styles.fieldWide}>
        <Toggle
          checked={on}
          onCheckedChange={(checked) => onChange(checked ? 'true' : 'false')}
          aria-label={field.label}
        />
      </Field>
    );
  }

  if (field.type === 'select') {
    const labeled = field.optionLabels
      ? field.options.map((o) => ({ value: o, label: field.optionLabels![o] ?? o }))
      : field.options;
    if (labeled.length <= 3 && !field.optionLabels) {
      return (
        <Field label={field.label} help={field.help} className={styles.fieldWide}>
          <SegmentedControl value={value} onValueChange={onChange} options={field.options} label={field.label} />
        </Field>
      );
    }
    if (labeled.length > 4) {
      return (
        <Field label={field.label} help={field.help} className={styles.fieldWide}>
          <Select value={value} aria-label={field.label} onChange={(e) => onChange(e.target.value)}>
            {labeled.map((o) => typeof o === 'string'
              ? <option key={o} value={o}>{o}</option>
              : <option key={o.value} value={o.value}>{o.label}</option>)}
          </Select>
        </Field>
      );
    }
    return (
      <Field label={field.label} help={field.help} className={styles.fieldWide}>
        <SegmentedControl
          value={value}
          onValueChange={onChange}
          options={labeled.map((o) => (typeof o === 'string' ? o : o.value))}
          optionLabels={field.optionLabels}
          label={field.label}
        />
        {field.optionLabels && (
          <Muted className={styles.fieldHint}>{field.optionLabels[value] ?? value}</Muted>
        )}
      </Field>
    );
  }

  if (field.type === 'number') {
    if (field.max !== undefined) {
      return (
        <Field label={field.label} help={field.help} valueDisplay={`${value || '—'}${field.unit ?? ''}`}>
          <SliderField min={field.min} max={field.max} step={field.step ?? 1} value={value} onChange={onChange} label={field.label} />
        </Field>
      );
    }
    return (
      <Field label={field.label} help={field.help}>
        <Input type="number" min={field.min} step={field.step ?? 1} value={value} placeholder={field.placeholder}
               aria-label={field.label} onChange={(e) => onChange(e.target.value)} />
      </Field>
    );
  }

  return (
    <Field label={field.label} help={field.help} className={styles.fieldWide}>
      <Input type={field.type === 'password' ? 'password' : 'text'} value={value} placeholder={field.placeholder}
             aria-label={field.label} onChange={(e) => onChange(e.target.value)} />
    </Field>
  );
}

const STATE_CLASS: Record<string, string> = {
  Running: 'stateRunning',
  Pending: 'statePending',
  Completed: 'stateCompleted',
  Failed: 'stateFailed',
  Cancelled: 'stateCancelled',
};

export function stateStyle(state: string): string | undefined {
  const key = STATE_CLASS[state];
  return key ? styles[key as keyof typeof styles] : styles.state;
}

export function LabRow({ e, onAction }: {
  e: LabEvent;
  onAction: (kind: string, config: Record<string, string>) => Promise<void>;
}) {
  if (e.title && e.rows) {
    return (
      <li className={styles.tableRow}>
        <div className={styles.tableTitle}>{e.title}</div>
        <div className={styles.tableScroll}>
          <Table className={styles.labTable}>
            {e.columns && (
              <thead><tr>{e.columns.map((c, i) => <th key={i}>{c}</th>)}{e.action && <th>Action</th>}</tr></thead>
            )}
            <tbody>
              {e.rows.map((row, ri) => (
                <tr key={ri}>
                  {row.map((cell, ci) => <td key={ci}>{cell}</td>)}
                  {e.action && (
                    <td>
                      <Button onClick={() => void onAction(e.action!.kind, {
                        [e.action!.configKey]: row[e.action!.valueColumn] ?? '',
                      })}>{e.action.label}</Button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </Table>
        </div>
      </li>
    );
  }
  if (e.name !== undefined && e.value !== undefined) {
    return <li className={styles.metric}><span className={styles.metricName}>{e.name}</span><b>{e.value}{e.unit ?? ''}</b></li>;
  }
  if (e.done !== undefined && e.total !== undefined) {
    return <li className={styles.progress}>progress {e.done}/{e.total}{e.label ? ` · ${e.label}` : ''}</li>;
  }
  if (e.result !== undefined && (e.white !== undefined || e.black !== undefined)) {
    return <li>#{e.index ?? '?'} {e.white ?? '?'} vs {e.black ?? '?'} — <b>{e.result}</b></li>;
  }
  if (e.finalState !== undefined) {
    return <li className={styles.done}>done: {e.finalState}{e.message ? ` (${e.message})` : ''}</li>;
  }
  if (e.level !== undefined && e.message !== undefined) {
    const lvl = e.level === 'error' ? styles.logError : (e.level === 'warning' || e.level === 'warn') ? styles.logWarn : styles.logInfo;
    return <li className={lvl}>[{e.level}] {e.message}</li>;
  }
  return <li><Muted>{JSON.stringify(e)}</Muted></li>;
}