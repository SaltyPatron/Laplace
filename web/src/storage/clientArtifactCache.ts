const DB_NAME = 'laplace-client-artifacts';
const DB_VERSION = 1;
const STORE = 'artifacts';

interface ArtifactRecord {
  key: string;
  kind: string;
  receipt: string;
  bytes: number;
  storedAt: number;
  blob: Blob;
}

function openDb(): Promise<IDBDatabase> {
  if (!('indexedDB' in globalThis)) return Promise.reject(new Error('IndexedDB unavailable'));
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB open failed'));
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(STORE)) {
        const store = db.createObjectStore(STORE, { keyPath: 'key' });
        store.createIndex('kind', 'kind', { unique: false });
      }
    };
    request.onsuccess = () => resolve(request.result);
  });
}

function key(kind: string, receipt: string): string {
  return `${kind}:${receipt.toLowerCase()}`;
}

export async function clientArtifactGet(kind: string, receipt: string): Promise<ArrayBuffer | null> {
  let db: IDBDatabase | null = null;
  try {
    db = await openDb();
    const record = await new Promise<ArtifactRecord | undefined>((resolve, reject) => {
      const tx = db!.transaction(STORE, 'readonly');
      const request = tx.objectStore(STORE).get(key(kind, receipt));
      request.onerror = () => reject(request.error ?? new Error('IndexedDB read failed'));
      request.onsuccess = () => resolve(request.result as ArtifactRecord | undefined);
    });
    if (!record) return null;
    return await record.blob.arrayBuffer();
  } finally {
    db?.close();
  }
}

export async function clientArtifactPut(
  kind: string,
  receipt: string,
  value: ArrayBuffer,
  contentType = 'application/octet-stream',
): Promise<void> {
  let db: IDBDatabase | null = null;
  try {
    db = await openDb();
    await new Promise<void>((resolve, reject) => {
      const tx = db!.transaction(STORE, 'readwrite');
      const store = tx.objectStore(STORE);
      const index = store.index('kind');
      const cursor = index.openCursor(IDBKeyRange.only(kind));
      cursor.onsuccess = () => {
        const row = cursor.result;
        if (!row) return;
        const old = row.value as ArtifactRecord;
        if (old.receipt.toLowerCase() !== receipt.toLowerCase()) row.delete();
        row.continue();
      };
      cursor.onerror = () => tx.abort();
      store.put({
        key: key(kind, receipt),
        kind,
        receipt,
        bytes: value.byteLength,
        storedAt: Date.now(),
        blob: new Blob([value], { type: contentType }),
      } satisfies ArtifactRecord);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error ?? new Error('IndexedDB write failed'));
      tx.onabort = () => reject(tx.error ?? new Error('IndexedDB write aborted'));
    });

    // Best-effort request: browsers may decline persistent storage without affecting correctness.
    if (navigator.storage?.persist) void navigator.storage.persist().catch(() => false);
  } finally {
    db?.close();
  }
}

export async function clientStorageEstimate(): Promise<{ usage: number; quota: number } | null> {
  if (!navigator.storage?.estimate) return null;
  try {
    const estimate = await navigator.storage.estimate();
    return {
      usage: estimate.usage ?? 0,
      quota: estimate.quota ?? 0,
    };
  } catch {
    return null;
  }
}


export interface ClientArtifactLoadResult {
  value: ArrayBuffer;
  source: 'cache' | 'network';
}

export async function clientArtifactGetOrLoad(
  kind: string,
  receipt: string,
  expectedBytes: number,
  loader: () => Promise<ArrayBuffer>,
): Promise<ClientArtifactLoadResult> {
  const readValid = async (): Promise<ArrayBuffer | null> => {
    const cached = await clientArtifactGet(kind, receipt);
    return cached && cached.byteLength === expectedBytes ? cached : null;
  };

  try {
    const hit = await readValid();
    if (hit) return { value: hit, source: 'cache' };
  } catch {
    // Storage is an accelerator; network correctness remains available.
  }

  const loadAndStore = async (): Promise<ClientArtifactLoadResult> => {
    try {
      const secondHit = await readValid();
      if (secondHit) return { value: secondHit, source: 'cache' };
    } catch { /* fall through */ }

    const value = await loader();
    if (value.byteLength !== expectedBytes) {
      throw new Error(`Artifact size mismatch for ${kind}: expected ${expectedBytes}, received ${value.byteLength}`);
    }
    try { await clientArtifactPut(kind, receipt, value); } catch { /* network result remains valid */ }
    return { value, source: 'network' };
  };

  const nav = navigator as Navigator & {
    locks?: { request<T>(name: string, callback: () => Promise<T>): Promise<T> };
  };
  if (nav.locks?.request) {
    return await nav.locks.request(`laplace:${kind}:${receipt.toLowerCase()}`, loadAndStore);
  }
  return await loadAndStore();
}
