import { useRef, useState } from 'react';
import { Button, ErrorText, Field, Input, Modal, Select } from '@ui';
import { plainMessage } from '../api/client';
import { recordResult, type HostedEvent } from './api';
import type { Match } from './model';
import styles from './Events.module.css';

const placeLabel = (place: number, of: number) =>
  of === 2 ? (place === 1 ? 'Won' : 'Lost') : `${place}${['th', 'st', 'nd', 'rd'][place % 100 > 10 && place % 100 < 14 ? 0 : place % 10] ?? 'th'}`;

/** The host's result for one match: a score and a finishing place for each entrant, every place used once. */
export function ResultEntry({ event, match, onClose, onSaved }: { event: HostedEvent; match: Match; onClose: () => void; onSaved: () => Promise<void> }) {
  const [rows, setRows] = useState(() => match.slots.map((slot, position) => ({ score: slot.score ?? '', place: slot.place ?? position + 1 })));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const pending = useRef(false);
  const names = new Map(event.entrants.map((entrant) => [entrant.id, entrant.name]));

  // Choosing a place someone else holds swaps the two, so the places always stay a full set.
  function place(position: number, next: number) {
    setRows((current) => current.map((row, i) =>
      i === position ? { ...row, place: next } : row.place === next ? { ...row, place: current[position].place } : row));
  }
  async function save() {
    if (pending.current) return;
    pending.current = true; setBusy(true); setError('');
    try {
      await recordResult(event.id, match.id, rows.map((row) => ({ score: row.score.trim() || null, place: row.place })));
      await onSaved();
    } catch (failure) {
      setError(plainMessage(failure));
    } finally { pending.current = false; setBusy(false); }
  }

  return (
    <Modal open onClose={() => { if (!busy) onClose(); }} title={match.name ?? 'Result'} actions={<>
      <Button variant="ghost" disabled={busy} onClick={onClose}>Cancel</Button>
      <Button loading={busy} disabled={busy} onClick={() => void save()}>Save result</Button>
    </>}>
      <div className={styles.result}>
        {match.slots.map((slot, position) => {
          const name = names.get(slot.entrant ?? '') ?? `Entrant ${position + 1}`;
          return (
            <fieldset key={position} className={styles.resultRow}>
              <legend>{name}</legend>
              <Field label="Score">
                <Input value={rows[position].score} maxLength={16} inputMode="numeric" autoComplete="off"
                  onChange={(change) => setRows((current) => current.map((row, i) => (i === position ? { ...row, score: change.target.value } : row)))} />
              </Field>
              <Field label="Finish">
                <Select value={rows[position].place} onChange={(change) => place(position, Number(change.target.value))}>
                  {match.slots.map((_, n) => <option key={n} value={n + 1}>{placeLabel(n + 1, match.slots.length)}</option>)}
                </Select>
              </Field>
            </fieldset>
          );
        })}
      </div>
      {error && <ErrorText role="alert">{error}</ErrorText>}
    </Modal>
  );
}
