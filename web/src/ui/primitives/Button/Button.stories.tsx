import type { Meta, StoryObj } from '@storybook/react';
import { Button, IconButton } from './Button';
import { Tooltip, TooltipContent, TooltipTrigger } from '../Tooltip';

const meta: Meta<typeof Button> = {
  title: 'Tier1/Button',
  component: Button,
  tags: ['autodocs'],
};

export default meta;
type Story = StoryObj<typeof Button>;

export const Primary: Story = { args: { children: 'Start experiment' } };
export const Ghost: Story = { args: { variant: 'ghost', children: 'Stop' } };
export const NavActive: Story = { args: { variant: 'nav', active: true, children: 'Lab' } };
export const Disabled: Story = { args: { disabled: true, children: 'Disabled' } };
export const Loading: Story = { args: { loading: true, children: 'Running…' } };

export const DisabledWithTooltip: Story = {
  render: () => (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button visuallyDisabled>Start</Button>
      </TooltipTrigger>
      <TooltipContent>needs stockfish, cutechess</TooltipContent>
    </Tooltip>
  ),
};

export const Icon: Story = {
  render: () => (
    <Tooltip>
      <TooltipTrigger asChild>
        <IconButton aria-label="Pawn piece">P</IconButton>
      </TooltipTrigger>
      <TooltipContent>Pawn</TooltipContent>
    </Tooltip>
  ),
};
