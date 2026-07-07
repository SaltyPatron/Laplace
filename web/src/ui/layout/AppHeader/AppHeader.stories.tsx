import type { Meta, StoryObj } from '@storybook/react';
import { AppHeader } from '../AppHeader';
import { NavTabs } from '../NavTabs';
import { TenantField } from '../TenantField';
import { Panel } from '../Panel';
import { Stack } from '../Stack';

const meta: Meta = {
  title: 'Tier4/Layout',
};

export default meta;

export const HeaderShell: StoryObj = {
  render: () => (
    <AppHeader
      title="Laplace"
      tagline="witnessed consensus, not weights"
      nav={
        <NavTabs
          tabs={[
            { id: 'chat', label: 'Chat', active: true, onClick: () => {} },
            { id: 'lab', label: 'Lab', onClick: () => {} },
          ]}
        />
      }
      tenant={<TenantField value="local-dev" onChange={() => {}} />}
    />
  ),
};

export const PanelStack: StoryObj = {
  render: () => (
    <Stack gap={4}>
      <Panel title="Learned PST">Panel body</Panel>
      <Panel title="Engine">Second panel</Panel>
    </Stack>
  ),
};
