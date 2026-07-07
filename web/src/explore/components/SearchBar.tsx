import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { LookupRow } from '@ui';
import { exploreResolve } from '../api';

export function SearchBar({ placeholder = 'word, ILI, frame, or id hex…' }: { placeholder?: string }) {
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const nav = useNavigate();

  async function submit() {
    const ref = q.trim();
    if (!ref || busy) return;
    setBusy(true);
    setErr(null);
    try {
      const hit = await exploreResolve(ref);
      nav(`/explore/entity/${hit.id_hex}`);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="explore-search">
      <LookupRow
        value={q}
        onChange={setQ}
        onSubmit={() => void submit()}
        placeholder={placeholder}
        submitLabel={busy ? '…' : 'Resolve'}
        disabled={busy}
        error={err}
      />
    </div>
  );
}
