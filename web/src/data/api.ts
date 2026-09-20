import { apiGet, apiPost, apiPostBody, type ApiOptions } from '../api/client';
import { contentReceipt, type ContentMode, type ContentPayload, type ContentReadback } from './content';
export async function admitContent(mode: ContentMode, payload: ContentPayload, options: ApiOptions) {
  return contentReceipt(await apiPost<unknown>(`/v1/content/${mode}`, payload, options));
}
export function readContent(id: string, options: ApiOptions) {
  return apiGet<ContentReadback>(`/v1/content/${encodeURIComponent(id)}`, options);
}

export async function admitContentRaw(
  mode: ContentMode,
  meta: { name: string; path: string; modified_at?: string },
  body: BodyInit,
  options: ApiOptions,
) {
  const query = new URLSearchParams({ name: meta.name, path: meta.path });
  if (meta.modified_at) query.set('modified_at', meta.modified_at);
  return contentReceipt(await apiPostBody<unknown>(
    `/v1/content/${mode}/raw?${query.toString()}`,
    body,
    'application/octet-stream',
    options,
  ));
}
