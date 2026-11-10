import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { create } from 'zustand';
import { browseSubstrate } from '../explore/browse/api';
import type { BrowseHit } from '../explore/browse/types';
import { useAppStore } from '../store';
import { Icon } from '../ui/icons';
import { useInspector } from './inspector';
import { NAV } from './nav';
import styles from './CommandPalette.module.css';

export const usePalette = create<{ open: boolean; seed: string; show: (seed?: string) => void; hide: () => void }>((set) => ({
  open: false, seed: '',
  show: (seed = '') => set({ open: true, seed }),
  hide: () => set({ open: false, seed: '' }),
}));

type Option =
  | { kind: 'entity'; key: string; hit: BrowseHit }
  | { kind: 'search'; key: string; text: string }
  | { kind: 'go'; key: string; label: string; path: string; hint: string };

/** One search for the whole app: entities as you type, the full search page, and every destination. */
export function CommandPalette() {
  const { open, seed, hide } = usePalette();
  const { tenant, quoteId } = useAppStore();
  const navigate = useNavigate();
  const inspector = useInspector();
  const [text, setText] = useState('');
  const [hits, setHits] = useState<BrowseHit[]>([]);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const [active, setActive] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const listId = useId();

  useEffect(() => { if (open) { setText(seed); setActive(0); requestAnimationFrame(() => input.current?.focus()); } }, [open, seed]);

  useEffect(() => {
    const query = text.trim();
    if (!open || query.length < 2) { setHits([]); setBusy(false); setFailed(false); return; }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      setBusy(true); setFailed(false);
      browseSubstrate({ query, limit: 8, capacity: 2048 }, { tenant, quoteId, signal: controller.signal })
        .then((response) => setHits(response.hits))
        .catch(() => { if (!controller.signal.aborted) { setHits([]); setFailed(true); } })
        .finally(() => { if (!controller.signal.aborted) setBusy(false); });
    }, 200);
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [open, text, tenant, quoteId]);

  const options = useMemo<Option[]>(() => {
    const query = text.trim().toLowerCase();
    const destinations = NAV.flatMap((item) => [
      { label: item.label, path: item.path, hint: item.description },
      ...(item.children ?? []).filter((child) => child.path !== item.path).map((child) => ({ label: `${item.label} › ${child.label}`, path: child.path, hint: '' })),
    ]).filter((d) => !query || d.label.toLowerCase().includes(query));
    return [
      ...hits.map((hit) => ({ kind: 'entity' as const, key: `e:${hit.id_hex}`, hit })),
      ...(query ? [{ kind: 'search' as const, key: 'search', text: text.trim() }] : []),
      ...destinations.slice(0, query ? 4 : 12).map((d) => ({ kind: 'go' as const, key: `go:${d.path}`, ...d })),
    ];
  }, [hits, text]);

  useEffect(() => { setActive((index) => Math.min(index, Math.max(0, options.length - 1))); }, [options.length]);

  if (!open) return null;

  function choose(option: Option | undefined) {
    if (!option) return;
    hide();
    if (option.kind === 'entity') inspector.open(option.hit.id_hex, option.hit.label);
    else if (option.kind === 'search') navigate(`/explore?q=${encodeURIComponent(option.text)}`);
    else navigate(option.path);
  }

  return (
    <div className={styles.backdrop} onMouseDown={(event) => { if (event.target === event.currentTarget) hide(); }}>
      <div className={styles.palette} role="dialog" aria-modal="true" aria-label="Search and go to">
        <div className={styles.field}>
          <Icon name="search" />
          <input ref={input} className={styles.input} value={text} placeholder="Search anything, or type a place to go…"
            role="combobox" aria-expanded="true" aria-controls={listId} aria-autocomplete="list"
            aria-activedescendant={options[active] ? `${listId}-${active}` : undefined}
            onChange={(event) => { setText(event.target.value); setActive(0); }}
            onKeyDown={(event) => {
              if (event.key === 'Escape') { event.preventDefault(); hide(); }
              else if (event.key === 'ArrowDown') { event.preventDefault(); setActive((i) => Math.min(options.length - 1, i + 1)); }
              else if (event.key === 'ArrowUp') { event.preventDefault(); setActive((i) => Math.max(0, i - 1)); }
              else if (event.key === 'Enter') { event.preventDefault(); choose(options[active]); }
            }} />
          <kbd className={styles.kbd}>Esc</kbd>
        </div>
        <ul id={listId} role="listbox" className={styles.list} aria-label="Results">
          {options.map((option, index) => (
            <li key={option.key} id={`${listId}-${index}`} role="option" aria-selected={index === active}
              className={styles.option} onMouseEnter={() => setActive(index)} onMouseDown={(event) => { event.preventDefault(); choose(option); }}>
              {option.kind === 'entity' ? <>
                <span className={styles.primary}>{option.hit.label}</span>
                <span className={styles.meta}>{option.hit.type || 'content'} · T{option.hit.tier}{option.hit.witnesses ? ` · ${option.hit.witnesses} witnesses` : ''}</span>
              </> : option.kind === 'search' ? <>
                <span className={styles.primary}>Search for “{option.text}”</span>
                <span className={styles.meta}>Open full results</span>
              </> : <>
                <span className={styles.primary}>{option.label}</span>
                <span className={styles.meta}>{option.hint || 'Go to page'}</span>
              </>}
            </li>
          ))}
        </ul>
        <p className={styles.status} role="status">
          {busy ? 'Searching…' : failed ? 'Search is unavailable right now; you can still go to a page.' : text.trim().length >= 2 && !hits.length ? 'No recorded content matches yet.' : ' '}
        </p>
      </div>
    </div>
  );
}
