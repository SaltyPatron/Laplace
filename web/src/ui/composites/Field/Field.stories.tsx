import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import { Input } from '../../primitives/Input';
import { SliderField } from '../../primitives/SliderField';
import { Field } from './Field';

const meta: Meta<typeof Field> = {
  title: 'Tier3/Field',
  component: Field,
};

export default meta;

export const WithHelp: StoryObj = {
  render: () => (
    <Field label="Temperature" help="Sampling temperature for engine">
      <Input defaultValue="0.7" />
    </Field>
  ),
};

export const WithSlider: StoryObj = {
  render: () => {
    const [v, setV] = useState('4');
    return (
      <Field label="depth" help="Search depth" valueDisplay={v}>
        <SliderField min={1} max={12} value={v} onChange={setV} />
      </Field>
    );
  },
};
