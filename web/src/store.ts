import { create } from 'zustand';
import type { ProvenanceLine, PreflightQuoteResponse } from './api/client';

export interface ProvenanceEntry {
  reply: string;
  effMu?: number;
  witnesses?: number;
  ordUsed?: number;
}

export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  provenance: ProvenanceEntry[];
  streaming?: boolean;
  error?: string;
  performance?: ChatPerformance;
}

export interface ChatPerformance {
  substrateMs: number;
  elapsedMs: number;
  firstResultMs?: number;
  outputUtf8Bytes: number;
  outputCodepoints: number;
  outputWords: number;
  generatedTokens?: number;
  generatedTokensPerSecond?: number;
}

export interface QuoteGate {
  serviceId: string;
  quote?: PreflightQuoteResponse;
  message: string;
}

/** A structural read preset handed from the Home landing to the Query console. */
export interface QuerySeed {
  topic: string;
  topic2?: string;
  shape?: string;
  bands?: number[];
  relationType?: string;
}

export interface AuthProvider {
  id: string;
  displayName: string;
  loginUrl: string;
}

export interface AuthUser {
  id: string;
  tenantId: string;
  displayName?: string | null;
  email?: string | null;
  provider?: string | null;
}

interface AppState {
  tenant: string;
  quoteId: string;
  model: string;
  /** Conversation session key (spec 34): server-minted on the first turn, resent on
   *  every later turn so the substrate carries the conversation — history is never
   *  resent. In-memory only; a new conversation starts a new session. */
  session: string | null;
  messages: ChatMessage[];
  pendingQuote: QuoteGate | null;
  exploreSeedPrompt: string | null;
  querySeed: QuerySeed | null;
  authReady: boolean;
  authUser: AuthUser | null;
  authProviders: AuthProvider[];
  setTenant: (tenant: string) => void;
  setSession: (session: string | null) => void;
  setQuoteId: (quoteId: string) => void;
  setModel: (model: string) => void;
  pushMessage: (message: ChatMessage) => void;
  updateLastAssistant: (update: (m: ChatMessage) => ChatMessage) => void;
  setPendingQuote: (gate: QuoteGate | null) => void;
  setExploreSeedPrompt: (prompt: string | null) => void;
  setQuerySeed: (seed: QuerySeed | null) => void;
  setAuth: (user: AuthUser | null, providers: AuthProvider[]) => void;
  clearConversation: () => void;
}

const initialTenant = localStorage.getItem('laplace.tenant') ?? 'local-dev';
const sessionKey = (tenant: string) => `laplace.session.${tenant}`;

export const useAppStore = create<AppState>((set) => ({
  tenant: initialTenant,
  quoteId: '',
  model: 'laplace-converse-001',
  session: localStorage.getItem(sessionKey(initialTenant)),
  messages: [],
  pendingQuote: null,
  exploreSeedPrompt: null,
  querySeed: null,
  authReady: false,
  authUser: null,
  authProviders: [],
  setTenant: (tenant) => {
    localStorage.setItem('laplace.tenant', tenant);
    // A tenant switch is a different witnessed world — never carry a session across.
    set({ tenant, session: localStorage.getItem(sessionKey(tenant)), messages: [] });
  },
  setSession: (session) => set((state) => {
    if (session) localStorage.setItem(sessionKey(state.tenant), session);
    else localStorage.removeItem(sessionKey(state.tenant));
    return { session };
  }),
  setQuoteId: (quoteId) => set({ quoteId }),
  setModel: (model) => set({ model }),
  pushMessage: (message) => set((s) => ({ messages: [...s.messages, message] })),
  updateLastAssistant: (update) =>
    set((s) => {
      const messages = [...s.messages];
      for (let i = messages.length - 1; i >= 0; i--) {
        if (messages[i].role === 'assistant') {
          messages[i] = update(messages[i]);
          break;
        }
      }
      return { messages };
    }),
  setPendingQuote: (pendingQuote) => set({ pendingQuote }),
  setExploreSeedPrompt: (exploreSeedPrompt) => set({ exploreSeedPrompt }),
  setQuerySeed: (querySeed) => set({ querySeed }),
  setAuth: (authUser, authProviders) => set((state) => {
    if (!authUser) return { authReady: true, authUser: null, authProviders };
    localStorage.setItem('laplace.tenant', authUser.tenantId);
    return {
      authReady: true,
      authUser,
      authProviders,
      tenant: authUser.tenantId,
      session: localStorage.getItem(sessionKey(authUser.tenantId)),
      messages: state.tenant === authUser.tenantId ? state.messages : [],
    };
  }),
  clearConversation: () => set((state) => {
    localStorage.removeItem(sessionKey(state.tenant));
    return { messages: [], pendingQuote: null, session: null };
  }),
}));


export function asNum(value: string | number | null | undefined): number {
  return typeof value === 'number' ? value : Number(value ?? 0);
}

export function provenanceFromMetadata(lines: ProvenanceLine[] | undefined): ProvenanceEntry[] {
  return (lines ?? []).map((l) => ({
    reply: l.reply ?? '',
    effMu: asNum(l.eff_mu),
    witnesses: asNum(l.witnesses),
  }));
}
