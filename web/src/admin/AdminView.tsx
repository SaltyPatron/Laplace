import { lazy, Suspense, useState } from 'react';
import { Link } from 'react-router-dom';
import { Banner, LoadingText, SegmentedControl } from '@ui';
import { useSectionParam } from '../layout/useSectionParam';
import styles from './Admin.module.css';

const IngestControl = lazy(() => import('./IngestControl').then((m) => ({ default: m.IngestControl })));
const IngestJournal = lazy(() => import('./IngestJournal').then((m) => ({ default: m.IngestJournal })));
const Activity = lazy(() => import('./Activity').then((m) => ({ default: m.Activity })));
const OpConsole = lazy(() => import('./OpConsole').then((m) => ({ default: m.OpConsole })));
const Repair = lazy(() => import('./Repair').then((m) => ({ default: m.Repair })));
const Agents = lazy(() => import('./Agents').then((m) => ({ default: m.Agents })));

type Section = 'ingest' | 'activity' | 'ops' | 'repair' | 'agents';
const SECTIONS: Section[] = ['ingest', 'activity', 'ops', 'repair', 'agents'];
/** The URL owns section navigation; this remains a convenience, not an auth boundary. */
export function AdminView() {
  const [section, setSection] = useSectionParam('section', SECTIONS, 'ingest');
  const [ingestRefreshSignal, setIngestRefreshSignal] = useState(0);
  const refreshIngest = () => setIngestRefreshSignal((value) => value + 1);
  return <div className={styles.page}>
    <header className={styles.header}><h2 className={styles.title}>Operator</h2>
      <SegmentedControl value={section} onValueChange={(value) => setSection(value as Section)} options={SECTIONS} label="Operator section" />
    </header>
    <nav className={styles.toolbar} aria-label="Data management"><Link to="/data">Add and inspect data</Link><Link to="/explore/warehouse">Browse sources</Link></nav>
    <Banner variant="warning">
      Operator actions may affect shared database state. Authentication mode controls access; tenant headers are not an operator-role boundary.
    </Banner>
    <Suspense fallback={<LoadingText>Loading operator tool…</LoadingText>}>
      {section === 'ingest' ? <><IngestControl onStarted={refreshIngest} /><IngestJournal refreshSignal={ingestRefreshSignal} /></> : section === 'activity' ? <Activity />
        : section === 'ops' ? <OpConsole /> : section === 'repair' ? <Repair /> : <Agents />}
    </Suspense>
  </div>;
}
