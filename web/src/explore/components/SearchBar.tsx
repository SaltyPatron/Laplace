import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, LookupRow } from '@ui';
import { ApiError } from '../../api/client';
import { exploreResolve } from '../api';
import styles from './SearchBar.module.css';

interface SearchBarProps {
  placeholder?: string;
  label?: string;
  hint?: string;
  destination?: 'entity' | 'mesh';
  shortcut?: boolean;
}

export function SearchBar({
  placeholder = 'word, ILI, frame, player, or id hex…',
  label = 'Find anything in the substrate',
  hint = 'Names and close spellings work. Press / to focus.',
  destination = 'entity',
  shortcut = true,
}: SearchBarProps) {
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const nav = useNavigate();

  useEffect(() => {
    if (!shortcut) return;
    const focusSearch = (event: KeyboardEvent) => {
      if (event.key !== '/' || event.altKey || event.ctrlKey || event.metaKey) return;
      const target = event.target as HTMLElement | null;
      if (target?.matches('input, textarea, select, [contenteditable="true"]')) return;
      event.preventDefault();
      inputRef.current?.focus();
    };
    window.addEventListener('keydown', focusSearch);
    return () => window.removeEventListener('keydown', focusSearch);
  }, [shortcut]);

  async function submit() {
    const ref = q.trim();
    if (!ref || busy) return;
    setBusy(true);
    setErr(null);
    try {
      const hit = await exploreResolve(ref);
      if (!hit.exists) {
        nav(`/explore/notfound/${encodeURIComponent(ref)}`);
        return;
      }
      nav(destination === 'mesh'
        ? `/explore/mesh/${hit.id_hex}`
        : `/explore/entity/${hit.id_hex}`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        nav(`/explore/notfound/${encodeURIComponent(ref)}`);
      } else if (e instanceof ApiError && e.status === 503) {
        setErr('Search is temporarily unavailable. Your query is still here; try again.');
      } else {
        setErr(e instanceof Error ? e.message : String(e));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={styles.search}>
      <div className={styles.heading}>
        <span className={styles.label}>{label}</span>
        {hint ? <span className={styles.hint}>{hint}</span> : null}
      </div>
      <LookupRow
        value={q}
        onChange={(value) => { setQ(value); if (err) setErr(null); }}
        onSubmit={() => void submit()}
        placeholder={placeholder}
        submitLabel={destination === 'mesh' ? 'Enter mesh' : 'Open'}
        disabled={busy}
        busy={busy}
        submitDisabled={!q.trim()}
        error={err}
        ariaLabel={label}
        inputRef={inputRef}
      >
        {q ? (
          <Button type="button" variant="ghost" disabled={busy} onClick={() => { setQ(''); setErr(null); inputRef.current?.focus(); }}>
            Clear
          </Button>
        ) : null}
      </LookupRow>
    </div>
  );
}
