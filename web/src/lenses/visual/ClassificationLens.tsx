import { TaxonomyView } from '../../world/TaxonomyView';

/** Broader classes and members (is-a), as recorded by sources. */
export default function ClassificationLens({ id }: { id: string }) {
  return <TaxonomyView id={id} />;
}
