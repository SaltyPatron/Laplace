import { useEffect } from 'react';
import { Link, Navigate, useSearchParams } from 'react-router-dom';
import { changeWorld } from '../../world/context';
import { SearchBar } from '../components/SearchBar';
import { useExploreStore } from '../store';
import styles from './MeshView.module.css';

/** Historical entry address for the shared collection's relation presentation. */
export function MeshLanding() {
  const resetMeshTrail = useExploreStore((s) => s.resetMeshTrail);
  const [params] = useSearchParams();
  useEffect(() => resetMeshTrail(), [resetMeshTrail]);
  // A subject in the address goes straight to the shared search with the related-structure view.
  if (params.get('q')?.trim()) return <Navigate to={`/explore?${changeWorld(params, { presentation: 'mesh' })}`} replace />;

  return (
    <div className={styles.landing}>
      <header className={styles.landingHero}>
        <h2 className={styles.landingTitle}>Related structure</h2>
        <p className={styles.landingLede}>
          Search known content and select a subject to inspect its returned parent relations and members.
          Selecting a related subject keeps the search collection and its criteria in view.
        </p>
        <div className={styles.landingSearch}>
          <SearchBar
            label="Choose an entity, then inspect its relations"
            hint="Results use the shared knowledge search. Related structure changes the selected subject's presentation."
            destination="mesh"
          />
        </div>
      </header>
      <Link to={`/explore?${changeWorld(params, { selected: null, answer: null, presentation: 'mesh' })}`}>
        Return to the knowledge collection
      </Link>
    </div>
  );
}
