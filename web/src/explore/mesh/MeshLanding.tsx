import { useEffect } from 'react';
import { Muted } from '@ui';
import { Link } from 'react-router-dom';
import { SearchBar } from '../components/SearchBar';
import { useExploreStore } from '../store';
import { ModalityMap } from './ModalityMap';
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
  const resetMeshTrail = useExploreStore((s) => s.resetMeshTrail);

  useEffect(() => resetMeshTrail(), [resetMeshTrail]);

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
          <SearchBar
            placeholder="a word, sense, frame, or id hex…"
            label="Enter the mesh at any witnessed node"
            hint="Names, words, semantic IDs, and close spellings work."
            destination="mesh"
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
            <Link key={w} className={styles.exampleChip}
              to={`/explore/resolve/${encodeURIComponent(w)}`}>
              {w}
            </Link>
          ))}
        </div>
      </div>

      <ModalityMap />
    </div>
  );
}
