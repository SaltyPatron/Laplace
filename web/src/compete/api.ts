/** Hosted events: the calls a host and a visitor with the link make. */
import { PaymentRequiredError, apiDelete, apiGet, apiPost, apiPut, type PreflightQuoteResponse } from '../api/client';
import type { Competition } from './model';

/** `paired` formats open with head-to-head pairs, so the host can choose how seeds are paired. */
export interface FormatInfo { id: string; title: string; summary: string; paired: boolean }
export interface PairingInfo { id: string; title: string; summary: string }
export interface Formats { free_entrants: number; max_entrants: number; host_service: string; session_hours: number; formats: FormatInfo[]; pairings: PairingInfo[] }
/** One occasion holding several events; one hosting fee covers them all while `covered_until` is ahead. */
export interface EventSession { id: string; title: string; created_at: string; covered_until: string | null }
export interface EventSummary { id: string; title: string; format: string; status: EventStatus; created_at: string; entrants: number; session: string | null }
export type EventStatus = 'registration' | 'running';
/** `frozen`: the workspace's plan ended, so its events are read-only. `delete_on`: when the event is deleted, or null while a plan keeps it. */
export interface Retention { frozen: boolean; delete_on: string | null }

/** An event as the server gives it: the competition, plus whether this viewer hosts it. */
export interface HostedEvent extends Competition {
  id: string;
  format: string;
  status: EventStatus;
  host: boolean;
  pairing: string;
  retention: Retention;
  session: { id: string; title: string; covered: boolean } | null;
}

export const readFormats = (signal?: AbortSignal) => apiGet<Formats>('/v1/competitions/formats', { signal });
export const readEvents = (signal?: AbortSignal) => apiGet<{ competitions: EventSummary[]; retention: Retention }>('/v1/competitions', { signal });

export async function readEvent(id: string, signal?: AbortSignal): Promise<HostedEvent> {
  const event = await apiGet<HostedEvent>(`/v1/competitions/${encodeURIComponent(id)}`, { signal });
  return { ...event, stages: event.stages ?? [], standings: event.standings ?? [] };
}

export const readSessions = (signal?: AbortSignal) => apiGet<{ sessions: EventSession[] }>('/v1/competitions/sessions', { signal });
export const createSession = (title: string) => apiPost<{ id: string; title: string }>('/v1/competitions/sessions', { title });
export const createEvent = (title: string, format: string, session?: string) =>
  apiPost<{ id: string }>('/v1/competitions', { title, format, session: session ?? null });
export const deleteEvent = (id: string) => apiDelete(`/v1/competitions/${encodeURIComponent(id)}`);
export const register = (id: string, name: string) =>
  apiPost<{ id: string; name: string }>(`/v1/competitions/${encodeURIComponent(id)}/entrants`, { name });
/** Host only: adds many entrants at once; names already registered are skipped. */
export const addEntrants = (id: string, names: string[]) =>
  apiPost<{ added: number; skipped: number }>(`/v1/competitions/${encodeURIComponent(id)}/entrants/batch`, { names });
/** Host only, before the start: reseed in a given order, draw at random, or change the format or the pairing. */
export const changeDraw = (id: string, change: { order?: string[]; shuffle?: boolean; format?: string; pairing?: string }) =>
  apiPut<void>(`/v1/competitions/${encodeURIComponent(id)}/draw`, change);
export const withdraw = (id: string, entrant: string) =>
  apiDelete(`/v1/competitions/${encodeURIComponent(id)}/entrants/${encodeURIComponent(entrant)}`);
export const recordResult = (id: string, match: string, slots: { score: string | null; place: number }[]) =>
  apiPut<void>(`/v1/competitions/${encodeURIComponent(id)}/matches/${encodeURIComponent(match)}/result`, { slots });

// The hosting quote a host paid for, kept until the event it was bought for has started.
const quoteKey = (id: string) => `laplace.event-quote.${id}`;
function storedQuote(id: string): string | null {
  try { return localStorage.getItem(quoteKey(id)); } catch { return null; }
}
function keepQuote(id: string, quote: string | null): void {
  try { if (quote) localStorage.setItem(quoteKey(id), quote); else localStorage.removeItem(quoteKey(id)); } catch { /* Storage is optional. */ }
}

/**
 * Starts the event. A free-size event, a workspace on a plan, or a paid quote starts at once.
 * Otherwise the host is sent to checkout for the hosting fee and starts the event on return.
 */
export async function startEvent(id: string, service: string): Promise<'started' | 'checkout'> {
  const quote = storedQuote(id);
  try {
    await apiPost(`/v1/competitions/${encodeURIComponent(id)}/start`, {}, quote ? { quoteId: quote } : {});
    keepQuote(id, null);
    return 'started';
  } catch (failure) {
    if (!(failure instanceof PaymentRequiredError)) throw failure;
    const fresh = await apiPost<PreflightQuoteResponse>('/v1/billing/preflight', { service_id: service, units: 1 });
    keepQuote(id, fresh.quote_id);
    if (!fresh.stripe_checkout_url) {
      throw new Error('This event is larger than the free size, and checkout is not set up on this host.');
    }
    window.location.assign(fresh.stripe_checkout_url);
    return 'checkout';
  }
}
