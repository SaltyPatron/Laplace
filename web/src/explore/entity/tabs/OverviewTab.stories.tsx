import type { Meta, StoryObj } from '@storybook/react';
import { OverviewTab } from './OverviewTab';
import type { ExploreEntityResponse } from '../../types';

const mockEntity: ExploreEntityResponse = {
  id_hex: 'a'.repeat(32),
  label: 'dog',
  tier: 2,
  type: 'word',
  exists: true,
  evidence_count: 42,
  salient_facts: [
    { type: 'IsA', fact: 'animal', eff_mu: 1850.2, witnesses: 12 },
    { type: 'CapableOf', fact: 'bark', eff_mu: 1720.5, witnesses: 8 },
  ],
  senses: [
    { sense_id_hex: 'b'.repeat(32), synset_id_hex: 'c'.repeat(32), synset_label: 'dog.n.01', eff_mu: 1900, witnesses: 15 },
  ],
  constituents: [],
  physicalities: [],
  consensus_out: [],
  consensus_in: [],
  evidence: [],
};

const meta: Meta<typeof OverviewTab> = {
  title: 'Explore/Entity/OverviewTab',
  component: OverviewTab,
  parameters: { layout: 'padded' },
};

export default meta;
type Story = StoryObj<typeof OverviewTab>;

export const Default: Story = {
  args: { entity: mockEntity },
};

export const Empty: Story = {
  args: {
    entity: { ...mockEntity, salient_facts: [], senses: [] },
  },
};
