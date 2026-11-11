/** The gauntlet's "Match setup" parameter form (workers, conductor, clock, Stockfish options, ingest). */
import type { Dispatch, SetStateAction } from 'react';
import {
  Field,
  Input,
  SegmentedControl,
  Select,
  SliderField,
  Toggle,
} from '@ui';
import type { LabCatalog } from '../sse';
import type { Setup } from './gauntletSetup';
import styles from './GauntletView.module.css';

export function GauntletSetupForm({
  setup,
  setSetup,
  connections,
}: {
  setup: Setup;
  setSetup: Dispatch<SetStateAction<Setup>>;
  connections: NonNullable<LabCatalog['connections']>;
}) {
  return (
          <div className={styles.form}>
            {(['laplaceProfile', 'stockfishProfile'] as const).map((key) => (
              <Field key={key} label={key === 'laplaceProfile' ? 'Laplace worker' : 'Stockfish worker'} className={styles.wide}
                help="Engine execution is separate from this match's recording service. A selected remote connection stays on that worker for the game.">
                <Select aria-label={key === 'laplaceProfile' ? 'Laplace worker' : 'Stockfish worker'}
                  value={setup[key]} onChange={(e) => setSetup(s => ({ ...s, [key]: e.target.value,
                    ...(key === 'stockfishProfile' ? { stockfishSyzygyPath: '' } : {}) }))}>
                  <option value="">This installation</option>
                  {connections.filter(p => p.implementation === (key === 'laplaceProfile' ? 'laplace-mono' : 'stockfish'))
                    .map(p => <option key={p.id} value={p.id}>{p.label} · {p.environment} · {p.knowledge}</option>)}
                </Select>
              </Field>
            ))}
            <Field label="Match conductor" help="Fastchess runs engine matches. Cutechess provides an alternative conductor. This selection does not change the Lichess bot or the selected database source." className={styles.wide}>
              <SegmentedControl value={setup.conductor}
                onValueChange={(v) => setSetup((s) => ({ ...s, conductor: v as Setup['conductor'] }))}
                options={['fastchess', 'cutechess']} label="Match conductor" />
            </Field>
            <Field
              label="Clock"
              help="Per-move seconds is watchable. Fixed depth is a matched limit applied to both engines, not unrestricted Stockfish search."
              className={styles.wide}
            >
              <SegmentedControl
                value={setup.clock}
                onValueChange={(v) => setSetup((s) => ({ ...s, clock: v as Setup['clock'] }))}
                options={['seconds', 'depth']}
                label="Clock"
              />
            </Field>

            {setup.clock === 'seconds' ? (
              <Field label="Seconds per move" valueDisplay={`${setup.st}s`} help="The same per-move time budget and margin are applied to both engines.">
                <SliderField
                  min={0.1}
                  max={10}
                  step={0.1}
                  value={setup.st}
                  label="Seconds per move"
                  onChange={(v) => setSetup((s) => ({ ...s, st: v }))}
                />
              </Field>
            ) : (
              <Field label="Search depth" valueDisplay={setup.depth} help="Sent to both engines as go depth N.">
                <SliderField
                  min={1}
                  max={20}
                  value={setup.depth}
                  label="Search depth"
                  onChange={(v) => setSetup((s) => ({ ...s, depth: v }))}
                />
              </Field>
            )}

            <Field label="Games" help="Each opening is played twice with colors swapped. Use an even game count.">
              <Input
                type="number"
                min={2}
                step={2}
                value={setup.rounds}
                aria-label="Games"
                onChange={(e) => setSetup((s) => ({ ...s, rounds: e.target.value }))}
              />
            </Field>

            <Field
              label="Limit Stockfish by UCI Elo"
              help="Checked enables UCI_LimitStrength. Unchecked removes only the Elo limiter; the selected shared depth/time budget still limits Stockfish search."
            >
              <Toggle
                checked={setup.limitStrength}
                onCheckedChange={(value) => setSetup((s) => ({ ...s, limitStrength: value }))}
                aria-label="Limit Stockfish by UCI Elo"
              />
            </Field>
            <Field label="Stockfish Elo cap" help="2000 is a default, not a fixed level. The installed engine reports its supported range in the transcript.">
              <Input
                type="number"
                min={100}
                step={50}
                value={setup.elo}
                disabled={!setup.limitStrength}
                aria-label="Stockfish Elo cap"
                onChange={(e) => setSetup((s) => ({ ...s, elo: e.target.value }))}
              />
            </Field>

            <Field label="Games in flight" help="Parallel games share this host. Choose concurrency together with engine threads and memory from a benchmark run in the same environment.">
              <Input
                type="number"
                min={1}
                value={setup.concurrency}
                aria-label="Games in flight"
                onChange={(e) => setSetup((s) => ({ ...s, concurrency: e.target.value }))}
              />
            </Field>

            <Field label="Stockfish threads" help="Search threads per Stockfish process. Blank uses the installed engine default; the run records the actual options.">
              <Input type="number" min={1} step={1} value={setup.stockfishThreads}
                placeholder="Engine default" aria-label="Stockfish threads"
                onChange={(e) => setSetup((s) => ({ ...s, stockfishThreads: e.target.value }))} />
            </Field>
            <Field label="Stockfish hash (MiB)" help="Transposition-table memory per Stockfish process. Allow additional memory for its network, Laplace, and other services.">
              <Input type="number" min={1} step={1} value={setup.stockfishHashMb}
                placeholder="Engine default" aria-label="Stockfish hash (MiB)"
                onChange={(e) => setSetup((s) => ({ ...s, stockfishHashMb: e.target.value }))} />
            </Field>
            <Field label="Stockfish NUMA policy" help="Optional: auto, system, hardware, or none. Use the topology observed by the benchmark on this host.">
              <Input value={setup.stockfishNumaPolicy} placeholder="Engine default"
                aria-label="Stockfish NUMA policy"
                onChange={(e) => setSetup((s) => ({ ...s, stockfishNumaPolicy: e.target.value }))} />
            </Field>
            <Field label="Stockfish tablebase paths" help="Optional Syzygy directories on the server. Use colons on Linux or semicolons on Windows; include smaller-piece tables.">
              <Input value={setup.stockfishSyzygyPath} placeholder={setup.stockfishProfile ? 'Configured on the worker' : 'Engine default'} disabled={!!setup.stockfishProfile}
                aria-label="Stockfish tablebase paths"
                onChange={(e) => setSetup((s) => ({ ...s, stockfishSyzygyPath: e.target.value }))} />
            </Field>

            <Field
              label="Ingest to substrate"
              help="laplace-uci cannot record its own plies, so the PGN is re-ingested when the run completes."
              layout="row"
              className={styles.wide}
            >
              <Toggle
                checked={setup.ingest}
                onCheckedChange={(v) => setSetup((s) => ({ ...s, ingest: v }))}
                aria-label="Ingest to substrate"
              />
            </Field>
          </div>
  );
}
