import type { Meta, StoryObj } from '@storybook/react';
import { Button } from '../Button';
import { Tooltip, TooltipContent, TooltipTrigger } from './Tooltip';

const meta: Meta = {
  title: 'Tier2/Tooltip',
};

export default meta;

export const Default: StoryObj = {
  render: () => (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button variant="ghost">Hover or focus me</Button>
      </TooltipTrigger>
      <TooltipContent>Glicko-2 conservative estimate (eff_mu)</TooltipContent>
    </Tooltip>
  ),
};

export const DisabledWithReason: StoryObj = {
  render: () => (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button visuallyDisabled>Start</Button>
      </TooltipTrigger>
      <TooltipContent>needs cutechess, stockfish</TooltipContent>
    </Tooltip>
  ),
};
