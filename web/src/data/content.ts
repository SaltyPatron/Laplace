export type ContentMode = 'text' | 'code';
export interface ContentPayload {
  name: string;
  path: string;
  content_base64: string;
}
/**
 * An upload as the substrate holds it: the file [metadata, content], the witness trunk [participant, file] that
 * holds it (source_id), the participant (source), the relative path it is named by, and its bytes.
 */
export interface ContentReceipt {
  file_id: string; content_id: string; metadata_id: string;
  source_id: string; source: string; path: string; bytes: number;
}
/** A participant's file read back: its parts, the witness trunk that holds it, and its content. */
export interface ContentReadback {
  file_id: string; content_id: string; metadata_id: string; source_id: string; source: string;
  content_base64: string; text: string | null; bytes: number;
}
export function encodeContent(bytes: Uint8Array): string {
  // Chunks are a multiple of three: base64 padding occurs only in the last chunk.
  const encoded: string[] = [];
  for (let offset = 0; offset < bytes.length; offset += 24576)
    encoded.push(btoa(String.fromCharCode(...bytes.subarray(offset, offset + 24576))));
  return encoded.join('');
}
export function decodeContent(base64: string): Uint8Array<ArrayBuffer> {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes;
}
export function contentPayload(name: string, path: string, bytes: Uint8Array): ContentPayload {
  if (!name.trim()) throw new Error('Give the artifact a name.');
  const normalized = path.replace(/\\/g, '/');
  if (!normalized || normalized.startsWith('/') || /^[a-z]:/i.test(normalized) || normalized.split('/').some((part) => part === '..' || part === ''))
    throw new Error('Use a relative artifact path with no empty or parent-directory segments.');
  if (bytes.length === 0) throw new Error('This content endpoint does not accept an empty artifact.');
  new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
  return { name, path, content_base64: encodeContent(bytes) };
}
export function textBytes(text: string): Uint8Array<ArrayBuffer> {
  const bytes = new TextEncoder().encode(text);
  if (new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes) !== text)
    throw new Error('The text contains an unpaired Unicode surrogate. Correct it before admission.');
  return bytes;
}
export function contentReceipt(value: unknown): ContentReceipt {
  if (!value || typeof value !== 'object') throw new Error('The server returned no admission receipt.');
  const row = value as Record<string, unknown>;
  for (const field of ['file_id', 'content_id', 'metadata_id', 'source_id'])
    if (typeof row[field] !== 'string' || !/^[0-9a-f]{32}$/i.test(row[field] as string))
      throw new Error(`The admission response did not contain a valid ${field}.`);
  if (typeof row.source !== 'string' || typeof row.path !== 'string' || typeof row.bytes !== 'number' || !Number.isSafeInteger(row.bytes) || row.bytes < 0)
    throw new Error('The server returned an incomplete admission receipt.');
  return value as ContentReceipt;
}
