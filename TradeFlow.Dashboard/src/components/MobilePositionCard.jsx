import { B, dirColor, fmtDollar } from '../styles/theme.js';
import { Pill } from './shared/index.js';

export function MobilePositionCard({ position, onClose }) {
  const { contract, direction, quantity, entryPrice, costBasis, stopPrice, targetPrice, trailPct, openedAt, trader, xScore } = position;
  const stopPct   = ((stopPrice   - entryPrice) / entryPrice * 100).toFixed(0);
  const targetPct = ((targetPrice - entryPrice) / entryPrice * 100).toFixed(0);

  const columns = [
    { label: 'Entry',  value: `$${entryPrice.toFixed(2)}`,  color: B.tx, sub: null          },
    { label: 'Stop',   value: `$${stopPrice.toFixed(2)}`,   color: B.rd, sub: `${stopPct}%` },
    { label: 'Target', value: `$${targetPrice.toFixed(2)}`, color: B.gr, sub: `+${targetPct}%` },
  ];

  return (
    <div style={{ background: B.card, border: `1px solid ${B.bd}`, borderRadius: 10, overflow: 'hidden', marginBottom: 10 }}>
      <div style={{ padding: '12px 14px 10px', borderBottom: `1px solid ${B.bd2}`, display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
          <Pill color={dirColor(direction)} style={{ fontSize: 11, padding: '2px 7px' }}>
            {direction === 'call' ? 'CALL' : 'PUT'}
          </Pill>
          <span style={{ fontSize: 15, fontWeight: 700, color: B.tx }}>{contract}</span>
        </div>
        <span style={{ fontSize: 12, color: B.mu2, fontWeight: 500 }}>×{quantity}</span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', borderBottom: `1px solid ${B.bd2}` }}>
        {columns.map((col, i) => (
          <div key={col.label} style={{
            padding: '10px 12px',
            borderRight: i < 2 ? `1px solid ${B.bd2}` : 'none',
            textAlign: i === 0 ? 'left' : i === 1 ? 'center' : 'right',
          }}>
            <div style={{ fontSize: 10, color: B.mu2, marginBottom: 3 }}>{col.label}</div>
            <div style={{ fontSize: 15, fontWeight: 700, color: col.color }}>{col.value}</div>
            {col.sub && <div style={{ fontSize: 10, color: col.color, marginTop: 1, opacity: 0.8 }}>{col.sub}</div>}
          </div>
        ))}
      </div>

      <div style={{ padding: '10px 14px', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <div>
          <div style={{ fontSize: 12, color: B.tx, fontWeight: 500 }}>
            {trader} <span style={{ color: B.mu }}>xs{xScore}</span>
          </div>
          <div style={{ fontSize: 10, color: B.mu, marginTop: 1 }}>
            {fmtDollar(costBasis)} cost · Trail {trailPct}% · {openedAt}
          </div>
        </div>
        <button
          onClick={() => onClose(position)}
          className="btn-close"
          style={{
            padding: '9px 16px',
            borderRadius: 6,
            fontSize: 11,
            fontWeight: 700,
            color: B.rd,
            background: 'rgba(248,81,73,0.08)',
            border: `1px solid rgba(248,81,73,0.32)`,
            minWidth: 72,
            transition: 'background 0.1s',
          }}
        >
          CLOSE
        </button>
      </div>
    </div>
  );
}
