import type { Preview } from '@storybook/react';
import '../src/ui/layers.css';
import '../src/ui/theme.css';
import { TooltipProvider } from '../src/ui';

const preview: Preview = {
  decorators: [
    (Story) => (
      <TooltipProvider>
        <div style={{ padding: '1.5rem', background: 'var(--color-bg-surface)', minHeight: '100vh' }}>
          <Story />
        </div>
      </TooltipProvider>
    ),
  ],
  parameters: {
    a11y: { test: 'todo' },
    controls: { matchers: { color: /(background|color)$/i, date: /Date$/i } },
  },
};

export default preview;
