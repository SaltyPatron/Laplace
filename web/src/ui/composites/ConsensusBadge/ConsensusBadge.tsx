import { Badge } from '../../primitives/Badge';
import { Tooltip, TooltipContent, TooltipTrigger } from '../../primitives/Tooltip';

export interface ConsensusBadgeProps {
  mu?: number;
  witnesses?: number;
  ordUsed?: number;
  /** Chat provenance uses 3 decimals + compact witness suffix; explore uses 1 decimal. */
  tone?: 'chat' | 'explore';
}

// The standing's witness_count is the number of matchups played into it, not of distinct
// witnesses: one source stating a link with a count of 547 plays 547 games. It is named
// what it is.
function formatLabel(mu: number | undefined, witnesses: number | undefined, tone: 'chat' | 'explore'): string {
  const parts: string[] = [];
  if (mu !== undefined) parts.push(`μ ${mu.toFixed(tone === 'chat' ? 3 : 1)}`);
  if (witnesses !== undefined) {
    parts.push(tone === 'chat' ? `${witnesses}g` : `${witnesses.toLocaleString()} ${witnesses === 1 ? 'game' : 'games'}`);
  }
  return parts.join(' · ');
}

export function ConsensusBadge({ mu, witnesses, ordUsed, tone = 'explore' }: ConsensusBadgeProps) {
  if (ordUsed !== undefined) {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Badge variant="ord">ord {ordUsed}</Badge>
        </TooltipTrigger>
        <TooltipContent>n-gram context order used for this token</TooltipContent>
      </Tooltip>
    );
  }

  if (mu === undefined && witnesses === undefined) return null;

  if (tone === 'chat' && mu !== undefined) {
    const clamped = Math.max(0, Math.min(1, mu));
    const hue = Math.round(clamped * 120);
    const label = formatLabel(mu, witnesses, tone);
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Badge
            variant="mu"
            style={{ borderColor: `hsl(${hue} 70% 45%)`, color: `hsl(${hue} 70% 65%)` }}
          >
            {label}
          </Badge>
        </TooltipTrigger>
        <TooltipContent>
          eff_mu {mu} — Glicko-2 95% confidence lower bound; {witnesses ?? 0} games played
        </TooltipContent>
      </Tooltip>
    );
  }

  return (
    <Badge
      variant="default"
      style={{
        borderColor: 'transparent',
        background: 'none',
        color: 'var(--color-accent)',
        fontSize: '0.78rem',
        marginLeft: '0.35rem',
        padding: 0,
      }}
    >
      {formatLabel(mu, witnesses, tone)}
    </Badge>
  );
}
