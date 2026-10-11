import { useCallback, useEffect, useState } from 'react';
import { apiGet, apiPost } from '../../api/client';
import { Alert, Button, Field, Muted, Panel, Select, useVisiblePolling } from '@ui';
import styles from './LichessPanel.module.css';

interface Profile {
  id: string; label: string; environment: string; implementation: string;
  knowledge: string; maxConcurrent: number; rated: boolean;
}
interface Control {
  controller: string; phase: string; activeProfile: string | null; error?: string;
  selection: { profile: string | null; revision: number; changedUtc: string };
  profiles: Profile[];
  bot?: { connected: boolean; username?: string; gamesRecorded: number; error?: string; recentLog: string[] };
}
interface View { available: boolean; canControl: boolean; error?: string; control?: Control }

export function LichessRoutingPanel() {
  const [view, setView] = useState<View | null>(null);
  const [chosen, setChosen] = useState<{ id: string; revision: number } | null>(null);
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
  const profile = control?.profiles.find(p => p.id === selected);
  const apply = async () => {
    if (!control) return;
    setBusy(true); setError(null);
    try {
      await apiPost('/chess/lichess/selection', { profile: selected || null, expectedRevision: chosen?.revision ?? control.selection.revision });
      setChosen(null); await refresh();
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  return <Panel className={styles.panel} title="Lichess destination">
    <p className={styles.intro}>Choose where Laplace plays. Confirmed moves and outcomes are admitted live to the selected engine’s knowledge database. Existing games finish before a destination change takes effect.</p>
    {error && <Alert>{error}</Alert>}
    {connectionError && <Alert>{connectionError}</Alert>}
    {view?.error && <Alert>{view.error}</Alert>}
    {control && <>
      <Field label="Where are we getting our Lichess UCI from?">
        <Select value={selected} onChange={e => { setChosen({id: e.target.value, revision: control.selection.revision}); setError(null); }} disabled={!view?.canControl || busy} aria-label="Lichess destination">
          <option value="">Off — finish existing games</option>
          {control.profiles.map(p => <option key={p.id} value={p.id}>{p.label} · {p.environment} · {p.implementation}</option>)}
        </Select>
      </Field>
      {profile && <p>Knowledge: <strong>{profile.knowledge}</strong>. Up to {profile.maxConcurrent} games; {profile.rated ? 'rated and casual' : 'casual only'}.</p>}
      <Button onClick={() => void apply()} disabled={!view?.canControl || busy}>{busy ? 'Saving…' : 'Apply destination'}</Button>
      {!view?.canControl && <Muted>Your signed-in account can view this configuration but does not have permission to change the bot destination.</Muted>}
      <p>Controller: {control.controller}. State: <strong>{control.phase}</strong>. Active: {control.profiles.find(p => p.id === control.activeProfile)?.label ?? 'none'}.</p>
      {control.selection.profile !== control.activeProfile && control.phase !== 'off' && <Muted>Requested: {control.profiles.find(p => p.id === control.selection.profile)?.label ?? 'off'}.</Muted>}
      {(control.error || control.bot?.error) && <Alert>{control.error ?? control.bot?.error}</Alert>}
      {control.bot?.username && <p><a href={`https://lichess.org/@/${control.bot.username}`} target="_blank" rel="noreferrer">@{control.bot.username}</a> · {control.bot.connected ? 'connected' : 'disconnected'} · {control.bot.gamesRecorded} completed games</p>}
      {!!control.bot?.recentLog.length && <div className={styles.log}><ul tabIndex={0} aria-label="Lichess activity">{control.bot.recentLog.slice(-8).map((line, i) => <li key={i}>{line}</li>)}</ul></div>}
    </>}
  </Panel>;
}
