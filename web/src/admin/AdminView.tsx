import { Link } from 'react-router-dom';
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
    <header className={styles.header}><h2 className={styles.title}>Operator</h2>
      <SegmentedControl value={section} onValueChange={(value) => setSection(value as Section)} options={SECTIONS} label="Operator section" />
    </header>
    <nav className={styles.toolbar} aria-label="Data management"><Link to="/data">Add and inspect data</Link><Link to="/explore/warehouse">Browse sources</Link></nav>
    <Banner variant="warning">
      These controls can affect shared database state, not only the selected tenant.
      Sign-in and API-key requirements depend on the server's authentication mode;
      header tenancy is for trusted local development. Signing in is not an operator-role
      check. Host service controls separately require the operator credential.
    </Banner>
    {section === 'ingest' ? <IngestJournal /> : section === 'activity' ? <Activity />
      : section === 'ops' ? <OpConsole /> : section === 'repair' ? <Repair /> : <Agents />}
  </div>;
}
