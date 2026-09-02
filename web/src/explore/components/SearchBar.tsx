import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, LookupRow } from '@ui';
import styles from './SearchBar.module.css';

interface SearchBarProps {
  placeholder?: string;
  label?: string;
  hint?: string;
  destination?: 'entity' | 'mesh';
  shortcut?: boolean;
}

/**
 * A Browse launcher, not a second resolver. Identity discovery belongs to the one
 * `/v1/explore/browse` contract; callers may only specialize the destination used
 * after the user chooses a canonical result.
 */
export function SearchBar({
  placeholder = 'word, ILI, frame, player, or id hex…',
  label = 'Find anything in the substrate',
  hint = 'Browse returns canonical matches; choose the entity you mean. Press / to focus.',
  destination = 'entity',
  shortcut = true,
}: SearchBarProps) {
  const [q, setQ] = useState('');
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

  function submit() {
    const ref = q.trim();
    if (!ref) return;
    const params = new URLSearchParams({ q: ref });
    if (destination === 'mesh') params.set('view', 'mesh');
    nav(`/explore?${params.toString()}`);
  }

  return (
    <div className={styles.search}>
      <div className={styles.heading}>
        <span className={styles.label}>{label}</span>
        {hint ? <span className={styles.hint}>{hint}</span> : null}
      </div>
      <LookupRow
        value={q}
        onChange={setQ}
        onSubmit={submit}
        placeholder={placeholder}
        submitLabel={destination === 'mesh' ? 'Find mesh entry' : 'Browse'}
        submitDisabled={!q.trim()}
        ariaLabel={label}
        inputRef={inputRef}
      >
        {q ? (
          <Button type="button" variant="ghost" onClick={() => { setQ(''); inputRef.current?.focus(); }}>
            Clear
          </Button>
        ) : null}
      </LookupRow>
    </div>
  );
}
