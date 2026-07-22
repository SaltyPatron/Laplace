import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { LookupRow, Muted } from '@ui';
import { exploreResolve } from '../api';
import { useExploreStore } from '../store';
import styles from './MeshView.module.css';

/**
 * The mesh front page. The divisions are a fixed structural vocabulary — the hub
 * types the factorization is built from — not a data-derived list (there are
 * 100k+ synsets; you don't page a team list that long, you enter the graph at a
 * node and drill). So this explains the ladder and drops you in via search.
 */
const DIVISIONS: { name: string; tag: string; blurb: string }[] = [
  { name: 'Word surface', tag: 'surface', blurb: 'The lemma you type. Every entry point into the mesh; it plays for its senses.' },
  { name: 'WordNet sense', tag: 'sense', blurb: 'A single reading of a word. Binds a surface to one concept.' },
  { name: 'ILI concept', tag: 'synset', blurb: 'The master hub — a synset addressed by its Interlingual Index. Where every language and source converge.' },
  { name: 'FrameNet frame', tag: 'frame', blurb: 'A scene a concept evokes, with roles. The verb-side of meaning.' },
  { name: 'VerbNet class', tag: 'class', blurb: 'A class of verbs sharing syntax and semantics.' },
  { name: 'PropBank roleset', tag: 'roleset', blurb: 'A predicate with numbered arguments. The proposition skeleton.' },
];

export function MeshLanding() {
  const nav = useNavigate();
  const resetMeshTrail = useExploreStore((s) => s.resetMeshTrail);
  const [ref, setRef] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => resetMeshTrail(), [resetMeshTrail]);

  async function enterMesh(term: string) {
    const t = term.trim();
    if (!t || busy) return;
    setBusy(true);
    setError(null);
    try {
      const hit = await exploreResolve(t);
      if (!hit) { setError(`Nothing witnessed for "${t}".`); return; }
      resetMeshTrail();
      nav(`/explore/mesh/${hit.id_hex}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={styles.landing}>
      <header className={styles.landingHero}>
        <h2 className={styles.landingTitle}>The mesh</h2>
        <p className={styles.landingLede}>
          Meaning here is factored, not flat: <strong>surface → lemma → sense → concept → frame /
          class / roleset → roles</strong>. Enter at any node and drill — a concept is a hub whose
          roster is its members; a word is a player whose teams are the hubs it plays for. Every
          arrow is a witnessed, rated edge.
        </p>
        <div className={styles.landingSearch}>
          <LookupRow
            value={ref}
            onChange={setRef}
            onSubmit={() => void enterMesh(ref)}
            placeholder="a word, sense, frame, or id hex…"
            submitLabel="Enter mesh"
            error={error}
            disabled={busy}
          />
        </div>
      </header>

      <div className={styles.divisions}>
        {DIVISIONS.map((d) => (
          <div key={d.tag} className={styles.division}>
            <span className={styles.divisionName}>{d.name}</span>
            <span className={styles.divisionTag}>{d.tag}</span>
            <span className={styles.divisionBlurb}>{d.blurb}</span>
          </div>
        ))}
      </div>

      <div className={styles.landingExamples}>
        <Muted>Or start from a familiar concept:</Muted>
        <div className={styles.exampleChips}>
          {['whale', 'run', 'gravity', 'justice', 'cell'].map((w) => (
            <button key={w} type="button" className={styles.exampleChip}
              onClick={() => void enterMesh(w)} disabled={busy}>
              {w}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
