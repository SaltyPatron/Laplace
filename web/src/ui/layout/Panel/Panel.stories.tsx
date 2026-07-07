import type { Meta, StoryObj } from '@storybook/react';
import { Button } from '../../primitives/Button';
import { Muted } from '../../primitives/Text';
import { Panel } from './Panel';

const meta: Meta<typeof Panel> = {
  title: 'Tier4/Panel',
  component: Panel,
};

export default meta;

export const Default: StoryObj = {
  render: () => (
    <Panel title="Learned PST" actions={<Button size="sm" variant="ghost">Refresh</Button>}>
      <Muted>Panel body content</Muted>
    </Panel>
  ),
};
