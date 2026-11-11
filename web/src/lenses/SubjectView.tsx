import { lazy, Suspense, useId, useMemo, useState } from 'react';
import { describeFailure } from '../api/client';
import { readFacet, readIdentity, FacetUnavailable } from '../data/facets';
import { StrandList } from '../domain/Strand/StrandList';
import { useAppStore } from '../store';
import { useReadResource } from '../ui/hooks/useReadResource';
import { LoadingText } from '../ui/primitives/Text';
import { useEntityLabels } from '../shell/inspector';
import { lensesFor, type FacetLens, type LensDef, type VisualLens } from './registry';
import styles from './SubjectView.module.css';

function FacetBody({ id, lens }: { id: string; lens: FacetLens }) {
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

const visualCache = new Map<string, ReturnType<typeof lazy>>();
function VisualBody({ id, lens }: { id: string; lens: VisualLens }) {
  const Component = useMemo(() => {
    let component = visualCache.get(lens.id);
    if (!component) { component = lazy(lens.load); visualCache.set(lens.id, component); }
    return component as React.ComponentType<{ id: string }>;
  }, [lens]);
  return <Suspense fallback={<LoadingText>Loading view…</LoadingText>}><Component id={id} /></Suspense>;
}

export interface SubjectViewProps {
  id: string;
  /** Uncontrolled starting lens. */
  initialLens?: string;
  /** Controlled lens (e.g. kept in the page URL). */
  lens?: string;
  onLensChange?: (lens: string) => void;
  /** Lenses to leave out in this context (e.g. heavy canvases in a narrow drawer). */
  exclude?: string[];
}

/** Any entity, seen through its lenses. Used by the inspector drawer and the entity page. */
export function SubjectView({ id, initialLens = 'means', lens: controlled, onLensChange, exclude = [] }: SubjectViewProps) {
  const { tenant, quoteId } = useAppStore();
  const remember = useEntityLabels((s) => s.remember);
  const identity = useReadResource({ key: JSON.stringify(['identity', id, tenant, quoteId]),
    read: async (signal) => { const value = await readIdentity(id, { tenant, quoteId, signal }); remember(id, value.ref.label); return value; } });
  const lenses: LensDef[] = lensesFor(identity.data?.ref ?? null).filter((l) => !exclude.includes(l.id));
  const [own, setOwn] = useState(initialLens);
  const active = controlled ?? own;
  const current = lenses.find((l) => l.id === active) ?? lenses[0];
  const choose = (next: string) => { if (onLensChange) onLensChange(next); else setOwn(next); };
  const tabsId = useId();
  return <div className={styles.subject}>
    {identity.data && <p className={styles.identity}>
      Tier {identity.data.ref.tier}{identity.data.ref.kind ? ` · ${identity.data.ref.kind}` : ''}{identity.data.size ? ` · ${identity.data.size} parts` : ''}
      {!identity.data.exists && ' · not recorded'}
    </p>}
    {identity.error && <p className={styles.note} role="alert">{describeFailure(identity.error).message}</p>}
    <div role="tablist" aria-label="Views of this subject" className={styles.tabs}>
      {lenses.map((l) => <button key={l.id} type="button" role="tab" id={`${tabsId}-${l.id}`} aria-selected={l.id === current.id}
        aria-controls={`${tabsId}-panel`} className={styles.tab} onClick={() => choose(l.id)} title={l.question}>{l.label}</button>)}
    </div>
    <section role="tabpanel" id={`${tabsId}-panel`} aria-labelledby={`${tabsId}-${current.id}`} aria-label={current.label} className={styles.panel}>
      <p className={styles.question}>{current.question}</p>
      {current.kind === 'facet'
        ? <FacetBody key={`${id}-${current.id}`} id={id} lens={current} />
        : <VisualBody key={`${id}-${current.id}`} id={id} lens={current} />}
    </section>
  </div>;
}
