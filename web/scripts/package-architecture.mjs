import { readFile, writeFile, mkdir, copyFile, readdir, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const web = fileURLToPath(new URL('../', import.meta.url));
const repo = path.dirname(web);
const atlas = path.join(repo, 'docs/operation-atlas');
const out = path.join(web, 'public/architecture');
const read = async (name) => JSON.parse(await readFile(path.join(atlas, name), 'utf8'));
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
function reviewedSource(bytes, expected) {
  if (digest(bytes) === expected) return 'matches working source';
  const text = bytes.toString('utf8');
  if (!Buffer.from(text, 'utf8').equals(bytes)) return 'working source modified since diagram review';
  const lf = text.replace(/\r\n/g, '\n');
  if (digest(Buffer.from(lf)) === expected || digest(Buffer.from(lf.replace(/\n/g, '\r\n'))) === expected) {
    return 'matches working source after line-ending normalization';
  }
  return 'working source modified since diagram review';
}
const [machine, traces, sequence, isa, inventory, publication, integration, sourceMap] = await Promise.all(
  ['machine.json', 'traces.json', 'sequence.json', 'isa-views.json', 'operations.json',
    'publication-evidence.json', 'integration.json', 'source-map.json'].map(read));
const files = new Map(sourceMap.files.map((f) => [`${f.repo}/${f.path}`, f]));
const diagrams = [];
function diagram(folder, id, title, kind, description, evidence = [], operations = [], related = []) {
  diagrams.push({ id: `${folder}/${id}`, title, kind, description,
    evidence: evidence.map((e) => {
      const item = typeof e === 'string' ? { path: e } : e;
      return { ...item, source: files.get(item.path)?.id };
    }), operations, related: related.map((id) => `trace-diagrams/${id}`),
    assets: ['svg', 'png', folder === 'publication-figures' ? 'pdf' : 'puml'].map((ext) => `${folder}/${id}.${ext}`) });
}
for (const [id, d] of Object.entries(machine.diagrams)) diagram('diagrams', id, d.title, 'Specification', d.status, d.evidence);
for (const t of traces) diagram('trace-diagrams', t.id, t.title, 'Source trace', t.boundary, t.evidence, t.spec_operations, t.nested_views);
for (const s of sequence.stages) diagram('sequence-diagrams', s.id, s.title, 'Numbered outline',
  'Numbered contract order only. Branches and runtime behavior require the linked source traces.',
  [`Laplace-Wiki/${s.path}`], s.operations.map((o) => o.id));
for (const [id, d] of Object.entries(isa)) diagram('isa-diagrams', id, `Refactor ISA · ${id}`, 'Source trace', d.notes,
  [...new Set(d.handlers.map((h) => `${h.repo}/${h.path}`))]);
for (const f of publication.figures) diagram('publication-figures', f.id, f.title, 'Analytic figure', `${f.kind}. ${f.caption}`);
const ids = new Set(diagrams.map((d) => d.id));
const operationIds = new Set(sequence.stages.flatMap((s) => s.operations.map((o) => o.id)));
const snapshots = new Map();
for (const d of diagrams) {
  for (const id of d.related) if (!ids.has(id)) throw new Error(`Missing related diagram: ${id}`);
  for (const id of d.operations) if (!operationIds.has(id)) throw new Error(`Missing operation: ${id}`);
  for (const e of d.evidence) {
    if (!e.sha256) continue;
    if (!snapshots.has(e.path)) {
      const location = path.resolve(repo, '..', e.path);
      let bytes;
      try { bytes = await readFile(location); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      const verification = bytes && reviewedSource(bytes, e.sha256);
      if (bytes && !verification) throw new Error(`Stale UML source: ${e.path}`);
      snapshots.set(e.path, verification || 'recorded snapshot; source checkout unavailable');
    }
    e.verification = snapshots.get(e.path);
  }
}
await mkdir(out, { recursive: true });
for (const d of diagrams) {
  for (const relative of d.assets) {
    const source = path.join(atlas, relative);
    const bytes = await readFile(source);
    if (relative.endsWith('.svg') && /Syntax Error|<script\b/i.test(bytes.toString())) throw new Error(`Invalid diagram: ${relative}`);
    await mkdir(path.dirname(path.join(out, relative)), { recursive: true });
    await copyFile(source, path.join(out, relative));
  }
}
// Preserve source inspection without executing the former standalone viewer's scripts.
await mkdir(path.join(out, 'sources'), { recursive: true });
for (const name of await readdir(path.join(atlas, 'source-data'))) {
  if (!/^[a-f0-9]+\.js$/.test(name)) continue;
  const content = await readFile(path.join(atlas, 'source-data', name), 'utf8');
  const match = content.match(/^window\.SOURCE_FILES\["([a-f0-9]+)"\]=([\s\S]*);$/);
  if (!match) throw new Error(`Unexpected source snapshot wrapper: ${name}`);
  await writeFile(path.join(out, 'sources', `${match[1]}.json`), JSON.stringify(JSON.parse(match[2])));
}
await writeFile(path.join(out, 'source-map.json'), JSON.stringify(sourceMap));
const catalog = {
  diagrams, stages: sequence.stages, conflicts: sequence.conflicts,
  reconciliation: sequence.reconciliation, registrations: inventory.operations,
  inventoryRevisions: inventory.repositories, lanes: integration.lanes,
  publicationFindings: publication.findings,
  coverage: { operations: operationIds.size, registrations: inventory.operations.length,
    diagrams: diagrams.length, sourceDefinitions: sourceMap.symbols.length,
    sourceParseErrors: sourceMap.files.filter((f) => f.parse_error).length },
  validation: { sourceFilesChecked: [...snapshots.values()].filter((s) => s.startsWith('matches working source')).length,
    modifiedSourceFiles: [...snapshots.values()].filter((s) => s === 'working source modified since diagram review').length,
    unavailableSourceFiles: [...snapshots.values()].filter((s) => s === 'recorded snapshot; source checkout unavailable').length,
    scope: 'Export presence, UML syntax markers, references and available source hashes. Semantic review and runtime acceptance are separate.' },
};
await writeFile(path.join(out, 'catalog.json'), JSON.stringify(catalog));
console.log(JSON.stringify({ architecture: catalog.coverage, validation: catalog.validation,
  catalogBytes: (await stat(path.join(out, 'catalog.json'))).size }));
