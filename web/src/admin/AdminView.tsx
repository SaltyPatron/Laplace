import { useState } from 'react';
import { Banner, SegmentedControl } from '@ui';
import { Activity } from './Activity';
import { Agents } from './Agents';
import { IngestJournal } from './IngestJournal';
import { OpConsole } from './OpConsole';
import { Repair } from './Repair';
import styles from './Admin.module.css';

type Section = 'ingest' | 'activity' | 'ops' | 'repair' | 'agents';

const SECTIONS: Section[] = ['ingest', 'activity', 'ops', 'repair', 'agents'];

/**
 * The operator surface.
 *
 * Everything here runs against endpoints that were already open to anyone who
 * could reach the host — `POST /v1/op` is an allow-listed named call and takes
 * no SQL text, and the tenant header is unauthenticated. So this console adds
 * reach, not privilege, and the banner says so: nothing on this page is a
 * security boundary, and it must not be mistaken for one while auth is stubbed.
 *
 * ONE EXCEPTION TO "REACH, NOT PRIVILEGE", and it is deliberate: `ops.evict_source`
 * is now on the endpoint's write allow-list, so this console can retract a
 * source's testimony. That is a real capability increase over what the surface
 * granted before, taken because retraction is a first-class operator duty — the
 * Ops tab confirms it explicitly rather than treating it as one call among 358.
 */
export function AdminView() {
  const [section, setSection] = useState<Section>('ingest');

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h2 className={styles.title}>Operator</h2>
        <SegmentedControl
          value={section}
          onValueChange={(v) => setSection(v as Section)}
          options={SECTIONS}
          label="Operator section"
        />
      </header>

      <Banner variant="warning">
        Privileges are not enforced. There is no authentication on this deployment — the tenant is
        a free-text header any caller can set, and every operation here is reachable directly over
        HTTP without it. Treat this page as a convenience over public endpoints, not as an admin
        boundary. Cancellation, repair and retraction are all reachable the same way.
      </Banner>

      {section === 'ingest' ? <IngestJournal />
        : section === 'activity' ? <Activity />
        : section === 'ops' ? <OpConsole />
        : section === 'repair' ? <Repair />
        : <Agents />}
    </div>
  );
}
