import { useState } from 'react';
import { Muted, ReadStatus, useReadResource } from '@ui';
import { ingestFiles, type IngestRun } from './api';
import { describeRunFailure, ingestStatusTone } from './ingestPresentation';
import styles from './Admin.module.css';

const FAILED_FILE_LIMIT = 50;
const FAILED_FILES_SHOWN = 5;

/**
 * Explains a failed or interrupted run: the stage it reached, the file it was on,
 * and the last lines the runner wrote. Failed file receipts are read only when the
 * operator opens the details, so a long journal does not fan out one request per row.
 */
export function IngestRunFailure({ run, tenant }: { run: IngestRun; tenant: string }) {
  const [open, setOpen] = useState(false);
  const failure = describeRunFailure(run.error ?? '', run.phase, run.status);
  const filesRead = useReadResource({
    key: JSON.stringify(['ingest-failed-files', tenant, run.run_id]),
    enabled: open,
    read: (signal) => ingestFiles(run.run_id, FAILED_FILE_LIMIT, { tenant, signal }),
  });
  const failedFiles = (filesRead.data?.rows ?? []).filter((file) => ingestStatusTone(file.status) === 'failed');
  const withErrors = failedFiles.filter((file) => file.error);
  const shown = (withErrors.length ? withErrors : failedFiles).slice(0, FAILED_FILES_SHOWN);

  return (
    <div className={styles.runErr}>
      <p>{failure.summary}</p>
      <details onToggle={(event) => setOpen(event.currentTarget.open)}>
        <summary>What happened</summary>
        <dl className={styles.reviewDetails}>
          <dt>Stage</dt><dd>{failure.stage ?? 'Not reported'}</dd>
          <dt>File</dt><dd>{failure.file ? <code>{failure.file}</code> : 'Not named in the runner output'}</dd>
          {failure.exitStatus != null && <><dt>Exit status</dt><dd>{failure.exitStatus}</dd></>}
        </dl>
        {failure.excerpt
          ? <><strong>Last runner output</strong><pre className={styles.sig}>{failure.excerpt}</pre></>
          : <Muted>The runner wrote no error output for this run.</Muted>}
        {open && <>
          <ReadStatus label="Failed file receipts" resource={filesRead} />
          {filesRead.data && (shown.length === 0
            ? <Muted>No file receipt for this run is marked as failed.</Muted>
            : <>
              <strong>Files marked failed{failedFiles.length > shown.length ? ` (first ${shown.length} of ${failedFiles.length})` : ''}</strong>
              <ul>{shown.map((file) => <li key={`${file.file_label}:${file.file_id ?? ''}`}>
                <code>{file.file_label}</code>{file.error && <pre className={styles.sig}>{file.error}</pre>}
              </li>)}</ul>
            </>)}
        </>}
        <Muted>
          The complete output is in the ingestion runner&apos;s log on the server that ran it. File receipts for this run
          list each file&apos;s own outcome.
        </Muted>
      </details>
    </div>
  );
}
