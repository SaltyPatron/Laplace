import { Panel } from './Panel';

export interface TrainStatus {
  running: boolean; games: number; white: number; black: number; draws: number;
  adjudicated: number; lastOutcome: string; temperature: number; weight: number; maxPlies: number;
}

export interface TrainKnobs {
  games: number;
  temp: number;
  maxPlies: number;
  weight: number;
}

export interface EnginePanelProps {
  train: TrainStatus | null;
  knobs: TrainKnobs;
  onKnobsChange: (knobs: TrainKnobs) => void;
  onStart: () => void;
  onStop: () => void;
}

export function EnginePanel({ train, knobs, onKnobsChange, onStart, onStop }: EnginePanelProps) {
  return (
    <Panel title="Training">
      {train ? (
        <ul className="stats">
          <li>state: <b>{train.running ? 'running' : 'idle'}</b></li>
          <li>games: <b>{train.games}</b></li>
          <li>W / B / D: <b>{train.white} / {train.black} / {train.draws}</b></li>
          <li>adjudicated: {train.adjudicated}</li>
          <li>last: {train.lastOutcome || '—'}</li>
        </ul>
      ) : <div className="muted">no status</div>}
      <div className="knobs">
        <label>games<input type="number" min={0} value={knobs.games}
          onChange={(e) => onKnobsChange({ ...knobs, games: +e.target.value })} /></label>
        <label>temp<input type="number" min={0} step={10} value={knobs.temp}
          onChange={(e) => onKnobsChange({ ...knobs, temp: +e.target.value })} /></label>
        <label>max plies<input type="number" min={2} value={knobs.maxPlies}
          onChange={(e) => onKnobsChange({ ...knobs, maxPlies: +e.target.value })} /></label>
        <label>weight<input type="number" min={0} step={0.1} value={knobs.weight}
          onChange={(e) => onKnobsChange({ ...knobs, weight: +e.target.value })} /></label>
      </div>
      <div className="row">
        <button onClick={onStart} disabled={train?.running}>{knobs.games > 0 ? `Run ${knobs.games}` : 'Run ∞'}</button>
        <button onClick={onStop} disabled={!train?.running}>Stop</button>
      </div>
      <p className="muted">games 0 = run until Stop · temp = exploration · weight = per-game evidence · folds online.</p>
    </Panel>
  );
}
