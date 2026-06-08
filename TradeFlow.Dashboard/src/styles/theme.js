// -- Palette --

export const B = {
  bg:   '#0d1117',
  card: '#161b22',
  cd2:  '#1c2128',
  bd:   '#30363d',
  bd2:  '#21262d',
  tx:   '#e6edf3',
  mu:   '#8b949e',
  mu2:  '#484f58',
  gr:   '#3fb950',
  rd:   '#f85149',
  am:   '#d29922',
  bl:   '#58a6ff',
};

export const REGIME_COLORS = {
  Bullish: { c: '#3fb950', bg: 'rgba(63,185,80,0.1)',  bd: 'rgba(63,185,80,0.3)'  },
  Choppy:  { c: '#d29922', bg: 'rgba(210,153,34,0.1)', bd: 'rgba(210,153,34,0.3)' },
  Bearish: { c: '#f85149', bg: 'rgba(248,81,73,0.1)',  bd: 'rgba(248,81,73,0.3)'  },
};

export const OUTCOME_COLORS = {
  TargetHit:   { c: '#3fb950', label: 'TARGET'  },
  StoppedOut:  { c: '#f85149', label: 'STOPPED' },
  ExitAlert:   { c: '#58a6ff', label: 'EXIT'    },
  ForceClosed: { c: '#d29922', label: 'FORCED'  },
};

// -- Helpers --

export const pnlColor = v => v >= 0 ? B.gr : B.rd;
export const dirColor = d => d === 'call' ? B.gr : B.rd;

export const fmtDollar = v =>
  v == null ? '$0' : '$' + Math.abs(v).toLocaleString('en-US', { maximumFractionDigits: 0 });

export const fmtDollarCents = v =>
  '$' + Math.abs(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export const fmtPct = v => v == null ? '—' : (v >= 0 ? '+' : '') + v.toFixed(1) + '%';

export const effectiveBudget = (sizingPct, budget) =>
  Math.round(budget * sizingPct / 100);

export const fmtTime = iso => {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleTimeString('en-US', {
      hour: 'numeric',
      minute: '2-digit',
      hour12: true,
      timeZone: 'America/New_York',
    }) + ' ET';
  } catch {
    return String(iso);
  }
};
