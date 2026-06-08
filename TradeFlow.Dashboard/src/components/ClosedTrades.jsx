import { B, pnlColor, dirColor, fmtDollar, fmtDollarCents, fmtPct, OUTCOME_COLORS, fmtTime } from '../styles/theme.js';
import { Card, Pill } from './shared/index.js';

export function ClosedTrades({ trades }) {
  const net = trades.reduce((sum, t) => sum + t.pnl, 0);

  return (
    <Card noPad>
      <div style={{ padding: '10px 14px', borderBottom: `1px solid ${B.bd}`, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu }}>
          Closed Today ({trades.length})
        </div>
        <span style={{ fontSize: 14, fontWeight: 700, color: pnlColor(net) }}>
          {net >= 0 ? '+' : ''}{fmtDollar(net)} net
        </span>
      </div>

      {trades.map((t, i) => {
        const oc = OUTCOME_COLORS[t.outcome] ?? { c: B.mu, label: t.outcome };
        return (
          <div key={t.id} style={{ padding: '8px 14px', borderBottom: i === trades.length - 1 ? 'none' : `1px solid ${B.bd2}` }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 5 }}>
                <Pill color={dirColor(t.direction)}>{t.direction === 'call' ? 'C' : 'P'}</Pill>
                <span style={{ fontSize: 12, fontWeight: 600, color: B.tx }}>{t.contract}</span>
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 13, fontWeight: 700, color: pnlColor(t.pnl) }}>
                  {t.pnl >= 0 ? '+' : ''}{fmtDollarCents(t.pnl)}
                </span>
                <span style={{ fontSize: 11, color: pnlColor(t.pnlPct) }}>{fmtPct(t.pnlPct)}</span>
                <Pill color={oc.c} style={{ fontSize: 9 }}>{oc.label}</Pill>
              </div>
            </div>
            <div style={{ fontSize: 10, color: B.mu }}>
              {t.trader} · xs{t.xScore} · {t.quantity}× ${t.entryPrice?.toFixed(2) ?? '—'} → ${t.exitPrice?.toFixed(2) ?? '—'} · {fmtTime(t.closedAt)}
            </div>
          </div>
        );
      })}
    </Card>
  );
}
