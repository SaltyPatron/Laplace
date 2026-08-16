import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Banner, Button, ErrorText, Input, LoadingText, Muted, Panel, TextArea,
} from '@ui';
import { useAppStore } from '../store';
import {
  agentConfig, agentRoutes, askAgent, saveAgentConfig,
  type AgentReply, type AgentRoutes,
} from './api';
import styles from './Admin.module.css';

/**
 * External agent routing — the outbound lane, where this substrate is the CLIENT
 * of other models rather than the server.
 *
 * Three things an operator needs and could not get anywhere else: which routes
 * exist, whether each one's credential actually resolves right now, and whether a
 * route works end to end. The last matters because a misconfigured route fails
 * identically to an absent one from every other surface.
 *
 * CREDENTIAL VALUES ARE NEVER FETCHED. The server sends the variable NAME a key
 * would be read from and a resolved/not-resolved verdict; the value never leaves
 * the host. agents.json holds no secrets either — the parser refuses an inline
 * api_key, because that file rides the deploy payload into a shared directory.
 */
export function Agents() {
  const { tenant } = useAppStore();
  const [routes, setRoutes] = useState<AgentRoutes | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const [config, setConfig] = useState<string>('');
  const [configPath, setConfigPath] = useState<string | null>(null);
  const [configDirty, setConfigDirty] = useState(false);
  const [saveErr, setSaveErr] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const [prompt, setPrompt] = useState('Reply with the single word OK.');
  const [model, setModel] = useState('');
  const [reply, setReply] = useState<AgentReply | null>(null);
  const [askErr, setAskErr] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);

  const load = useCallback(async () => {
    try {
      const [r, c] = await Promise.all([agentRoutes({ tenant }), agentConfig({ tenant })]);
      setRoutes(r);
      setConfig(c.content ?? '');
      setConfigPath(c.path ?? c.write_path);
      setConfigDirty(false);
      setErr(null);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }, [tenant]);

  useEffect(() => { void load(); }, [load]);

  async function save() {
    setSaving(true);
    setSaveErr(null);
    setSaved(null);
    try {
      const res = await saveAgentConfig(config, { tenant });
      setSaved(`Saved to ${res.path} — ${res.routes} route(s).`);
      await load();
    } catch (e) {
      setSaveErr(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  async function ask() {
    setAsking(true);
    setAskErr(null);
    setReply(null);
    try {
      setReply(await askAgent(
        { prompt, model: model.trim() || undefined, timeout_seconds: 180 },
        { tenant },
      ));
    } catch (e) {
      setAskErr(e instanceof Error ? e.message : String(e));
    } finally {
      setAsking(false);
    }
  }

  const aliases = routes?.rows.filter((r) => r.alias) ?? [];
  const providers = routes?.rows.filter((r) => !r.alias) ?? [];

  return (
    <div className={styles.opGrid}>
      <Panel
        title="Routes"
        actions={<Button variant="ghost" onClick={() => void load()}>Refresh</Button>}
      >
        {err && <ErrorText className={styles.runErrBox}>{err}</ErrorText>}
        {routes == null ? <LoadingText>Reading the agent catalog…</LoadingText> : (
          <>
            <Muted>
              config: <code>{routes.config ?? 'none found'}</code>
              {routes.default && <> · default route: <code>{routes.default}</code></>}
              {' · '}keys resolve from the process environment, then{' '}
              <code>secrets/{routes.secret_file}</code>
            </Muted>

            {!routes.config && (
              <Banner variant="info">
                No agents.json was found. The routes below are the built-in providers; saving the
                editor writes a file at <code>{configPath}</code>.
              </Banner>
            )}

            <div className={styles.tableWrap}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th scope="col">route</th>
                    <th scope="col">provider</th>
                    <th scope="col">model</th>
                    <th scope="col">auth</th>
                    <th scope="col">credential</th>
                    <th scope="col">endpoint</th>
                  </tr>
                </thead>
                <tbody>
                  {[...aliases, ...providers].map((r) => (
                    <tr key={`${r.alias ? 'a' : 'p'}:${r.name}`}>
                      <td>
                        {r.name}
                        {r.alias && <Badge className={styles.badge}>alias</Badge>}
                        {r.default && <Badge className={styles.badge}>default</Badge>}
                      </td>
                      <td>{r.provider}</td>
                      <td className={r.model ? undefined : styles.cancelled}>
                        {/* A null model is not a broken route — it is a route
                            nobody has named a model for. Only anthropic ships a
                            default, so this is the normal state for the rest. */}
                        {r.model ?? 'not set — name one as provider/model'}
                      </td>
                      <td>{r.auth}</td>
                      <td className={r.credentialed ? styles.ok : styles.failed}>
                        {r.credentialed ? 'resolved' : 'missing'}
                        <span className={styles.credSource}>{r.credential_source}</span>
                      </td>
                      <td title={r.base_url}>
                        <code className={styles.queryCell}>{r.base_url || '—'}</code>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Panel>

      <Panel title="Test a route">
        <Muted>
          Runs through the same client the MCP <code>ask</code> tool uses, so a pass here is a pass
          there. Leave the model blank to use the default route.
        </Muted>
        <label className={styles.field}>
          <span>model — alias, provider/model, or a vendor-branded name</span>
          <Input value={model} onChange={(e) => setModel(e.target.value)} placeholder="(default)" />
        </label>
        <label className={styles.field}>
          <span>prompt</span>
          <TextArea rows={3} value={prompt} onChange={(e) => setPrompt(e.target.value)} />
        </label>
        <Button onClick={() => void ask()} loading={asking} disabled={!prompt.trim()}>Ask</Button>

        {askErr && <ErrorText className={styles.runErrBox}>{askErr}</ErrorText>}
        {reply && (
          <>
            <Muted>
              {reply.provider}/{reply.model} · {reply.finish_reason ?? 'no finish reason'} ·{' '}
              {reply.input_tokens ?? '—'} in / {reply.output_tokens ?? '—'} out ·{' '}
              {Math.round(reply.provider_ms)}ms · attempt {reply.attempts}
            </Muted>
            {/* A refusal or a block returns an EMPTY reply with a reason. Rendering
                only the text would show nothing at all and read as a dead route. */}
            {reply.note && <Banner variant="warning">{reply.note}</Banner>}
            <pre className={styles.replyBox}>{reply.reply || '(empty)'}</pre>
          </>
        )}
      </Panel>

      <Panel
        title={`agents.json${configPath ? ` — ${configPath}` : ''}`}
        actions={
          <Button onClick={() => void save()} loading={saving} disabled={!configDirty}>
            Save
          </Button>
        }
        className={styles.wideCell}
      >
        <Muted>
          Routing only. Keys are named here (<code>api_key_env</code>) and read from the
          environment or the secrets file — an inline <code>api_key</code> is refused, because this
          file is published into the shared app directory by the deploy. The document is validated
          by the same parser every call uses before anything is written.
        </Muted>
        <TextArea
          rows={18}
          spellCheck={false}
          value={config}
          onChange={(e) => { setConfig(e.target.value); setConfigDirty(true); setSaved(null); }}
          placeholder='{ "default": "house", "agents": { "house": { "provider": "anthropic", "model": "claude-opus-5" } } }'
        />
        {saveErr && <ErrorText className={styles.runErrBox}>{saveErr}</ErrorText>}
        {saved && <Banner variant="info">{saved}</Banner>}
      </Panel>
    </div>
  );
}
