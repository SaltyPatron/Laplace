/** A view-local read lifecycle. This is not a cache or an authority boundary. */
export type ReadStatus = 'idle' | 'loading' | 'refreshing' | 'ready' | 'failed' | 'cancelled';
export type ReadLoader<T> = (signal: AbortSignal) => Promise<T>;
export interface ReadSnapshot<T> {
  readonly status: ReadStatus;
  readonly data: T | undefined;
  readonly error: Error | null;
  readonly updatedAt: number | null;
}

/**
 * One request at a time, with explicit cancellation and late-response fencing.
 * A new scope gets a new resource; old-scope bodies are never shown as new data.
 * Refresh failures retain the last successful same-scope body and its timestamp.
 */
export class ReadResource<T> {
  private snapshot: ReadSnapshot<T> = { status: 'idle', data: undefined, error: null, updatedAt: null };
  private readonly listeners = new Set<() => void>();
  private generation = 0;
  private controller: AbortController | null = null;
  private pending: Promise<void> | null = null;

  readonly getSnapshot = (): ReadSnapshot<T> => this.snapshot;
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  private publish(snapshot: ReadSnapshot<T>): void {
    this.snapshot = snapshot;
    for (const listener of this.listeners) listener();
  }

  /** Automatic refresh coalesces with work already in flight. */
  readonly load = (loader: ReadLoader<T>): Promise<void> => {
    if (this.pending) return this.pending;
    const generation = ++this.generation;
    const controller = new AbortController();
    this.controller = controller;
    const previous = this.snapshot;
    const pending = Promise.resolve().then(async () => {
      controller.signal.throwIfAborted();
      return loader(controller.signal);
    }).then((data) => {
      if (generation !== this.generation) return;
      this.publish({ status: 'ready', data, error: null, updatedAt: Date.now() });
    }).catch((error: unknown) => {
      if (generation !== this.generation) return;
      this.publish({
        ...this.snapshot,
        status: controller.signal.aborted ? 'cancelled' : 'failed',
        error: controller.signal.aborted ? null : error instanceof Error ? error : new Error(String(error)),
      });
    }).finally(() => {
      if (generation !== this.generation) return;
      this.controller = null;
      this.pending = null;
    });
    this.pending = pending;
    this.publish({ ...previous, status: previous.updatedAt === null ? 'loading' : 'refreshing', error: null });
    return pending;
  };

  /** Cancellation means stop waiting; it does not certify cancellation of a server job. */
  readonly cancel = (): void => {
    if (!this.pending && !this.controller) return;
    ++this.generation;
    this.controller?.abort();
    this.controller = null;
    this.pending = null;
    this.publish({ ...this.snapshot, status: 'cancelled', error: null });
  };

  /** Explicit new intent supersedes the old read, even if its provider ignores abort. */
  readonly reload = (loader: ReadLoader<T>): Promise<void> => {
    this.cancel();
    return this.load(loader);
  };
}
