import { useId, useState } from 'react';
import { describeFailure } from '../api/client';
import { readFacet, readIdentity, FacetUnavailable } from '../data/facets';
import { StrandList } from '../domain/Strand/StrandList';
import { useAppStore } from '../store';
import { useReadResource } from '../ui/hooks/useReadResource';
import { LoadingText } from '../ui/primitives/Text';
import { useEntityLabels } from '../shell/inspector';
import { lensesFor, type LensDef } from './registry';
import styles from './SubjectView.module.css';

function LensBody({ id, lens }: { id: string; lens: LensDef }) {
  const { tenant, quoteId } = useAppStore();
  const read = useReadResource({ key: JSON.stringify(['facet', id, lens.facet, tenant, quoteId]),
    read: (signal) => readFacet(id, lens.facet, { limit: 100 }, { tenant, quoteId, signal }) });
  if (read.error instanceof FacetUnavailable) return <p className={styles.note}>This view needs a backend read that is not deployed yet.</p>;
  if (read.error) return <p className={styles.note} role="alert">{describeFailure(read.error).message} <button type="button" className={styles.link} onClick={() => void read.reload()}>Try again</button></p>;
  if (!read.data) return <LoadingText>Reading…</LoadingText>;
  const facet = read.data;
  return <>
    <p className={styles.summary}>
      {facet.total != null ? `${facet.total.toLocaleString()} ${facet.total === 1 ? 'result' : 'results'}` : `${facet.items.length.toLocaleString()}+ results`}
      {facet.sources.length > 0 && ` · from ${facet.sources.length} ${facet.sources.length === 1 ? 'source' : 'sources'}`}
    </p>
    <StrandList subjectId={id} subjectLabel={facet.entity.label} strands={facet.items} groupByKind={lens.groupByKind} empty={lens.empty} />
  </>;
}

/** Any entity, seen through its lenses. Used by the inspector drawer and the entity page. */
export function SubjectView({ id, initialLens = 'means' }: { id: string; initialLens?: string }) {
  const { tenant, quoteId } = useAppStore();
  const remember = useEntityLabels((s) => s.remember);
  const identity = useReadResource({ key: JSON.stringify(['identity', id, tenant, quoteId]),
    read: async (signal) => { const value = await readIdentity(id, { tenant, quoteId, signal }); remember(id, value.ref.label); return value; } });
  const lenses = lensesFor(identity.data?.ref ?? null);
  const [active, setActive] = useState(initialLens);
  const current = lenses.find((lens) => lens.id === active) ?? lenses[0];
  const tabsId = useId();
  return <div className={styles.subject}>
    {identity.data && <p className={styles.identity}>
      Tier {identity.data.ref.tier}{identity.data.ref.kind ? ` · ${identity.data.ref.kind}` : ''}{identity.data.size ? ` · ${identity.data.size} parts` : ''}
      {!identity.data.exists && ' · not recorded'}
    </p>}
    {identity.error && <p className={styles.note} role="alert">{describeFailure(identity.error).message}</p>}
    <div role="tablist" aria-label="Views of this subject" className={styles.tabs}>
      {lenses.map((lens) => <button key={lens.id} type="button" role="tab" id={`${tabsId}-${lens.id}`} aria-selected={lens.id === current.id}
        aria-controls={`${tabsId}-panel`} className={styles.tab} onClick={() => setActive(lens.id)} title={lens.question}>{lens.label}</button>)}
    </div>
    <section role="tabpanel" id={`${tabsId}-panel`} aria-labelledby={`${tabsId}-${current.id}`} aria-label={current.label} className={styles.panel}>
      <p className={styles.question}>{current.question}</p>
      <LensBody key={`${id}-${current.id}`} id={id} lens={current} />
    </section>
  </div>;
}
