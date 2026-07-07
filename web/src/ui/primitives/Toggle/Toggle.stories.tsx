import type { Meta, StoryObj } from '@storybook/react';
import { useState } from 'react';
import { Field } from '../../composites/Field';
import { Input } from '../Input';
import { Toggle } from '../Toggle';
import { SegmentedControl } from '../SegmentedControl';
import { SliderField } from '../SliderField';

const meta: Meta = {
  title: 'Tier2/Controls',
};

export default meta;

export const ToggleField: StoryObj = {
  render: () => {
    const [on, setOn] = useState(false);
    return (
      <Field label="opening book" help="seed games from the opening set" layout="row">
        <Toggle checked={on} onCheckedChange={setOn} />
      </Field>
    );
  },
};

export const SegmentedField: StoryObj = {
  render: () => {
    const [mode, setMode] = useState('fold');
    return (
      <Field label="bias mode" help="substrate root-bias source">
        <SegmentedControl value={mode} onValueChange={setMode} options={['fold', 'edge', 'off']} label="bias mode" />
      </Field>
    );
  },
};

export const SliderFieldStory: StoryObj = {
  name: 'SliderField',
  render: () => {
    const [v, setV] = useState('4');
    return (
      <Field label="depth" help="search depth" valueDisplay={v}>
        <SliderField min={1} max={12} value={v} onChange={setV} />
      </Field>
    );
  },
};

export const TextField: StoryObj = {
  render: () => (
    <Field label="PGN path" help="server-side path to a .pgn file">
      <Input type="text" defaultValue="/data/games.pgn" />
    </Field>
  ),
};
