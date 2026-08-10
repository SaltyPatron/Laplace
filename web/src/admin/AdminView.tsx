import { useState } from 'react';
import { Banner, SegmentedControl } from '@ui';
import { IngestJournal } from './IngestJournal';
import { OpConsole } from './OpConsole';
import styles from './Admin.module.css';

type Section = 'ingest' | 'ops';

/**
 * The operator surface.
 *
 * Everything here runs against endpoints that were already open to anyone who
 * could reach the host — `POST /v1/op` is an allow-listed named call and takes
 * no SQL text, and the tenant header is unauthenticated. So this console adds
 * reach, not privilege, and the banner says so: nothing on this page is a
 * security boundary, and it must not be mistaken for one while auth is stubbed.
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
          options={['ingest', 'ops']}
          label="Operator section"
        />
      </header>

      <Banner variant="warning">
        Privileges are not enforced. There is no authentication on this deployment — the tenant is
        a free-text header any caller can set, and every operation here is reachable directly over
        HTTP without it. Treat this page as a convenience over public endpoints, not as an admin
        boundary.
      </Banner>

      {section === 'ingest' ? <IngestJournal /> : <OpConsole />}
    </div>
  );
}
