/** Play-view wire types for the /chess API and small shared helpers (square math, error text). */
import type { MoveScore } from './Board';

export interface PlayStart { sessionId: string; fen: string; status?: string; ply?: number; }
export interface RetainedPlay {
  sessionId: string; state: string; recordToSubstrate: boolean; moves: string[]; positionFens: string[];
  result?: string; canRetryRecording: boolean;
}

export interface PlayMoveResult { fen: string; terminal: boolean; status: string; legal: boolean; ply: number; motifs?: string[]; }

export interface LegalResponse { moves: MoveScore[]; inCheck: boolean; status: string; }

export interface BestMove {
  uci: string | null; fen: string; effMu: number; rated: boolean; terminal: boolean; status: string;
  scoreCp: number; depth: number; nodes: number; pv?: string[]; motifs?: string[];
}

export interface PositionEval { whiteScoreCp: number; depth: number; nodes: number; substrate?: boolean; terminal?: boolean; status?: string; }

export interface SearchInfo { scoreCp: number; depth: number; nodes: number; substrate: boolean; pv: string[]; }

const FILES = 'abcdefgh';

export const sqRC = (sq: string) => ({ f: FILES.indexOf(sq[0]), r: 8 - Number(sq[1]) });

export const msg = (e: unknown) => (e instanceof Error ? e.message : String(e));
