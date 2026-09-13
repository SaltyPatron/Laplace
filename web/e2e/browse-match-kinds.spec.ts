import { test, expect } from '@playwright/test';

test('Browse explains constituent and contains-all structural hits', async ({ page }) => {
  await page.route('**/v1/explore/browse?**', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        object: 'laplace.explore.browse',
        query: 'Sodium Chloride',
        hits: [
          {
            id_hex: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            label: 'Sodium',
            tier: 2,
            type: 'Word',
            matched_name_id_hex: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            match_kind: 'constituent',
            rating: null,
            rd: null,
            eff_mu: null,
            witnesses: 0,
          },
          {
            id_hex: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
            label: 'Chloride',
            tier: 2,
            type: 'Word',
            matched_name_id_hex: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
            match_kind: 'constituent',
            rating: null,
            rd: null,
            eff_mu: null,
            witnesses: 0,
          },
          {
            id_hex: 'cccccccccccccccccccccccccccccccc',
            label: 'Sodium chloride dissolves in water.',
            tier: 3,
            type: 'Sentence',
            matched_name_id_hex: 'cccccccccccccccccccccccccccccccc',
            match_kind: 'contains_all',
            rating: null,
            rd: null,
            eff_mu: null,
            witnesses: 0,
          },
        ],
        receipt: {
          query_root_id_hex: 'dddddddddddddddddddddddddddddddd',
          query_member_ids_hex: [
            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
          ],
          candidate_names: 1,
          candidate_capacity: 2048,
          candidate_truncated: false,
          matched_entities: 3,
          returned: 3,
          offset: 0,
          limit: 50,
          elapsed_us: 731,
        },
      }),
    });
  });

  await page.goto('/explore?q=Sodium%20Chloride');

  await expect(page.getByRole('link', { name: 'Sodium', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Chloride', exact: true })).toBeVisible();
  await expect(page.getByText('query constituent')).toHaveCount(2);
  await expect(page.getByText('contains all query words')).toBeVisible();
});
