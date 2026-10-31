import { useState } from 'react';
import { Link as RouteLink, useSearchParams } from 'react-router-dom';
import { Badge, Button, ErrorText, Field, Input, LoadingText, Muted, NavTabs, Panel, Select, Stack, Table, TableScroll, Td, Th, useReadResource } from '@ui';
import styles from './ArchitectureView.module.css';

interface Evidence { path: string; sha256?: string; source?: string; verification?: string }
interface Diagram { id: string; title: string; kind: string; description: string; assets: string[]; evidence: Evidence[]; operations: string[]; related: string[] }
interface Operation { id: string; title: string; path: string; fields: Record<string, string>; missing_fields: string[] }
interface Stage { id: string; title: string; operations: Operation[] }
interface Registration { id: string; name: string; family: string; surface: string; source: string; line: number; status: string; purpose?: string; source_bodies?: { name: string; url: string }[] }
interface Catalog {
  diagrams: Diagram[]; stages: Stage[]; registrations: Registration[];
  conflicts: { title: string; text: string; path: string; line: number }[];
  reconciliation: { observations: Record<string, { finding: string; traces: string[] }[]> };
  lanes: { name: string; contract: string; paths: string[]; split: string; recovery: string }[];
  coverage: { operations: number; registrations: number; diagrams: number; sourceDefinitions: number; sourceParseErrors: number };
  validation: { sourceFilesChecked: number; unavailableSourceFiles: number; scope: string };
}
interface SourceFile { id: string; repo: string; path: string; sha256: string; parse_error: boolean }
interface Symbol { id: string; name: string; repo: string; path: string; file: string; line: number; end_line: number; source?: string }
interface Sources { files: SourceFile[]; symbols: Symbol[]; revisions: Record<string, string>; limitations: string[] }
const root = '/explore/architecture';
const asset = (path: string) => `/architecture/${path}`;
const address = (values: Record<string, string>) => `${root}?${new URLSearchParams(values)}`;
const plain = (text: string) => text.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '$1 ($2)').replace(/\*\*|`/g, '');
async function json<T>(url: string, signal: AbortSignal): Promise<T> {
  const result = await fetch(url, { signal });
  if (!result.ok) throw new Error(`Architecture resource could not be loaded (${result.status}).`);
  return result.json() as Promise<T>;
}
function DiagramLink({ id, catalog }: { id: string; catalog: Catalog }) {
  const diagram = catalog.diagrams.find((d) => d.id === id);
  return diagram ? <RouteLink to={address({ diagram: id })}>{diagram.title}</RouteLink> : <span>Unmapped diagram: {id}</span>;
}

export function ArchitectureView() {
  const read = useReadResource<Catalog>({ key: 'architecture:catalog', read: (signal) => json(asset('catalog.json'), signal) });
  if (!read.data) return read.error ? <Panel title="Architecture unavailable"><ErrorText role="alert">{read.error.message}</ErrorText><Button onClick={() => void read.reload()}>Retry architecture</Button></Panel> : <LoadingText>Loading architecture and operation evidence…</LoadingText>;
  return <Architecture catalog={read.data} />;
}
function Architecture({ catalog }: { catalog: Catalog }) {
  const [params, setParams] = useSearchParams();
  const section = params.get('section') ?? 'diagrams';
  const q = params.get('q') ?? '';
  const set = (values: Record<string, string>) => { const next = new URLSearchParams(params); for (const [key, value] of Object.entries(values)) { if (value) next.set(key, value); else next.delete(key); } setParams(next); };
  const sections = [['diagrams', 'System & flows'], ['operations', 'Operation contracts'], ['registrations', 'Code registrations'], ['conflicts', 'Conflicts'], ['sources', 'Source inspection']];
  return <Stack className={styles.workspace} gap={4}>
    <header><h2>Architecture & operations</h2><p>Follow Laplace from source admission through cognition, effects and learning. Compare the contract with each implementation and inspect the evidence behind the diagram.</p></header>
    <NavTabs tabs={sections.map(([id, label]) => ({ id, label, active: section === id, href: address({ section: id }), onClick: () => setParams({ section: id }) }))} />
    <Muted>{catalog.coverage.operations} specification operations · {catalog.coverage.registrations} source registrations · {catalog.coverage.diagrams} diagrams and figures.</Muted>
    {section === 'diagrams' ? <DiagramBrowser catalog={catalog} /> : section === 'sources' ? <SourceBrowser /> : <>
      <Field label="Search this inventory"><Input value={q} onChange={(e) => set({ q: e.target.value })} type="search" /></Field>
      {section === 'operations' ? <Operations catalog={catalog} query={q} /> : section === 'registrations' ? <Registrations catalog={catalog} query={q} /> : section === 'conflicts' ? <Stack>{catalog.conflicts.filter((c) => `${c.title} ${c.text}`.toLowerCase().includes(q.toLowerCase())).map((c) => <Panel key={c.title} title={plain(c.title)}>
        {c.text.split('\n').filter(Boolean).map((line, i) => <p key={i} className={styles.prose}>{plain(line.replace(/^- /, ''))}</p>)}
        {(catalog.reconciliation.observations[c.title.split('.')[0]] ?? []).map((o, i) => <div key={i}><h3>Implementation observation</h3><p>{o.finding}</p><ul>{o.traces.map((id) => <li key={id}><DiagramLink id={`trace-diagrams/${id}`} catalog={catalog} /></li>)}</ul></div>)}
        <Muted>{c.path}:{c.line}</Muted>
      </Panel>)}</Stack> : <Panel title="Unknown architecture section"><RouteLink to={root}>Return to system and flows</RouteLink></Panel>}
    </>}
    <details><summary>What has been checked</summary><p>{catalog.validation.scope}</p><p>{catalog.validation.sourceFilesChecked} available source files matched recorded hashes; {catalog.validation.unavailableSourceFiles} require their separate checkout for a fresh comparison.</p><p>Syntax extraction reports {catalog.coverage.sourceParseErrors} files with parse errors.</p><a href={asset('catalog.json')} download>Download architecture evidence</a></details>
  </Stack>;
}
function DiagramBrowser({ catalog }: { catalog: Catalog }) {
  const [params, setParams] = useSearchParams();
  const [zoom, setZoom] = useState('fit');
  const [naturalWidth, setNaturalWidth] = useState(0);
  const [imageError, setImageError] = useState('');
  const query = params.get('q') ?? '';
  const kind = params.get('kind') ?? '';
  const selectedId = params.get('diagram') ?? 'diagrams/whole-lifecycle';
  const selected = catalog.diagrams.find((d) => d.id === selectedId);
  const matches = catalog.diagrams.filter((d) => (!kind || d.kind === kind) && `${d.title} ${d.description} ${d.evidence.map((e) => e.path).join(' ')}`.toLowerCase().includes(query.toLowerCase()));
  const update = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next, { replace: key === 'q' }); };
  const svg = selected?.assets.find((a) => a.endsWith('.svg'));
  return <Stack gap={4}>
    <div className={styles.filters}>
      <Field label="Find a diagram"><Input type="search" value={query} onChange={(e) => update('q', e.target.value)} /></Field>
      <Field label="Evidence kind"><Select value={kind} onChange={(e) => update('kind', e.target.value)}><option value="">All kinds</option>{['Specification', 'Source trace', 'Numbered outline', 'Analytic figure'].map((value) => <option key={value}>{value}</option>)}</Select></Field>
      <Field label="Diagram"><Select value={selectedId} onChange={(e) => update('diagram', e.target.value)}>{!matches.some((d) => d.id === selectedId) && <option value={selectedId}>{selected?.title ?? 'Unknown diagram'}</option>}{matches.map((d) => <option key={d.id} value={d.id}>{d.title}</option>)}</Select></Field>
    </div>
    <Muted role="status">{matches.length} matching diagrams and figures</Muted>
    {selected ? <Panel title={selected.title} expandable actions={<Badge>{selected.kind}</Badge>}>
      <p>{selected.description}</p>
      <div className={styles.actions}><Field label="Diagram scale"><Select value={zoom} onChange={(e) => setZoom(e.target.value)}><option value="fit">Fit width</option><option value="100">100% — original text size</option><option value="150">150%</option><option value="200">200%</option></Select></Field>{selected.assets.map((a) => <a key={a} href={asset(a)} download>{a.split('.').pop()?.toUpperCase()}</a>)}</div>
      {imageError === selected.id && <ErrorText role="alert">Diagram image failed to load. Its source and export links are available above.</ErrorText>}
      <div className={styles.figure} tabIndex={0} role="region" aria-label="Scrollable UML diagram">
        <img key={selected.id} src={svg ? asset(svg) : undefined} alt={selected.title} onLoad={(e) => setNaturalWidth(e.currentTarget.naturalWidth)} onError={() => setImageError(selected.id)} style={zoom === 'fit' ? { width: '100%' } : { width: naturalWidth ? `${naturalWidth * Number(zoom) / 100}px` : 'auto' }} />
      </div>
      {selected.operations.length > 0 && <><h3>Operation contracts</h3><div className={styles.actions}>{selected.operations.map((id) => <RouteLink key={id} to={address({ section: 'operations', operation: id })}>{id}</RouteLink>)}</div></>}
      {selected.related.length > 0 && <><h3>Related implementation boundaries</h3><ul>{selected.related.map((id) => <li key={id}><DiagramLink id={id} catalog={catalog} /></li>)}</ul></>}
      <details><summary>Sources and review boundaries</summary><ul>{selected.evidence.map((e, i) => <li key={i} className={styles.evidence}>{e.source ? <RouteLink to={address({ section: 'sources', file: e.source, expected: e.sha256 ?? '' })}>{e.path}</RouteLink> : e.path}{e.sha256 && <><br /><code>SHA-256 {e.sha256}</code></>}{e.verification && <p>{e.verification}</p>}</li>)}</ul></details>
    </Panel> : <Panel title="Diagram not found"><p>This saved address names a diagram absent from this build.</p><RouteLink to={root}>Open whole lifecycle</RouteLink></Panel>}
    <details><summary>Browse all matching diagrams ({matches.length})</summary><ul>{matches.map((d) => <li key={d.id}><DiagramLink id={d.id} catalog={catalog} /> · {d.kind}</li>)}</ul></details>
    <details><summary>Follow an implementation lifecycle</summary><Stack>{catalog.lanes.map((lane) => <section key={lane.name}><h3>{lane.name}</h3><p>{lane.contract}</p><p>{lane.split}</p><p>{lane.recovery}</p><ul>{lane.paths.map((id) => <li key={id}><DiagramLink id={`trace-diagrams/${id}`} catalog={catalog} /></li>)}</ul></section>)}</Stack></details>
  </Stack>;
}
function Operations({ catalog, query }: { catalog: Catalog; query: string }) {
  const [params] = useSearchParams();
  const chosen = params.get('operation');
  const operations = catalog.stages.flatMap((s) => s.operations).filter((o) => (!chosen || o.id === chosen) && `${o.id} ${o.title} ${Object.values(o.fields).join(' ')}`.toLowerCase().includes(query.toLowerCase()));
  return <Stack><p role="status">{operations.length} operation contracts {chosen && <RouteLink to={address({ section: 'operations' })}>Show all operations</RouteLink>}</p>{operations.map((o) => <details key={o.id} open={!!chosen}><summary>{o.id} {o.title}</summary><dl>{['In', 'Do', 'Out', 'From'].map((key) => <div key={key}><dt>{({ In: 'Inputs', Do: 'Operation', Out: 'Outputs', From: 'Authority' })[key]}</dt><dd className={styles.prose}>{plain(o.fields[key] ?? 'Missing in the source contract')}</dd></div>)}</dl><h3>Implementation comparisons</h3><ul>{catalog.diagrams.filter((d) => d.kind === 'Source trace' && d.operations.includes(o.id)).map((d) => <li key={d.id}><DiagramLink id={d.id} catalog={catalog} /></li>)}</ul>{!catalog.diagrams.some((d) => d.kind === 'Source trace' && d.operations.includes(o.id)) && <Muted>No implementation trace is mapped to this operation.</Muted>}</details>)}</Stack>;
}
function Registrations({ catalog, query }: { catalog: Catalog; query: string }) {
  const [page, setPage] = useState(0);
  const rows = catalog.registrations.filter((r) => `${r.name} ${r.family} ${r.surface} ${r.source} ${r.status}`.toLowerCase().includes(query.toLowerCase()));
  const current = Math.min(page, Math.max(0, Math.ceil(rows.length / 40) - 1));
  return <><p role="status">{rows.length} source registrations</p><TableScroll><Table><thead><tr><Th>Name / surface</Th><Th>Source</Th><Th>Evidence</Th></tr></thead><tbody>{rows.slice(current * 40, current * 40 + 40).map((r) => <tr key={r.id}><Td>{r.name}<br /><Muted>{r.family} · {r.surface}</Muted>{r.purpose && <p>{r.purpose}</p>}</Td><Td className={styles.evidence}>{r.source}:{r.line}</Td><Td>{r.status}{r.source_bodies?.map((b, i) => b.url.startsWith('traces.html#') ? <p key={i}><DiagramLink id={`trace-diagrams/${b.url.split('#')[1]}`} catalog={catalog} /></p> : b.url.startsWith('source.html#') ? <p key={i}><RouteLink to={address({ section: 'sources', symbol: b.url.split('#')[1] })}>{b.name}</RouteLink></p> : null)}<details><summary>Registration record</summary><pre>{JSON.stringify(r, null, 2)}</pre></details></Td></tr>)}</tbody></Table></TableScroll><div className={styles.actions}><Button disabled={current === 0} onClick={() => setPage(current - 1)}>Previous registrations</Button><span>Page {current + 1} of {Math.max(1, Math.ceil(rows.length / 40))}</span><Button disabled={(current + 1) * 40 >= rows.length} onClick={() => setPage(current + 1)}>Next registrations</Button></div></>;
}
function SourceBrowser() {
  const [params, setParams] = useSearchParams();
  const read = useReadResource<Sources>({ key: 'architecture:sources', read: (signal) => json(asset('source-map.json'), signal) });
  const query = params.get('q') ?? '';
  const symbol = read.data?.symbols.find((s) => s.id === params.get('symbol'));
  const file = read.data?.files.find((f) => f.id === (symbol?.file ?? params.get('file')));
  const bodies = useReadResource<Symbol[]>({ key: `architecture:source:${file?.id}`, enabled: !!file, read: (signal) => json(asset(`sources/${file?.id}.json`), signal) });
  if (!read.data) return read.error ? <ErrorText role="alert">{read.error.message}</ErrorText> : <LoadingText>Loading source index…</LoadingText>;
  const matches = query ? read.data.symbols.filter((s) => `${s.name} ${s.repo}/${s.path}`.toLowerCase().includes(query.toLowerCase())) : [];
  return <Stack><Field label="Find source definitions"><Input type="search" value={query} onChange={(e) => { const next = new URLSearchParams(params); next.set('q', e.target.value); setParams(next, { replace: true }); }} /></Field><p>{read.data.symbols.length} extracted definitions. Syntax and lexical names do not prove execution or call binding.</p>
    {query && <><p role="status">{matches.length} matches; first 100 shown. Narrow the search to inspect further matches.</p><ul>{matches.slice(0, 100).map((s) => <li key={s.id}><RouteLink to={address({ section: 'sources', symbol: s.id })}>{s.name}</RouteLink> · {s.repo}/{s.path}:{s.line}</li>)}</ul></>}
    {file && <Panel title={`${file.repo}/${file.path}`}><p className={styles.evidence}>Snapshot revision: {read.data.revisions[file.repo]}<br />SHA-256: {file.sha256}</p>{params.get('expected') && params.get('expected') !== file.sha256 && <ErrorText>This syntax snapshot differs from the source hash reviewed for the diagram. It cannot verify that diagram's current source.</ErrorText>}{file.parse_error && <ErrorText>This source has syntax extraction errors; the extracted structure may be incomplete.</ErrorText>}{bodies.error ? <ErrorText role="alert">{bodies.error.message}</ErrorText> : !bodies.data ? <LoadingText>Loading source snapshot…</LoadingText> : bodies.data.filter((s) => !symbol || s.id === symbol.id).map((s) => <details key={s.id} open={!!symbol}><summary>{s.name} · lines {s.line}–{s.end_line}</summary><pre className={styles.source}>{s.source}</pre></details>)}</Panel>}
    <details><summary>Extraction limitations</summary><ul>{read.data.limitations.map((s) => <li key={s}>{s}</li>)}</ul></details>
  </Stack>;
}
