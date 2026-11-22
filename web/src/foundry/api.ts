import { apiPost } from '../api/client';
import type { components } from '../api/types.gen';

export type RecipeCompileRequest = components['schemas']['RecipeCompileRequest'];
export type RecipeCompileResponse = components['schemas']['RecipeCompileResponse'];
export type RecipeQuoteRequest = components['schemas']['RecipeQuoteRequest'];
export type RecipeQuoteResponse = components['schemas']['RecipeQuoteResponse'];
export type SynthesisExportRequest = components['schemas']['SynthesisExportRequest'];
export type SynthesisQuoteRequest = components['schemas']['SynthesisQuoteRequest'];
export type SynthesisQuoteResponse = components['schemas']['SynthesisQuoteResponse'];

export type SynthesisExportResponse = components['schemas']['SynthesisExportResponse'] & {
  n_vocab?: number | string | null;
  n_edges?: number | string | null;
  hidden_dim?: number | string | null;
  raw_weight_bytes_retained?: number | string | null;
  model_name?: string | null;
};

export function compileRecipe(body: RecipeCompileRequest, opts: { tenant: string; quoteId?: string }) {
  return apiPost<RecipeCompileResponse>('/v1/recipe/compile', body, opts);
}

export function quoteRecipe(body: RecipeQuoteRequest, opts: { tenant: string }) {
  return apiPost<RecipeQuoteResponse>('/v1/billing/recipe/quote', body, opts);
}

export function exportSynthesis(body: SynthesisExportRequest, opts: { tenant: string; quoteId?: string }) {
  return apiPost<SynthesisExportResponse>('/v1/synthesis/export', body, opts);
}

export function quoteSynthesis(body: SynthesisQuoteRequest, opts: { tenant: string }) {
  return apiPost<SynthesisQuoteResponse>('/v1/billing/synthesis/quote', body, opts);
}
