import { Button, FormRow, Input, Muted, Panel } from '@ui';
import shared from './playShared.module.css';

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
        <ul className={shared.stats}>
          <li>state: <b>{train.running ? 'running' : 'idle'}</b></li>
          <li>games: <b>{train.games}</b></li>
          <li>W / B / D: <b>{train.white} / {train.black} / {train.draws}</b></li>
          <li>adjudicated: {train.adjudicated}</li>
          <li>last: {train.lastOutcome || '—'}</li>
        </ul>
      ) : <Muted>no status</Muted>}
      <div className={shared.knobs}>
        <FormRow label="games" htmlFor="train-games">
          <Input id="train-games" type="number" min={0} value={knobs.games}
            onChange={(e) => onKnobsChange({ ...knobs, games: +e.target.value })} />
        </FormRow>
        <FormRow label="temp" htmlFor="train-temp">
          <Input id="train-temp" type="number" min={0} step={10} value={knobs.temp}
            onChange={(e) => onKnobsChange({ ...knobs, temp: +e.target.value })} />
        </FormRow>
        <FormRow label="max plies" htmlFor="train-max-plies">
          <Input id="train-max-plies" type="number" min={2} value={knobs.maxPlies}
            onChange={(e) => onKnobsChange({ ...knobs, maxPlies: +e.target.value })} />
        </FormRow>
        <FormRow label="weight" htmlFor="train-weight">
          <Input id="train-weight" type="number" min={0} step={0.1} value={knobs.weight}
            onChange={(e) => onKnobsChange({ ...knobs, weight: +e.target.value })} />
        </FormRow>
      </div>
      <div className={shared.ctlRow}>
        <Button onClick={onStart} disabled={train?.running}>{knobs.games > 0 ? `Run ${knobs.games}` : 'Run ∞'}</Button>
        <Button onClick={onStop} disabled={!train?.running}>Stop</Button>
      </div>
      <Muted>games 0 = run until Stop · temp = exploration · weight = per-game evidence · folds online.</Muted>
    </Panel>
  );
}
