import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { exploreResolve } from '../api';

export function SearchBar({ placeholder = 'word, ILI, frame, or id hex…' }: { placeholder?: string }) {
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const nav = useNavigate();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
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
    <form className="explore-search" onSubmit={submit}>
      <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={placeholder} />
      <button type="submit" disabled={busy}>{busy ? '…' : 'Resolve'}</button>
      {err ? <span className="error">{err}</span> : null}
    </form>
  );
}
