import { Banner, SegmentedControl } from '@ui';
import { Activity } from './Activity';
import { Agents } from './Agents';
import { IngestJournal } from './IngestJournal';
import { OpConsole } from './OpConsole';
import { Repair } from './Repair';
import { useSectionParam } from '../layout/useSectionParam';
import styles from './Admin.module.css';

type Section = 'ingest' | 'activity' | 'ops' | 'repair' | 'agents';
const SECTIONS: Section[] = ['ingest', 'activity', 'ops', 'repair', 'agents'];

/** The URL owns section navigation; this remains a convenience, not an auth boundary. */
export function AdminView() {
  const [section, setSection] = useSectionParam('section', SECTIONS, 'ingest');
  return <div className={styles.page}>
    <header className={styles.header}>
      <h2 className={styles.title}>Operator</h2>
      <SegmentedControl value={section} onValueChange={(value) => setSection(value as Section)}
        options={SECTIONS} label="Operator section" />
    </header>
    <Banner variant="warning">
      Privileges are not enforced. There is no authentication on this deployment — the tenant is
      a free-text header any caller can set, and every operation here is reachable directly over
      HTTP without it. Treat this page as a convenience over public endpoints, not as an admin
      boundary. Cancellation, repair and retraction are all reachable the same way.
    </Banner>
    {section === 'ingest' ? <IngestJournal /> : section === 'activity' ? <Activity />
      : section === 'ops' ? <OpConsole /> : section === 'repair' ? <Repair /> : <Agents />}
  </div>;
}
