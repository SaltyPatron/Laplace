export function entityDestination(id: string, location: { pathname: string; search: string }): string {
  const params = new URLSearchParams(location.search);
  if (location.pathname === '/explore/sources') {
    params.set('entity', id);
    return `${location.pathname}?${params}`;
  }
  if (location.pathname === '/explore' || location.pathname === '/explore/warehouse' || location.pathname === '/query') {
    return selectWorldEntity(id, location);
  }
  return `/explore/entity/${id}`;
}
import { selectWorldEntity } from '../../world/context';

