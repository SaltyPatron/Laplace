import { Panel } from './Panel';

const MOTIF_LABEL: Record<string, string> = {
  fork: 'fork', discovered_check: 'discovered check', hanging_piece_won: 'won material',
};

export interface GameControlsProps {
  statusText: string;
  busy: boolean;
  over: boolean;
  status: string;
  motifs: string[];
  err: string | null;
  fen: string;
  autoReply: boolean;
  flip: boolean;
  searchDepth: number;
  useSubstrate: boolean;
  evalMode: boolean;
  onNewGame: () => void;
  onBotMove: () => void;
  onAutoReply: (v: boolean) => void;
  onFlip: (v: boolean) => void;
  onDepth: (v: number) => void;
  onSubstrate: (v: boolean) => void;
  onEvalMode: (v: boolean) => void;
}

/** Game status + actions, extracted from ChessView so the sidebar owns it as a
 *  first-class module rather than a block hard-wired above the board. */
export function GameControls(p: GameControlsProps) {
  const statusCls = `status${p.busy ? ' thinking' : ''}${p.over ? (p.status === 'draw' ? ' over draw' : ' over win') : ''}`;
  return (
    <Panel className="game-controls">
      <div className={statusCls} role="status">{p.statusText}</div>
      {p.motifs.length > 0 && (
        <div className="motifs">
          {p.motifs.map((m) => <span key={m} className="motif-chip">{MOTIF_LABEL[m] ?? m}</span>)}
        </div>
      )}
      {p.err && <div className="chess-error" role="alert">{p.err}</div>}
      <div className="ctl-row">
        <button onClick={p.onNewGame}>New game</button>
        <button onClick={p.onBotMove} disabled={p.busy || p.over}>Bot move</button>
        <label><input type="checkbox" checked={p.autoReply} onChange={(e) => p.onAutoReply(e.target.checked)} /> bot auto-replies</label>
        <label><input type="checkbox" checked={p.flip} onChange={(e) => p.onFlip(e.target.checked)} /> flip</label>
      </div>
      <div className="ctl-row">
        <label>depth<input type="number" min={1} max={12} value={p.searchDepth}
          onChange={(e) => { const n = parseInt(e.target.value, 10); if (!Number.isNaN(n)) p.onDepth(Math.min(12, Math.max(1, n))); }} /></label>
        <label><input type="checkbox" checked={p.useSubstrate} onChange={(e) => p.onSubstrate(e.target.checked)} /> substrate root bias</label>
        <label><input type="checkbox" checked={p.evalMode} onChange={(e) => p.onEvalMode(e.target.checked)} /> eval mode</label>
      </div>
      <span className="hint">drag/click to move · right-drag = arrow · right-click = mark · left-click clears</span>
      <code className="fen">{p.fen}</code>
    </Panel>
  );
}
