import { expect, test } from '@playwright/test';
import { formatCents, formatCurrencyCents } from '../src/billing/amounts';

test('billing cents preserve numeric and serialized integer values', () => {
  for (const value of [2900, '2900']) {
    expect(formatCents(value)).toBe('29.00');
    expect(formatCurrencyCents(value, 'usd', 'en-US')).toBe('$29.00');
  }
  expect(formatCents(0)).toBe('0.00');
  expect(formatCurrencyCents('1', 'USD', 'en-US')).toBe('$0.01');
  expect(formatCurrencyCents('-1', 'USD', 'en-US')).toBe('-$0.01');
  expect(formatCurrencyCents('-101', 'EUR', 'de-DE')).toBe('-1,01 €');
});

test('billing cents retain full Int64 precision and both limits', () => {
  expect(formatCents('9223372036854775807')).toBe('92233720368547758.07');
  expect(formatCents('-9223372036854775808')).toBe('-92233720368547758.08');
  expect(formatCurrencyCents('9223372036854775807', 'USD', 'en-US'))
    .toBe('$92,233,720,368,547,758.07');
  expect(formatCurrencyCents('-9223372036854775808', 'USD', 'en-US'))
    .toBe('-$92,233,720,368,547,758.08');
  expect(formatCents(Number.MAX_SAFE_INTEGER)).toBe('90071992547409.91');
});

test('billing cents do not convert missing invalid or inexact input to a price', () => {
  for (const value of [null, undefined, '', ' ', '1.5', '1e3', 'NaN',
    '9223372036854775808', '-9223372036854775809',
    1.5, Number.NaN, Number.POSITIVE_INFINITY, Number.MAX_SAFE_INTEGER + 1]) {
    expect(formatCents(value)).toBe('—');
    expect(formatCurrencyCents(value, 'USD', 'en-US')).toBe('—');
  }
});

test('billing cents keep exact fallback text and fractions independent of currency defaults', () => {
  expect(formatCurrencyCents('9223372036854775807', 'invalid', 'en-US'))
    .toBe('92233720368547758.07 INVALID');
  expect(formatCurrencyCents('101', 'JPY', 'en-US')).toBe('¥1.01');
});
