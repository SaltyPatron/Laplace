import { MeshView } from '../../explore/mesh/MeshView';

/** Parent relations and members around this subject. */
export default function StructureLens({ id }: { id: string }) {
  return <MeshView selectedId={id} embedded />;
}
