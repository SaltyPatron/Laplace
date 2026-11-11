import { useParams, useSearchParams, Link as RouterLink } from 'react-router-dom';
import { plainMessage, requestIdOf } from '../api/client';
import { ErrorText } from '../ui/primitives/Text';
import { readIdentity } from '../data/facets';
import { useEntityLabels } from '../shell/inspector';
import { useAppStore } from '../store';
import { useReadResource } from '../ui/hooks/useReadResource';
import { LEGACY_DETAIL } from './registry';
import { SubjectView } from './SubjectView';
import styles from './EntityPage.module.css';

/**
 * One page per entity: a header and one row of lenses. Nothing hidden behind a collapsed panel or a second
 * tab bar; "Appears in" is a lens like any other. The lens is kept in the address (?lens=), and old
 * ?detail= addresses open the matching lens.
 */
export function EntityPage() {
  const { idHex = '' } = useParams();
  const id = idHex.toLowerCase();
  const [params, setParams] = useSearchParams();
  const { tenant, quoteId, setExploreSeedPrompt } = useAppStore();
  const known = useEntityLabels((s) => s.labels[id]);
  const identity = useReadResource({ key: JSON.stringify(['identity', id, tenant, quoteId]), read: (signal) => readIdentity(id, { tenant, quoteId, signal }) });
  const label = identity.data?.ref.label ?? known;
  const lens = params.get('lens') ?? LEGACY_DETAIL[params.get('detail') ?? ''] ?? 'means';
  if (!/^[0-9a-f]{32}$/.test(id)) return <section className={styles.page}><h1>Not an entity address</h1><p>Entity addresses are 32 hexadecimal characters.</p></section>;
  return <article className={styles.page} aria-labelledby="entity-title">
    <header className={styles.header}>
      <div className={styles.titleBlock}>
        <h1 id="entity-title" className={styles.title} title={id}>{label ?? (identity.error ? id : 'Loading…')}</h1>
        <button type="button" className={styles.copy} onClick={() => void navigator.clipboard?.writeText(id)} title="Copy the content address">
          <code>{id.slice(0, 8)}…{id.slice(-4)}</code> Copy
        </button>
      </div>
      <nav className={styles.actions} aria-label="Do more with this">
        <RouterLink to={`/explore/matchup?x=${encodeURIComponent(label ?? id)}`}>Compare</RouterLink>
        <RouterLink to={`/proof?q=${encodeURIComponent(label ?? id)}`}>Storage proof</RouterLink>
        <RouterLink to="/chat" onClick={() => setExploreSeedPrompt(`What is "${label ?? id}"?`)}>Ask about it</RouterLink>
      </nav>
    </header>
    {identity.error && !label && <ErrorText requestId={requestIdOf(identity.error)}>{plainMessage(identity.error)}</ErrorText>}
    <SubjectView id={id} lens={lens} onLensChange={(next) => {
      const nextParams = new URLSearchParams(params);
      nextParams.delete('detail'); nextParams.delete('inspect');
      if (next === 'means') nextParams.delete('lens'); else nextParams.set('lens', next);
      setParams(nextParams, { replace: true, preventScrollReset: true });
    }} />
  </article>;
}
