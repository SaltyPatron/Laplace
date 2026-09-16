type CentValue = number | string | null | undefined;

// Billing wire fields are Int64 cents. Unsafe JSON numbers have already lost
// information; only their exact string representation can be displayed safely.
function integerCents(value: CentValue): bigint | null {
  if (typeof value === 'number') {
    if (!Number.isSafeInteger(value)) return null;
  } else if (typeof value !== 'string' || !/^[+-]?\d+$/.test(value)) {
    return null;
  }
  const cents = BigInt(value);
  return cents >= -9223372036854775808n && cents <= 9223372036854775807n ? cents : null;
}

function decimal(cents: bigint): string {
  const absolute = cents < 0n ? -cents : cents;
  return `${cents < 0n ? '-' : ''}${absolute / 100n}.${String(absolute % 100n).padStart(2, '0')}`;
}

export function formatCents(value: CentValue): string {
  const cents = integerCents(value);
  return cents === null ? '—' : decimal(cents);
}

export function formatCurrencyCents(
  value: CentValue, currency: string | null | undefined, locale?: string,
): string {
  const cents = integerCents(value);
  if (cents === null) return '—';
  const code = currency?.toUpperCase() || 'USD';
  try {
    // Preserve every cent, including for currencies whose default display
    // omits fractions. All billing fields here use a denominator of 100.
    const formatter = new Intl.NumberFormat(locale, {
      style: 'currency', currency: code, minimumFractionDigits: 2, maximumFractionDigits: 2,
    });
    const whole = cents / 100n;
    const fraction = new Intl.NumberFormat(locale, {
      useGrouping: false, minimumIntegerDigits: 2, maximumFractionDigits: 0,
    }).format(Number((cents < 0n ? -cents : cents) % 100n));
    return formatter.formatToParts(cents < 0n && whole === 0n ? -0 : whole)
      .map((part) => part.type === 'fraction' ? fraction : part.value).join('');
  } catch {
    return `${decimal(cents)} ${code}`;
  }
}
