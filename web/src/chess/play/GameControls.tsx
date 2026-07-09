import { Button, Checkbox, cn, ErrorText, Field, Muted, Panel, SliderField } from '@ui';
import shared from './playShared.module.css';
import styles from './GameControls.module.css';

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
  recordToSubstrate: boolean;
  evalMode: boolean;
  onNewGame: () => void;
  onBotMove: () => void;
  onAutoReply: (v: boolean) => void;
  onFlip: (v: boolean) => void;
  onDepth: (v: number) => void;
  onSubstrate: (v: boolean) => void;
  onRecordToSubstrate: (v: boolean) => void;
  onEvalMode: (v: boolean) => void;
}

export function GameControls(p: GameControlsProps) {
  return (
    <Panel className={styles.panel}>
      <div className={styles.statusBlock}>
        <div
          className={cn(
            styles.status,
            p.busy && styles.thinking,
            p.over && styles.over,
            p.over && (p.status === 'draw' ? styles.overDraw : styles.overWin),
          )}
          role="status"
        >
          {p.statusText}
        </div>
        <div className={styles.feedbackSlot} aria-live="polite">
          {p.err ? (
            <ErrorText role="alert">{p.err}</ErrorText>
          ) : (
            <div className={styles.motifs} aria-hidden={p.motifs.length === 0}>
              {p.motifs.map((m) => (
                <span key={m} className={styles.motifChip}>{MOTIF_LABEL[m] ?? m}</span>
              ))}
            </div>
          )}
        </div>
      </div>
      <div className={shared.ctlRow}>
        <Button onClick={p.onNewGame}>New game</Button>
        <Button onClick={p.onBotMove} disabled={p.busy || p.over}>Bot move</Button>
        <Checkbox
          id="auto-reply"
          checked={p.autoReply}
          onChange={(e) => p.onAutoReply(e.target.checked)}
          label="bot auto-replies"
        />
        <Checkbox
          id="flip-board"
          checked={p.flip}
          onChange={(e) => p.onFlip(e.target.checked)}
          label="flip"
        />
      </div>
      <div className={shared.ctlRow}>
        <Field label="Search depth" layout="row" valueDisplay={String(p.searchDepth)}>
          <SliderField
            min={1}
            max={12}
            value={String(p.searchDepth)}
            onChange={(v) => {
              const n = parseInt(v, 10);
              if (!Number.isNaN(n)) p.onDepth(Math.min(12, Math.max(1, n)));
            }}
            disabled={p.busy}
            label="Search depth"
          />
        </Field>
        <Checkbox
          id="substrate-bias"
          checked={p.useSubstrate}
          onChange={(e) => p.onSubstrate(e.target.checked)}
          label="substrate root bias"
        />
        <Checkbox
          id="record-substrate"
          checked={p.recordToSubstrate}
          onChange={(e) => p.onRecordToSubstrate(e.target.checked)}
          label="record to substrate"
        />
        <Checkbox
          id="eval-mode"
          checked={p.evalMode}
          onChange={(e) => p.onEvalMode(e.target.checked)}
          label="eval mode"
        />
      </div>
      <Muted>drag/click to move · ← → review · right-drag = arrow · right-click = mark</Muted>
      <code className={styles.fen}>{p.fen}</code>
    </Panel>
  );
}
