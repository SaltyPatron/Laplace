import { apiGet, type ApiOptions } from '../api/client';
import type { OperationParameter } from '../ui/lib/operationFields';

export interface OperationDescription {
  name: string;
  args: string;
  returns: string | null;
  kind: string;
  parameters: OperationParameter[];
  writable: boolean;
  destructive: boolean;
}
export interface OperationCatalog { object: string; operations: OperationDescription[]; truncated_at: number | null }
export function readOperationCatalog(like: string, opts: ApiOptions = {}) {
  const query = new URLSearchParams({ max_rows: '2000' });
  if (like.length > 0) query.set('like', like);
  return apiGet<OperationCatalog>(`/v1/ops/catalog?${query}`, opts);
}
