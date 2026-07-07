import type { StorybookConfig } from '@storybook/react-vite';
import path from 'node:path';

const config: StorybookConfig = {
  stories: [
    '../src/ui/**/*.stories.@(ts|tsx)',
    '../src/explore/entity/tabs/*.stories.@(ts|tsx)',
  ],
  addons: ['@storybook/addon-essentials', '@storybook/addon-a11y'],
  core: {
    disableTelemetry: true,
  },
  framework: {
    name: '@storybook/react-vite',
    options: {},
  },
  viteFinal: async (config) => {
    config.resolve ??= {};
    config.resolve.alias = {
      ...config.resolve.alias,
      '@ui': path.resolve(__dirname, '../src/ui'),
    };
    return config;
  },
};

export default config;
