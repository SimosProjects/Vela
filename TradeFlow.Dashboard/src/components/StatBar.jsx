import { B, pnlColor, fmtDollar } from '../styles/theme.js';

export function StatBar({ account }) {
  const { balance, openValue, exposurePct, dailyPnl, deployable } = account;
  const stats = [
    { label: 'Account Balance', value: `$${balance.toLocaleString()}`, color: B.tx },
    { label: "Today's P&L",     value: (dailyPnl >= 0 ? '+' : '') + fmtDollar(dailyPnl), color: pnlColor(dailyPnl), sub: '3 trades closed' },
    { label: 'Exposure',        value: `${exposurePct.toFixed(1)}%`, color: B.bl, sub: `${fmtDollar(openValue)} deployed` },
    { label: 'Deployable',      value: fmtDollar(deployable), color: B.mu },
  ];

  return (
    <div style={{
      display: 'grid',
      gridTemplateColumns: 'repeat(4, 1fr)',
      gap: '0.5px',
      background: B.bd,
      borderBottom: `1px solid ${B.bd}`,
      flexShrink: 0,
    }}>
      {stats.map(s => (
        <div key={s.label} style={{ background: B.card, padding: '10px 16px' }}>
          <div style={{ fontSize: 10, color: B.mu, marginBottom: 2 }}>{s.label}</div>
          <div style={{ fontSize: 18, fontWeight: 700, color: s.color, lineHeight: 1.2 }}>{s.value}</div>
          {s.sub && <div style={{ fontSize: 10, color: B.mu, marginTop: 2 }}>{s.sub}</div>}
        </div>
      ))}
    </div>
  );
}
