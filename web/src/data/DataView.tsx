import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Field, Input, Modal, Muted, Panel, ReadStatus, Select, TextArea, useReadResource } from '@ui';
import { ResultWorkspace, type ResultColumn } from '../ui/composites/ResultWorkspace/ResultWorkspace';
import { captureRows } from '../ui/lib/resultRows';
import { admitContent, readContent } from './api';
import { useAppStore } from '../store';
import { decodeContent, textBytes, type ContentMode } from './content';
import { type UploadItem } from './uploadQueue';
import { useUploadQueue } from './UploadProvider';
import styles from './DataView.module.css';

type VisibleItem = Omit<UploadItem, 'read' | 'receipt'> & { file_id?: string; content_id?: string; source?: string; modality?: string | null };
export function DataView() {
  const { tenant, authUser } = useAppStore();
  return <DataWorkspace key={JSON.stringify([tenant, authUser?.id])} tenant={tenant} />;
}
function DataWorkspace({ tenant }: { tenant: string }) {
  const { queue, scope } = useUploadQueue();
  const state = useSyncExternalStore(queue.subscribe, queue.getSnapshot, queue.getSnapshot);
  const [params, setParams] = useSearchParams();
  const [mode, setMode] = useState<ContentMode>('text');
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [retry, setRetry] = useState<VisibleItem | null>(null);
  const requested = params.get('id') ?? '';
  const [lookup, setLookup] = useState(requested);
  useEffect(() => { setLookup(requested); }, [requested]);
  useEffect(() => () => queue.stop(), [queue]);
  function openReceipt(id: string) {
    const next = new URLSearchParams(params); next.set('id', id); setParams(next);
  }
  const rows = useMemo(() => state.items.map(({ read: _read, receipt, ...item }): VisibleItem => ({
    ...item, file_id: receipt?.file_id, content_id: receipt?.content_id, source: receipt?.source, modality: receipt?.modality,
  })), [state.items]);
  const snapshot = useMemo(() => captureRows(rows, 'Explicit local artifact selection and HTTP admission receipts in this tab. This is not a seed manifest run or a complete source inventory.'), [rows]);
  const queued = state.items.filter((item) => item.status === 'queued').length;
  const admitted = state.items.filter((item) => item.status === 'admitted').length;
  const columns: ResultColumn<VisibleItem>[] = [
    { key: 'name', label: 'Artifact' },
    { key: 'path', label: 'Relative path', render: (item) => item.status === 'queued' ? <Input aria-label={`Relative path for ${item.name}`} value={item.path} disabled={state.running} onChange={(event) => queue.edit(item.id, { path: event.target.value, mode: item.mode })} /> : item.path },
    { key: 'mode', label: 'Admission', render: (item) => item.status === 'queued' ? <Select aria-label={`Admission mode for ${item.name}`} value={item.mode} disabled={state.running} onChange={(event) => queue.edit(item.id, { path: item.path, mode: event.target.value as ContentMode })}><option value="text">Text</option><option value="code">Code / structured</option></Select> : item.mode },
    { key: 'bytes', label: 'Selected bytes' },
    { key: 'status', label: 'Progress', render: (item) => <>{item.status}{item.error && <ErrorText>{item.error}</ErrorText>}</> },
    { key: 'file_id', label: 'Admitted data', render: (item) => item.file_id ? <><button type="button" onClick={() => openReceipt(item.file_id!)}>Read admitted bytes</button><Link className={styles.receipt} to={`/explore/entity/${item.content_id}`}>Explore content</Link><code className={styles.receipt}>{item.file_id}</code></> : 'No admission receipt' },
    { key: 'id', label: 'Selection controls', render: (item) => <div className={styles.toolbar}>
      {(item.status === 'invalid' || item.status === 'unconfirmed') && <Button variant="ghost" disabled={state.running} onClick={() => item.status === 'unconfirmed' ? setRetry(item) : queue.requeue(item.id)}>Requeue</Button>}
      <Button variant="ghost" disabled={state.running} onClick={() => queue.remove(item.id)}>Remove from selection</Button>
    </div> },
  ];
  return <div className={styles.page}>
    <header><h2>Data workspace</h2><p>Select files or author content, admit through the existing native text/code path, then open the returned records and bytes.</p>
      <Link to="/explore/warehouse">Browse sources</Link>{' · '}<Link to="/operator?section=ingest">Ingestion runs</Link>{' · '}<Link to="/operator?section=ops">Installed operations</Link></header>
    <div className={styles.inputs}>
      <Panel title="Select local files"><div className={styles.stack}>
        <Field label="Admission mode" help="Text accepts valid UTF-8 documents. Code / structured uses the server's registered grammar for each relative path extension; unsupported grammars are reported, not guessed.">
          <Select value={mode} onChange={(event) => setMode(event.target.value as ContentMode)}><option value="text">Text</option><option value="code">Code / structured source</option></Select>
        </Field>
        <Field label="Files" help="Selection does not upload anything. Original UTF-8 bytes, including line endings and a byte-order mark, are sent only when you start admission.">
          <Input type="file" multiple onChange={(event) => {
            for (const file of Array.from(event.target.files ?? [])) queue.add({ name: file.name, path: file.webkitRelativePath || file.name, mode,
              bytes: file.size, modifiedAt: new Date(file.lastModified).toISOString(), read: async () => new Uint8Array(await file.arrayBuffer()) });
            event.target.value = '';
          }} />
        </Field>
        <Muted>Files are read and sent one at a time. No hidden retry, text normalization, repository crawl or seed-manifest execution occurs.</Muted>
      </div></Panel>
      <Panel title="Author an artifact"><form className={styles.stack} onSubmit={(event) => {
        event.preventDefault();
        try {
          const bytes = textBytes(text);
          queue.add({ name, path: path || name, mode, bytes: bytes.length, read: async () => bytes }); setError(null);
        } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); }
      }}>
        <Field label="Artifact name"><Input required value={name} onChange={(event) => setName(event.target.value)} placeholder="notes.md" /></Field>
        <Field label="Relative artifact path" help="Optional; defaults to the artifact name. This is source metadata, not a server filesystem destination."><Input value={path} onChange={(event) => setPath(event.target.value)} placeholder="research/notes.md" /></Field>
        <Field label="Content" help="Whitespace and case are preserved. The selected admission mode also applies to authored content."><TextArea required rows={8} value={text} onChange={(event) => setText(event.target.value)} /></Field>
        <Button type="submit">Add text to selection</Button>{error && <ErrorText role="alert">{error}</ErrorText>}
      </form></Panel>
    </div>
    <Panel title="Selected artifacts" expandable>
      <p>Starting admission sends the selected bytes to this server under tenant <strong>{tenant}</strong>. Removing a selection does not retract or delete admitted data.</p>
      <div className={styles.toolbar}><Button disabled={queued === 0 || state.running} onClick={() => void queue.start((kind, payload) => {
          const current = useAppStore.getState();
          if (JSON.stringify([current.tenant, current.authUser?.id]) !== scope) throw new Error('The account or tenant changed before submission');
          return admitContent(kind, payload, { tenant });
        })}>Ingest {queued} queued artifact{queued === 1 ? '' : 's'}</Button>
        <Button variant="ghost" disabled={!state.running || state.stopping} onClick={queue.stop}>{state.stopping ? 'Stopping after current request' : 'Stop after current request'}</Button>
        <span role="status">{admitted} admitted · {queued} queued{state.running ? ' · admission active' : ''}</span></div>
      <Muted>Only the selection present when you press Ingest is submitted. Navigation stops after the current request and retains this selection in memory; reload loses unsubmitted local selections. An unconfirmed request stops the batch without retrying it.</Muted>
      <ResultWorkspace scopeKey={scope} label="Artifact selection" snapshot={snapshot} columns={columns} rowLabel={(item) => item.name} rowKey={(item) => item.id} />
    </Panel>
    <Panel title="Open admitted content"><form className={styles.toolbar} onSubmit={(event) => { event.preventDefault(); openReceipt(lookup); }}>
      <Field label="File, document or content ID" help="Use a returned ID. Readback uses the current tenant's content endpoint."><Input required pattern="[0-9a-fA-F]{32}" value={lookup} onChange={(event) => setLookup(event.target.value)} /></Field><Button type="submit">Read content</Button>
    </form></Panel>
    {requested && <ContentInspector key={JSON.stringify([scope, requested])} id={requested} tenant={tenant} />}
    <Modal open={retry != null} onClose={() => setRetry(null)} title="Requeue an unconfirmed admission?" actions={<><Button variant="ghost" onClick={() => setRetry(null)}>Go back</Button><Button onClick={() => { if (retry) queue.requeue(retry.id); setRetry(null); }}>Requeue without starting</Button></>}>
      <p>{retry?.name}: the previous request did not return an admission receipt. It may already have changed server state. Inspect that state before choosing to submit the same artifact again.</p>
    </Modal>
  </div>;
}
function ContentInspector({ id, tenant }: { id: string; tenant: string }) {
  const [error, setError] = useState<string | null>(null);
  const valid = /^[0-9a-f]{32}$/i.test(id);
  const readback = useReadResource({
    key: JSON.stringify(['user-content', tenant, id]), enabled: valid,
    read: (signal) => readContent(id, { tenant, signal }),
  });
  const content = readback.data;
  function download() {
    if (!content) return;
    try {
      const bytes = decodeContent(content.content_base64);
      const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' }));
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = (content.name || `${id}.bin`).split(/[\\/]/).pop() || `${id}.bin`;
      document.body.appendChild(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); setError(null);
    } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); }
  }
  return <Panel title="Admitted content readback" expandable actions={<Button variant="ghost" disabled={!valid} onClick={() => void readback.refresh()}>Refresh content</Button>}>
    {!valid ? <ErrorText>This address is not a 32-character hexadecimal ID.</ErrorText> : <ReadStatus label="Admitted content" resource={readback} />}
    {content && <div className={styles.stack}>
      <h3>{content.name || content.path || 'Content artifact'}</h3><Muted>{content.bytes ?? 'Unreported'} bytes · {content.modality ?? 'text'} · {content.source}</Muted>
      <div className={styles.toolbar}><Button onClick={download}>Download returned bytes</Button><Link to={`/explore/entity/${content.content_id}`}>Explore content structure</Link><Link to={`/explore/entity/${content.source_id}`}>Inspect source</Link></div>
      {content.text !== null ? <pre className={styles.preview}>{content.text.slice(0, 131072).replace(/[\uD800-\uDBFF]$/, '')}</pre> : <Muted>No text rendering was returned. The byte download uses content_base64, not a reconstructed display string.</Muted>}
      {content.text && content.text.length > 131072 && <Muted>Long text preview truncated. The byte download contains the complete returned content_base64.</Muted>}
      <details><summary>Identifiers and source context</summary><pre className={styles.preview}>{JSON.stringify({ requested_id: content.requested_id, kind: content.kind, file_id: content.file_id, document_id: content.document_id, content_id: content.content_id, metadata_id: content.metadata_id, source_id: content.source_id, path: content.path, modified_at: content.modified_at, contexts: content.contexts }, null, 2)}</pre></details>
    </div>}{error && <ErrorText role="alert">{error}</ErrorText>}
  </Panel>;
}
