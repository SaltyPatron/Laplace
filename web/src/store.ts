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
}

export interface QuoteGate {
  serviceId: string;
  quote?: PreflightQuoteResponse;
  message: string;
}

interface AppState {
  tenant: string;
  quoteId: string;
  model: string;
  messages: ChatMessage[];
  pendingQuote: QuoteGate | null;
  setTenant: (tenant: string) => void;
  setQuoteId: (quoteId: string) => void;
  setModel: (model: string) => void;
  pushMessage: (message: ChatMessage) => void;
  updateLastAssistant: (update: (m: ChatMessage) => ChatMessage) => void;
  setPendingQuote: (gate: QuoteGate | null) => void;
  clearConversation: () => void;
}

export const useAppStore = create<AppState>((set) => ({
  tenant: localStorage.getItem('laplace.tenant') ?? 'local-dev',
  quoteId: '',
  model: 'laplace-converse-001',
  messages: [],
  pendingQuote: null,
  setTenant: (tenant) => {
    localStorage.setItem('laplace.tenant', tenant);
    set({ tenant });
  },
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
  clearConversation: () => set({ messages: [], pendingQuote: null }),
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
