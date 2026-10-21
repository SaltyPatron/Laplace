interface Context { path?: string; requestId?: string; status?: number; stack?: string }
const recent = new Map<string, number>();
const REPORT_PATH = '/diagnostics/client';

function pathOnly(value: string): string {
  try { return new URL(value, window.location.origin).pathname.slice(0, 512); }
  catch { return '(invalid path)'; }
}

/** Browser failures reach both the console and the same durable host logs as API errors. */
export function reportClientFailure(operation: string, error: unknown, context: Context = {}): void {
  if (error instanceof DOMException && error.name === 'AbortError') return;
  const failure = {
    kind: 'client', operation: operation.slice(0, 128),
    path: pathOnly(context.path ?? window.location.pathname),
    requestId: context.requestId, status: context.status,
    errorType: error instanceof Error ? error.name : typeof error,
    message: (error instanceof Error ? error.message : String(error)).slice(0, 1024),
    stack: (context.stack ?? (error instanceof Error ? error.stack : undefined))?.slice(0, 4096),
  };
  console.error('[laplace failure]', failure);
  // Bound duplicates during a broken render/retry loop. HTTP requests themselves
  // still each have a server completion event and their own correlation ID.
  const now = Date.now();
  for (const [key, time] of recent) if (now - time > 30_000) recent.delete(key);
  const key = JSON.stringify([failure.operation, failure.path, failure.status, failure.message]);
  if (recent.has(key) || recent.size >= 100) return;
  recent.set(key, now);
  void fetch(REPORT_PATH, {
    method: 'POST', credentials: 'same-origin', keepalive: true,
    headers: { 'Content-Type': 'application/json', 'X-Laplace-Request': '1' },
    body: JSON.stringify(failure),
  }).then((response) => {
    if (!response.ok) console.warn('[laplace diagnostics] Host rejected failure report.', response.status);
  }, (reason: unknown) => {
    // This transport deliberately does not call itself after a reporting failure.
    console.warn('[laplace diagnostics] Failure report could not reach the host.', reason);
  });
}

export function installClientDiagnostics(): void {
  window.addEventListener('error', (event: Event) => {
    if (event instanceof ErrorEvent) reportClientFailure('javascript', event.error ?? new Error(event.message));
    else if (event.target instanceof HTMLScriptElement || event.target instanceof HTMLLinkElement) {
      const resource = event.target instanceof HTMLScriptElement ? event.target.src : event.target.href;
      reportClientFailure('resource.load', new Error('Browser resource failed to load.'), { path: resource });
    }
  }, true);
  window.addEventListener('unhandledrejection', (event) => reportClientFailure('promise.unhandled', event.reason));
}
