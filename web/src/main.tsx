import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { TooltipProvider } from '@ui';
import '@ui/layers.css';
import '@ui/theme.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <TooltipProvider>
      <App />
    </TooltipProvider>
  </StrictMode>,
);
