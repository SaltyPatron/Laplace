import { useState } from 'react';
import { Button, ErrorText, Muted } from '@ui';
import { useAppStore } from '../store';
import { DialPanel } from './DialPanel';
import { runQuery } from './api';
import { DIAL_DEFAULTS, type QueryResult } from './types';

export function TrajectoryCalculation({ topic }: { topic: string }) {
  const { tenant, quoteId } = useAppStore();
  const [dials, setDials] = useState(DIAL_DEFAULTS), [result, setResult] = useState<QueryResult | null>(null), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  return <form onSubmit={async event => {
    event.preventDefault(); setBusy(true); setError(''); setResult(null);
    try {
      const seed = dials.seed.trim() === '' ? undefined : Number(dials.seed);
      if (seed !== undefined && !Number.isSafeInteger(seed)) throw new Error('Seed must be an exactly representable integer.');
      setResult(await runQuery({ ...dials, seed, topic, shape: 'generate' }, { tenant, quoteId }));
    } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); }
    finally { setBusy(false); }
  }}><p>Calculate a seeded trajectory using this content as the starting point.</p><DialPanel shape="generate" idPrefix="trajectory-dial" dials={dials} onChange={setDials} /><Button type="submit" loading={busy}>Calculate trajectory</Button>{error && <ErrorText>{error}</ErrorText>}{result && (result.rows.length ? <ol>{result.rows.map((row, index) => <li key={index}>{row.reply}</li>)}</ol> : <Muted>No trajectory rows returned.</Muted>)}</form>;
}
