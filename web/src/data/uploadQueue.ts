import { contentPayload, type ContentMode, type ContentPayload, type ContentReceipt } from './content';
export type UploadStatus = 'queued' | 'reading' | 'submitting' | 'admitted' | 'invalid' | 'unconfirmed';
export interface UploadItem {
  id: string; name: string; path: string; mode: ContentMode; bytes: number;
  status: UploadStatus; error?: string; receipt?: ContentReceipt;
  read: () => Promise<Uint8Array>; modifiedAt?: string;
}
export interface UploadSnapshot { items: readonly UploadItem[]; running: boolean; stopping: boolean }
export type SubmitContent = (mode: ContentMode, payload: ContentPayload) => Promise<ContentReceipt>;
let sequence = 0;
/** Explicit, selected-file HTTP admissions. No automatic retries or browser semantic engine. */
export class UploadQueue {
  private state: UploadSnapshot = { items: [], running: false, stopping: false };
  private listeners = new Set<() => void>();
  getSnapshot = () => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private publish(update: Partial<UploadSnapshot>) {
    this.state = { ...this.state, ...update }; for (const listener of this.listeners) listener();
  }
  private patch(id: string, update: Partial<UploadItem>) {
    this.publish({ items: this.state.items.map((item) => item.id === id ? { ...item, ...update } : item) });
  }
  add(input: Omit<UploadItem, 'id' | 'status'>) {
    this.publish({ items: [...this.state.items, { ...input, id: `selected-${++sequence}`, status: 'queued' }] });
  }
  edit(id: string, update: Pick<UploadItem, 'path' | 'mode'>) {
    const item = this.state.items.find((value) => value.id === id);
    if (item?.status !== 'queued' || this.state.running) return;
    this.patch(id, update);
  }
  remove(id: string) {
    if (this.state.items.some((item) => item.id === id && (item.status === 'reading' || item.status === 'submitting'))) return;
    this.publish({ items: this.state.items.filter((item) => item.id !== id) });
  }
  requeue(id: string) {
    const item = this.state.items.find((value) => value.id === id);
    if (this.state.running || !item || (item.status !== 'invalid' && item.status !== 'unconfirmed')) return;
    this.patch(id, { status: 'queued', error: undefined });
  }
  stop = () => { if (this.state.running) this.publish({ stopping: true }); };
  async start(submit: SubmitContent): Promise<void> {
    if (this.state.running) return;
    // Freeze the explicit selection for this run. Files added later are not silently admitted.
    const selected = this.state.items.filter((item) => item.status === 'queued');
    if (selected.length === 0) return;
    this.publish({ running: true, stopping: false });
    try {
      for (const candidate of selected) {
        if (this.state.stopping) break;
        const item = this.state.items.find((value) => value.id === candidate.id);
        if (!item || item.status !== 'queued') continue;
        this.patch(item.id, { status: 'reading', error: undefined });
        let payload: ContentPayload;
        try { payload = contentPayload(item.name, item.path, await item.read(), item.modifiedAt); }
        catch (failure) {
          this.patch(item.id, { status: 'invalid', error: failure instanceof Error ? failure.message : String(failure) }); continue;
        }
        if (this.state.stopping) { this.patch(item.id, { status: 'queued' }); break; }
        this.patch(item.id, { status: 'submitting' });
        try {
          const receipt = await submit(item.mode, payload);
          this.patch(item.id, { status: 'admitted', receipt });
        } catch (failure) {
          this.patch(item.id, { status: 'unconfirmed', error: `${failure instanceof Error ? failure.message : String(failure)}. No automatic retry was made. Inspect server state before resubmitting this artifact.` });
          this.publish({ stopping: true });
        }
      }
    } finally { this.publish({ running: false, stopping: false }); }
  }
}
