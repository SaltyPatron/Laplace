// The layer order first, before any module brings its own styles: a cascade layer ranks
// where it first appears in the bundle, so a component stylesheet imported ahead of this
// put `components` below `base`, and the base link colour painted every button-link teal
// on its teal fill ("View in mesh", "Prove storage").
import '@ui/layers.css';
import '@ui/theme.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { TooltipProvider } from '@ui';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <TooltipProvider>
      <App />
    </TooltipProvider>
  </StrictMode>,
);
