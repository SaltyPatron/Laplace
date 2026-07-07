import type { Meta, StoryObj } from '@storybook/react';
import { Badge } from '../Badge';
import { Chip } from '../Chip';
import { Input } from '../Input';
import { Muted } from '../Text';

const meta: Meta = {
  title: 'Tier1/Primitives',
};

export default meta;

export const Badges: StoryObj = {
  render: () => (
    <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
      <Badge>default</Badge>
      <Badge variant="mu">μ 1420</Badge>
      <Badge variant="ord">ord 3</Badge>
      <Chip variant="confirm">confirm</Chip>
      <Chip variant="refute">refute</Chip>
      <Chip variant="draw">draw</Chip>
    </div>
  ),
};

export const Inputs: StoryObj = {
  render: () => (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', maxWidth: '16rem' }}>
      <Input placeholder="entity id" />
      <Input invalid defaultValue="bad" />
      <Muted>Muted helper text</Muted>
    </div>
  ),
};
