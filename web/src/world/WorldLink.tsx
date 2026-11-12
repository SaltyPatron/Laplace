import { createContext, useContext, type ReactNode } from 'react';
import { Link, useLocation, type LinkProps } from 'react-router-dom';
import { selectWorldEntity } from './context';

const EmbeddedWorld = createContext(false);
export function WorldProjection({ children }: { children: ReactNode }) {
  return <EmbeddedWorld.Provider value>{children}</EmbeddedWorld.Provider>;
}

/** Domain reference routes become typed pivots when rendered in a world. */
export function WorldLink({ to, ...props }: LinkProps) {
  const embedded = useContext(EmbeddedWorld), location = useLocation();
  const match = typeof to === 'string' ? to.match(/^\/chess\/games\/([a-f0-9]{32})$/i) : null;
  const entity = typeof to === 'string' ? to.match(/^\/explore\/entity\/([a-f0-9]{32})$/i) : null;
  const mesh = typeof to === 'string' ? to.match(/^\/explore\/mesh\/([a-f0-9]{32})$/i) : null;
  const destination = embedded && match ? selectWorldEntity(match[1], location, 'chess-game')
    : embedded && entity ? selectWorldEntity(entity[1], location, 'knowledge')
      : embedded && mesh ? selectWorldEntity(mesh[1], location, 'mesh') : to;
  return <Link {...props} to={destination} preventScrollReset={embedded || props.preventScrollReset} />;
}
