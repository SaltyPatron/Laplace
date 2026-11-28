import { useState } from 'react';
import {
  Badge,
  Banner,
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
import { describeFailure, PaymentRequiredError } from '../api/client';
import { useAppStore } from '../store';
import {
  compileRecipe,
  exportSynthesis,
  quoteRecipe,
  quoteSynthesis,
  type RecipeCompileResponse,
  type RecipeQuoteResponse,
  type SynthesisExportResponse,
  type SynthesisQuoteResponse,
} from './api';
import styles from './FoundryView.module.css';

const STARTER_RECIPES: { id: string; label: string; json: string }[] = [
  {
    id: 'dense-4l',
    label: 'Dense 4-layer GQA (laplace-blake3-v1)',
    json: JSON.stringify({
      kind: 'laplace.recipe',
      name: 'laplace-foundry-dense4',
      structure: 'dense',
      hidden_size: 256,
      num_layers: 4,
      intermediate_size: 768,
      tie_embeddings: false,
      embed: { op: 'coord' },
      lm_head: { op: 'trajectory' },
      compile: 'continuation',
      layers: Array.from({ length: 4 }, () => ({
        kv_heads: 2,
        heads: [{ op: 'context' }, { op: 'context' }, { op: 'relation' }, { op: 'relation' }],
        ffn: { op: 'unary' },
      })),
    }, null, 2),
  },
  {
    id: 'dense-6l-tied',
    label: 'Dense 6-layer tied GQA (laplace-blake3-v1)',
    json: JSON.stringify({
      kind: 'laplace.recipe',
      name: 'laplace-foundry-dense6',
      structure: 'dense',
      hidden_size: 384,
      num_layers: 6,
      intermediate_size: 1024,
      tie_embeddings: true,
      embed: { op: 'coord' },
      lm_head: { op: 'trajectory' },
      compile: 'continuation',
      layers: Array.from({ length: 6 }, () => ({
        kv_heads: 2,
        heads: [
          { op: 'context' }, { op: 'context' }, { op: 'relation' },
          { op: 'relation' }, { op: 'highway' }, { op: 'consensus' },
        ],
        ffn: { op: 'unary' },
      })),
    }, null, 2),
  },
];

export function FoundryView() {
  const { tenant, quoteId, setQuoteId } = useAppStore();
  const [recipeText, setRecipeText] = useState(STARTER_RECIPES[0].json);
  const [recipeQuote, setRecipeQuote] = useState<RecipeQuoteResponse | null>(null);
  const [compiled, setCompiled] = useState<RecipeCompileResponse | null>(null);
  const [compileErr, setCompileErr] = useState<string | null>(null);
  const [compileBusy, setCompileBusy] = useState(false);

  const [format, setFormat] = useState<'gguf' | 'safetensors'>('gguf');
  const [recipeIdPrefix, setRecipeIdPrefix] = useState('');
  const [tokenizerDir, setTokenizerDir] = useState('');
  const [filename, setFilename] = useState('laplace-blake3-v1.gguf');
  const [vocabSize, setVocabSize] = useState('32000');
  const [hiddenSize, setHiddenSize] = useState('256');
  const [numLayers, setNumLayers] = useState('4');
  const [numHeads, setNumHeads] = useState('4');
  const [numKvHeads, setNumKvHeads] = useState('2');
  const [intermediateSize, setIntermediateSize] = useState('768');
  const [tiedEmbeddings, setTiedEmbeddings] = useState(false);

  const [synthQuote, setSynthQuote] = useState<SynthesisQuoteResponse | null>(null);
  const [exported, setExported] = useState<SynthesisExportResponse | null>(null);
  const [exportErr, setExportErr] = useState<string | null>(null);
  const [exportBusy, setExportBusy] = useState(false);

  async function onQuoteRecipe() {
    setCompileBusy(true);
    setCompileErr(null);
    try {
      const q = await quoteRecipe(
        { tenant, action: 'compile', content_items: 1, commercial: false, private_export: false },
        { tenant },
      );
      setRecipeQuote(q);
      if (q.quote_id) setQuoteId(q.quote_id);
    } catch (e) {
      setCompileErr(describeFailure(e).message);
    } finally {
      setCompileBusy(false);
    }
  }

  async function onCompileRecipe() {
    setCompileBusy(true);
    setCompileErr(null);
    try {
      const res = await compileRecipe({ recipe: recipeText }, { tenant, quoteId });
      setCompiled(res);
      if (res.recipe_id_hex) setRecipeIdPrefix(res.recipe_id_hex);
    } catch (e) {
      if (e instanceof PaymentRequiredError) {
        setCompileErr(`Payment required (${e.message}). Run Preflight recipe quote first.`);
      } else {
        setCompileErr(describeFailure(e).message);
      }
    } finally {
      setCompileBusy(false);
    }
  }

  async function onQuoteSynthesis() {
    setExportBusy(true);
    setExportErr(null);
    try {
      const q = await quoteSynthesis({
        tenant,
        vocab_size: Number(vocabSize) || 32000,
        hidden_size: Number(hiddenSize) || 256,
        num_layers: Number(numLayers) || 4,
        num_heads: Number(numHeads) || 4,
        num_kv_heads: Number(numKvHeads) || 2,
        intermediate_size: Number(intermediateSize) || 768,
        tied_embeddings: tiedEmbeddings,
        format,
      }, { tenant });
      setSynthQuote(q);
      if (q.quote_id) setQuoteId(q.quote_id);
    } catch (e) {
      setExportErr(describeFailure(e).message);
    } finally {
      setExportBusy(false);
    }
  }

  async function onExportSynthesis() {
    setExportBusy(true);
    setExportErr(null);
    try {
      const prefix = recipeIdPrefix.trim();
      const res = await exportSynthesis({
        recipe: prefix ? null : recipeText,
        recipe_id_prefix: prefix || null,
        tokenizer_dir: tokenizerDir.trim() || null,
        format,
        filename: filename.trim() || null,
      }, { tenant, quoteId });
      setExported(res);
    } catch (e) {
      if (e instanceof PaymentRequiredError) {
        setExportErr(`Payment required (${e.message}). Run Preflight synthesis quote first.`);
      } else {
        setExportErr(describeFailure(e).message);
      }
    } finally {
      setExportBusy(false);
    }
  }

  const rawWeightRetained = exported?.raw_weight_bytes_retained ?? 0;

  return (
    <div className={styles.page}>
      <header className={styles.hero}>
        <div>
          <h2>Foundry &amp; Recipe Studio</h2>
          <Muted>
            Compile structural <code>.recipe</code> architectures and synthesize CPU Mold-a-Model{' '}
            <code>laplace-blake3-v1</code> GGUF / Safetensors checkpoints from witnessed substrate trajectories.
          </Muted>
        </div>
        <div className={styles.badges}>
          <Badge>workspace: {tenant}</Badge>
          <Chip variant="engineOk">raw_weight_bytes_retained = {String(rawWeightRetained)}</Chip>
        </div>
      </header>

      <div className={styles.grid}>
        <Panel title="Recipe Studio (.recipe)">
          <div className={styles.stack}>
            <Field label="Starter template">
              <Select
                aria-label="Starter recipe template"
                onChange={(event) => {
                  const found = STARTER_RECIPES.find((r) => r.id === event.target.value);
                  if (found) setRecipeText(found.json);
                }}
              >
                {STARTER_RECIPES.map((r) => (
                  <option key={r.id} value={r.id}>{r.label}</option>
                ))}
              </Select>
            </Field>
            <Field label="Recipe definition (JSON)" help="Sent directly to POST /v1/recipe/compile.">
              <TextArea
                rows={12}
                value={recipeText}
                onChange={(e) => setRecipeText(e.target.value)}
                spellCheck={false}
              />
            </Field>
            <div className={styles.actions}>
              <Button onClick={() => void onCompileRecipe()} loading={compileBusy} disabled={compileBusy || !recipeText.trim()}>
                Compile recipe
              </Button>
              <Button variant="ghost" onClick={() => void onQuoteRecipe()} disabled={compileBusy}>
                Preflight recipe quote
              </Button>
            </div>
            {compileErr && <ErrorText role="alert">{compileErr}</ErrorText>}
            {recipeQuote && (
              <Banner>
                Quote <code>{recipeQuote.quote_id}</code> · {(Number(recipeQuote.amount_cents) / 100).toFixed(2)} {recipeQuote.currency} · {recipeQuote.status}
              </Banner>
            )}
            {compiled && (
              <dl className={styles.receipt}>
                <div><dt>Recipe ID (BLAKE3)</dt><dd><code>{compiled.recipe_id_hex}</code></dd></div>
                <div><dt>Name</dt><dd>{compiled.name}</dd></div>
                <div><dt>Structure</dt><dd>{compiled.structure}</dd></div>
                <div><dt>Hidden / Layers</dt><dd>{compiled.hidden_size} · {compiled.num_layers} layers</dd></div>
                <div><dt>Compile mode</dt><dd>{compiled.compile_mode}</dd></div>
              </dl>
            )}
          </div>
        </Panel>

        <Panel title="Mold-a-Model Export (laplace-blake3-v1)">
          <div className={styles.stack}>
            <div className={styles.row2}>
              <Field label="Export format">
                <Select aria-label="Export format" value={format} onChange={(e) => {
                  const next = e.target.value as 'gguf' | 'safetensors';
                  setFormat(next);
                  if (filename === 'laplace-blake3-v1.gguf' || filename === 'laplace-blake3-v1.safetensors') {
                    setFilename(`laplace-blake3-v1.${next}`);
                  }
                }}>
                  <option value="gguf">GGUF (laplace-blake3-v1 · llama.cpp compatible)</option>
                  <option value="safetensors">Safetensors</option>
                </Select>
              </Field>
              <Field label="Output filename">
                <Input value={filename} onChange={(e) => setFilename(e.target.value)} placeholder="laplace-blake3-v1.gguf" />
              </Field>
            </div>

            <div className={styles.row2}>
              <Field label="Compiled recipe ID prefix" help="Optional: leave blank to use the Recipe JSON on the left.">
                <Input value={recipeIdPrefix} onChange={(e) => setRecipeIdPrefix(e.target.value)} placeholder="optional 32-hex prefix" />
              </Field>
              <Field label="Tokenizer directory" help="Optional path on server when exporting by recipe_id_prefix.">
                <Input value={tokenizerDir} onChange={(e) => setTokenizerDir(e.target.value)} placeholder="optional server directory" />
              </Field>
            </div>

            <details className={styles.quoteDims}>
              <summary>Architecture dimensions for synthesis quote</summary>
              <div className={styles.dimsGrid}>
                <Field label="Vocab"><Input type="number" value={vocabSize} onChange={(e) => setVocabSize(e.target.value)} /></Field>
                <Field label="Hidden"><Input type="number" value={hiddenSize} onChange={(e) => setHiddenSize(e.target.value)} /></Field>
                <Field label="Layers"><Input type="number" value={numLayers} onChange={(e) => setNumLayers(e.target.value)} /></Field>
                <Field label="Heads"><Input type="number" value={numHeads} onChange={(e) => setNumHeads(e.target.value)} /></Field>
                <Field label="KV heads"><Input type="number" value={numKvHeads} onChange={(e) => setNumKvHeads(e.target.value)} /></Field>
                <Field label="Intermediate"><Input type="number" value={intermediateSize} onChange={(e) => setIntermediateSize(e.target.value)} /></Field>
              </div>
              <Checkbox checked={tiedEmbeddings} onChange={(e) => setTiedEmbeddings(e.target.checked)} label="Tied embeddings" />
            </details>

            <div className={styles.actions}>
              <Button onClick={() => void onExportSynthesis()} loading={exportBusy} disabled={exportBusy}>
                Export Mold-a-Model checkpoint
              </Button>
              <Button variant="ghost" onClick={() => void onQuoteSynthesis()} disabled={exportBusy}>
                Preflight synthesis quote
              </Button>
            </div>

            {exportErr && <ErrorText role="alert">{exportErr}</ErrorText>}
            {synthQuote && (
              <Banner>
                Quote <code>{synthQuote.quote_id}</code> · {Number(synthQuote.estimated_parameters).toLocaleString()} est. params ·{' '}
                {(Number(synthQuote.amount_cents) / 100).toFixed(2)} {synthQuote.currency} · {synthQuote.status}
              </Banner>
            )}

            {exported && (
              <div className={styles.stack}>
                <dl className={styles.receipt}>
                  <div><dt>Export ID</dt><dd><code>{exported.id}</code></dd></div>
                  <div><dt>Format</dt><dd><code>{exported.format}</code></dd></div>
                  <div><dt>Output path</dt><dd><code>{exported.output_path}</code></dd></div>
                  <div><dt>Bytes written</dt><dd>{Number(exported.bytes).toLocaleString()} bytes</dd></div>
                  <div><dt>raw_weight_bytes_retained</dt><dd><code>{String(rawWeightRetained)}</code> (trajectory-derived, zero raw weights)</dd></div>
                  {exported.n_vocab != null && <div><dt>Open Merkle vocab</dt><dd>{Number(exported.n_vocab).toLocaleString()} entries</dd></div>}
                  {exported.n_edges != null && <div><dt>Consensus edges</dt><dd>{Number(exported.n_edges).toLocaleString()}</dd></div>}
                </dl>
                {exported.billing?.royalties && exported.billing.royalties.length > 0 && (
                  <Table>
                    <thead>
                      <tr><Th>Source</Th><Th>Owner</Th><Th>share_bp</Th><Th>Hits</Th><Th>Royalty (cents)</Th></tr>
                    </thead>
                    <tbody>
                      {exported.billing.royalties.map((r) => (
                        <tr key={r.source_id}>
                          <Td><code>{r.source_id}</code></Td>
                          <Td>{r.source_owner_tenant ?? '—'}</Td>
                          <Td>{r.share_bp} / 10000</Td>
                          <Td>{Number(r.hit_count).toLocaleString()}</Td>
                          <Td>{Number(r.royalty_cents)}</Td>
                        </tr>
                      ))}
                    </tbody>
                  </Table>
                )}
              </div>
            )}
          </div>
        </Panel>
      </div>
    </div>
  );
}
