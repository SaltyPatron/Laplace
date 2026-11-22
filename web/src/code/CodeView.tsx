import { useState } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Chip,
  ErrorText,
  Field,
  Input,
  Muted,
  Panel,
  Select,
  Table,
  Td,
  TextArea,
  Th,
} from '@ui';
import { describeFailure } from '../api/client';
import { EntityLink } from '../explore/components/EntityLink';
import { useAppStore } from '../store';
import { completeCode, searchCode, type CodeCompletionResponse, type CodeSearchResult } from './api';
import styles from './CodeView.module.css';

const CODE_LANGUAGES = [
  'c',
  'cpp',
  'c-sharp',
  'python',
  'rust',
  'go',
  'javascript',
  'typescript',
  'java',
  'bash',
  'sql',
  'lua',
  'ruby',
  'julia',
  'kotlin',
  'cuda',
  'fortran',
  'llvm',
] as const;

export function CodeView() {
  const { tenant, quoteId } = useAppStore();
  const [prompt, setPrompt] = useState('');
  const [language, setLanguage] = useState<string>('c');
  const [maxAttempts, setMaxAttempts] = useState('4');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [result, setResult] = useState<CodeCompletionResponse | null>(null);

  const [searchQuery, setSearchQuery] = useState('');
  const [searchPath, setSearchPath] = useState('');
  const [reindex, setReindex] = useState(false);
  const [searchBusy, setSearchBusy] = useState(false);
  const [searchErr, setSearchErr] = useState<string | null>(null);
  const [searchData, setSearchData] = useState<CodeSearchResult | null>(null);

  async function runCodeOoda() {
    if (!prompt.trim() || busy) return;
    setBusy(true);
    setErr(null);
    try {
      const { data } = await completeCode({
        model: 'laplace-code-001',
        prompt,
        code_language: language,
        max_attempts: Math.min(8, Math.max(1, Number(maxAttempts) || 4)),
      }, { tenant, quoteId });
      setResult(data);
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function runCodeSearch() {
    if (!searchQuery.trim() || searchBusy) return;
    setSearchBusy(true);
    setSearchErr(null);
    try {
      const data = await searchCode({
        query: searchQuery,
        path: searchPath.trim() || null,
        limit: 20,
        offset: 0,
        reindex,
      }, { tenant, quoteId });
      setSearchData(data);
    } catch (e) {
      setSearchErr(describeFailure(e).message);
    } finally {
      setSearchBusy(false);
    }
  }

  const hits = searchData?.matches ?? searchData?.results ?? [];

  return (
    <div className={styles.page}>
      <header className={styles.hero}>
        <div>
          <h2>Code OODA &amp; Repair</h2>
          <Muted>
            Same OODA spine as conversation (<code>InternalMonologue 0.10</code> + <code>ToolchainVerification 0.90</code>):
            generate candidates, compile/verify with the language toolchain, and localize failures across attempts.
          </Muted>
        </div>
        <Badge>model: laplace-code-001</Badge>
      </header>

      <div className={styles.grid}>
        <Panel title="Toolchain-verified code generation">
          <div className={styles.stack}>
            <div className={styles.row2}>
              <Field label="Tree-sitter language">
                <Select aria-label="Tree-sitter language" value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {CODE_LANGUAGES.map((lang) => (
                    <option key={lang} value={lang}>{lang}</option>
                  ))}
                </Select>
              </Field>
              <Field label="Max OODA attempts (1–8)">
                <Input
                  type="number"
                  min={1}
                  max={8}
                  value={maxAttempts}
                  onChange={(e) => setMaxAttempts(e.target.value)}
                />
              </Field>
            </div>
            <Field label="Code prompt / specification">
              <TextArea
                rows={6}
                value={prompt}
                placeholder="Write a bounded-memory ring buffer in C…"
                onChange={(e) => setPrompt(e.target.value)}
              />
            </Field>
            <div>
              <Button onClick={() => void runCodeOoda()} loading={busy} disabled={busy || !prompt.trim()}>
                Run Code OODA pass
              </Button>
            </div>
            {err && <ErrorText role="alert">{err}</ErrorText>}

            {result && (
              <div className={styles.stack}>
                <div className={styles.statusRow}>
                  <Chip variant={result.verified ? 'engineOk' : 'engineMissing'}>
                    {result.verified ? 'Verified (compile/test WIN)' : `Unverified (${result.failure_kind ?? 'failed'})`}
                  </Chip>
                  <span>language: <code>{result.code_language}</code></span>
                  {result.candidate_id && <span>candidate: <code>{result.candidate_id}</code></span>}
                </div>

                {result.code ? (
                  <pre className={styles.codeBlock}><code>{result.code}</code></pre>
                ) : (
                  <Muted>No code candidate emitted.</Muted>
                )}

                {result.attempts.length > 0 && (
                  <div className={styles.attempts}>
                    <h4>OODA Toolchain Verification Rounds ({result.attempts.length})</h4>
                    <Table>
                      <thead>
                        <tr>
                          <Th>#</Th>
                          <Th>Status</Th>
                          <Th>Tool</Th>
                          <Th>Exit</Th>
                          <Th>Candidate</Th>
                        </tr>
                      </thead>
                      <tbody>
                        {result.attempts.map((a) => (
                          <tr key={`${a.attempt}:${a.candidate_id}`}>
                            <Td>{a.attempt}</Td>
                            <Td>
                              {a.verified ? 'WIN' : a.timed_out ? 'TIMEOUT' : !a.tool_available ? 'NO TOOL' : 'LOSS'}
                            </Td>
                            <Td><code>{a.tool}</code></Td>
                            <Td>{a.exit_code}</Td>
                            <Td><code>{a.candidate_id}</code></Td>
                          </tr>
                        ))}
                      </tbody>
                    </Table>
                    {result.attempts.map((a) => (a.stdout || a.stderr) ? (
                      <details key={`log:${a.attempt}`} open={!a.verified && a.attempt === result.attempts.length}>
                        <summary>Attempt #{a.attempt} ({a.tool}) output</summary>
                        {a.stdout && <pre className={styles.toolLog}>{a.stdout}</pre>}
                        {a.stderr && <pre className={styles.toolLog}>{a.stderr}</pre>}
                      </details>
                    ) : null)}
                  </div>
                )}
              </div>
            )}
          </div>
        </Panel>

        <Panel title="Code substrate search">
          <div className={styles.stack}>
            <Field label="Query">
              <Input
                value={searchQuery}
                placeholder="Symbol, identifier, or AST pattern…"
                onChange={(e) => setSearchQuery(e.target.value)}
              />
            </Field>
            <Field label="Path filter (optional)">
              <Input
                value={searchPath}
                placeholder="e.g. src/forward/"
                onChange={(e) => setSearchPath(e.target.value)}
              />
            </Field>
            <Checkbox checked={reindex} onChange={(e) => setReindex(e.target.checked)} label="Reindex workspace before search" />
            <div>
              <Button variant="ghost" onClick={() => void runCodeSearch()} loading={searchBusy} disabled={searchBusy || !searchQuery.trim()}>
                Search code
              </Button>
            </div>
            {searchErr && <ErrorText role="alert">{searchErr}</ErrorText>}
            {searchData && (
              hits.length === 0 ? (
                <Muted>No code matches returned for this query.</Muted>
              ) : (
                <Table>
                  <thead>
                    <tr><Th>Symbol / match</Th><Th>Path</Th><Th>μ / Witnesses</Th></tr>
                  </thead>
                  <tbody>
                    {hits.map((m, idx) => (
                      <tr key={`${m.id_hex ?? m.path ?? idx}:${idx}`}>
                        <td>
                          {m.id_hex ? <EntityLink idHex={m.id_hex} label={m.symbol ?? m.label ?? m.id_hex} /> : (m.symbol ?? m.label ?? '—')}
                          {m.snippet && <pre className={styles.toolLog}>{m.snippet}</pre>}
                        </td>
                        <td><code>{m.path ?? '—'}</code></td>
                        <td>
                          {m.eff_mu != null ? `μ ${Number(m.eff_mu).toFixed(1)}` : '—'}
                          {m.witnesses != null ? ` · ${m.witnesses}w` : ''}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              )
            )}
          </div>
        </Panel>
      </div>
    </div>
  );
}
