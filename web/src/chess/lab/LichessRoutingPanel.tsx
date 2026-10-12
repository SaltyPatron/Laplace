import { useCallback, useEffect, useState } from 'react';
import { apiGet, apiPost } from '../../api/client';
import { Alert, Button, Field, Muted, Panel, Select, useVisiblePolling } from '@ui';
import styles from './LichessPanel.module.css';

interface Profile {
  id: string; label: string; environment: string; implementation: string;
  knowledge: string; maxConcurrent: number; rated: boolean;
}
interface KnowledgeTarget { id: string; label: string; identity: { cluster: string; database: string } }
interface Executor extends Omit<Profile, 'knowledge'> { sources: { id: string; recordingTargets: string[] }[] }
interface Selection { profile: string | null; revision: number; changedUtc: string; readSource?: string; recordingTarget?: string }
interface Control {
  controller: string; phase: string; activeProfile: string | null; error?: string;
  selection: Selection;
  activeSelection?: Selection;
  routing?: { executors: Executor[]; sources: KnowledgeTarget[]; recordingTargets: KnowledgeTarget[] };
  profiles: Profile[];
  bot?: { connected: boolean; username?: string; gamesRecorded: number; error?: string; recentLog: string[] };
}
interface View { available: boolean; canControl: boolean; error?: string; control?: Control }

export function LichessRoutingPanel() {
  const [view, setView] = useState<View | null>(null);
  const [chosen, setChosen] = useState<{ id: string; revision: number; readSource?: string; recordingTarget?: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [connectionError, setConnectionError] = useState<string | null>(null);
  const refresh = useCallback(async () => {
    try { setView(await apiGet<View>('/chess/lichess/control')); setConnectionError(null); }
    catch (e) { setConnectionError(e instanceof Error ? e.message : String(e)); }
  }, []);
  useEffect(() => { void refresh(); }, [refresh]);
  useVisiblePolling(refresh, { intervalMs: 3000, immediate: false });
  const control = view?.control;
  const selected = chosen?.id ?? control?.selection.profile ?? '';
  const routing = control?.routing;
  const profiles = routing?.executors ?? control?.profiles ?? [];
  const profile = profiles.find(p => p.id === selected);
  const readSource = chosen?.readSource ?? control?.selection.readSource ?? '';
  const recordingTarget = chosen?.recordingTarget ?? control?.selection.recordingTarget ?? '';
  const executor = routing?.executors.find(p => p.id === selected);
  const route = executor?.sources.find(s => s.id === readSource);
  const valid = !selected || !routing || !!route?.recordingTargets.includes(recordingTarget);
  const choose = (id: string, source: string, target: string) => {
    if (!control) return;
    setChosen({ id, readSource: source, recordingTarget: target, revision: chosen?.revision ?? control.selection.revision });
    setError(null);
  };
  const apply = async () => {
    if (!control) return;
    setBusy(true); setError(null);
    try {
      await apiPost('/chess/lichess/selection', { profile: selected || null,
        readSource: selected && routing ? readSource : null,
        recordingTarget: selected && routing ? recordingTarget : null,
        expectedRevision: chosen?.revision ?? control.selection.revision });
      setChosen(null); await refresh();
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  return <Panel className={styles.panel} title="Lichess routing">
    <p className={styles.intro}>Choose execution, knowledge and recording for the shared bot account. Existing games finish with their current settings before a change takes effect.</p>
    {error && <Alert>{error}</Alert>}
    {connectionError && <Alert>{connectionError}</Alert>}
    {view?.error && <Alert>{view.error}</Alert>}
    {control && <>
      <Field label="Execution location">
        <Select value={selected} onChange={e => choose(e.target.value, readSource, recordingTarget)} disabled={!view?.canControl || busy} aria-label="Lichess execution location">
          <option value="">Off — finish existing games</option>
          {profiles.map(p => <option key={p.id} value={p.id}>{p.label} · {p.environment} · {p.implementation}</option>)}
        </Select>
      </Field>
      {routing && selected && <>
        <Field label="Knowledge read source">
          <Select value={readSource} aria-label="Knowledge read source" disabled={!view?.canControl || busy}
            onChange={e => choose(selected, e.target.value, recordingTarget)}>
            <option value="">Select a read source</option>
            {routing.sources.filter(s => executor?.sources.some(r => r.id === s.id)).map(s => <option key={s.id} value={s.id}>{s.label}</option>)}
          </Select>
        </Field>
        <Field label="Game recording destination">
          <Select value={recordingTarget} aria-label="Game recording destination" disabled={!view?.canControl || busy}
            onChange={e => choose(selected, readSource, e.target.value)}>
            <option value="">Select a recording destination</option>
            {routing.recordingTargets.filter(t => route?.recordingTargets.includes(t.id)).map(t => <option key={t.id} value={t.id}>{t.label}</option>)}
          </Select>
        </Field>
        {!valid && <Muted>Select an installed read source and authorized recording destination for this execution location.</Muted>}
      </>}
      {profile && <p>{'knowledge' in profile && <>Knowledge: <strong>{profile.knowledge}</strong>. </>}Up to {profile.maxConcurrent} games; {profile.rated ? 'rated and casual' : 'casual only'}.</p>}
      <Button onClick={() => void apply()} disabled={!view?.canControl || busy || !valid}>{busy ? 'Saving…' : 'Apply routing'}</Button>
      {!view?.canControl && <Muted>Your signed-in account can view this configuration but does not have permission to change the bot destination.</Muted>}
      <p>Controller: {control.controller}. State: <strong>{control.phase}</strong>. Active: {profiles.find(p => p.id === control.activeProfile)?.label ?? 'none'}.</p>
      {routing && control.activeSelection && <p>Reading: {routing.sources.find(s => s.id === control.activeSelection?.readSource)?.label}. Recording: {routing.recordingTargets.find(t => t.id === control.activeSelection?.recordingTarget)?.label}.</p>}
      {(control.activeSelection ? control.selection.revision !== control.activeSelection.revision : control.selection.profile !== control.activeProfile) && control.phase !== 'off' && <Muted>Requested: {profiles.find(p => p.id === control.selection.profile)?.label ?? 'off'}.</Muted>}
      {(control.error || control.bot?.error) && <Alert>{control.error ?? control.bot?.error}</Alert>}
      {control.bot?.username && <p><a href={`https://lichess.org/@/${control.bot.username}`} target="_blank" rel="noreferrer">@{control.bot.username}</a> · {control.bot.connected ? 'connected' : 'disconnected'} · {control.bot.gamesRecorded} completed games</p>}
      {!!control.bot?.recentLog.length && <div className={styles.log}><ul tabIndex={0} aria-label="Lichess activity">{control.bot.recentLog.slice(-8).map((line, i) => <li key={i}>{line}</li>)}</ul></div>}
    </>}
  </Panel>;
}
