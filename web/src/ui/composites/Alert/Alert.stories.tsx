import type { Meta, StoryObj } from '@storybook/react';
import { Alert } from '../Alert';
import { Banner } from '../Banner';
import { LookupRow } from '../LookupRow';

const meta: Meta = {
  title: 'Tier3/Composites',
};

export default meta;

export const AlertError: StoryObj = {
  render: () => <Alert>Engine not found</Alert>,
};

export const BannerInfo: StoryObj = {
  render: () => <Banner>Quote required before sending — approve in chat toolbar.</Banner>,
};

export const Lookup: StoryObj = {
  render: () => (
    <LookupRow value="dog" onChange={() => {}} onSubmit={() => {}} placeholder="entity id" />
  ),
};
