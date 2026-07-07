import { Badge } from '../../primitives/Badge';
import { Tooltip, TooltipContent, TooltipTrigger } from '../../primitives/Tooltip';

export interface ConsensusBadgeProps {
  mu?: number;
  witnesses?: number;
  ordUsed?: number;
  /** Chat provenance uses 3 decimals + compact witness suffix; explore uses 1 decimal. */
  tone?: 'chat' | 'explore';
}

function formatLabel(mu: number | undefined, witnesses: number | undefined, tone: 'chat' | 'explore'): string {
  const parts: string[] = [];
  if (mu !== undefined) parts.push(`μ ${mu.toFixed(tone === 'chat' ? 3 : 1)}`);
  if (witnesses !== undefined) {
    parts.push(tone === 'chat' ? `${witnesses}w` : `${witnesses} wit`);
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
          eff_mu {mu} — Glicko-2 95% confidence lower bound; {witnesses ?? 0} witnesses
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
