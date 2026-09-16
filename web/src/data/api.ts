import { apiGet, apiPost, type ApiOptions } from '../api/client';
import { contentReceipt, type ContentMode, type ContentPayload, type ContentReadback } from './content';
export async function admitContent(mode: ContentMode, payload: ContentPayload, options: ApiOptions) {
  return contentReceipt(await apiPost<unknown>(`/v1/content/${mode}`, payload, options));
}
export function readContent(id: string, options: ApiOptions) {
  return apiGet<ContentReadback>(`/v1/content/${encodeURIComponent(id)}`, options);
}
