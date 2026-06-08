import { B, dirColor, fmtDollar, fmtTime } from '../styles/theme.js';
import { Pill } from './shared/index.js';

export function PositionRow({ position, onClose, isLast }) {
  const { contract, direction, quantity, entryPrice, costBasis, stopPrice, targetPrice, trailPct, openedAt, trader, xScore, discordRank } = position;
  const stopPct  = ((stopPrice  - entryPrice) / entryPrice * 100).toFixed(0);
  const targetPct = ((targetPrice - entryPrice) / entryPrice * 100).toFixed(0);

  return (
    <div
      className="pos-row"
      style={{
        padding: '6px 10px',
        borderBottom: isLast ? 'none' : `1px solid ${B.bd2}`,
        transition: 'background 0.1s',
      }}
    >
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 5 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <Pill color={dirColor(direction)}>{direction === 'call' ? 'C' : 'P'}</Pill>
          <span style={{ fontSize: 13, fontWeight: 600, color: B.tx }}>{contract}</span>
          <span style={{ fontSize: 11, color: B.mu }}>×{quantity}</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 11, color: B.mu2 }}>{fmtDollar(costBasis)}</span>
          <button
            onClick={() => onClose(position)}
            className="btn-close"
            style={{
              padding: '3px 8px',
              borderRadius: 4,
              fontSize: 10,
              fontWeight: 700,
              color: B.rd,
              background: 'rgba(248,81,73,0.07)',
              border: `1px solid rgba(248,81,73,0.28)`,
              transition: 'background 0.1s',
            }}
          >
            CLOSE
          </button>
        </div>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(5, 1fr)', gap: 4, fontSize: 11 }}>
        <div>
          <div style={{ color: B.mu2 }}>Entry</div>
          <div style={{ color: B.tx, fontWeight: 500 }}>${entryPrice.toFixed(2)}</div>
        </div>
        <div>
          <div style={{ color: B.mu2 }}>Stop</div>
          <div style={{ color: B.rd, fontWeight: 500 }}>${stopPrice.toFixed(2)}</div>
          <div style={{ color: B.mu, fontSize: 10 }}>{stopPct}%</div>
        </div>
        <div>
          <div style={{ color: B.mu2 }}>Target</div>
          <div style={{ color: B.gr, fontWeight: 500 }}>${targetPrice.toFixed(2)}</div>
          <div style={{ color: B.mu, fontSize: 10 }}>+{targetPct}%</div>
        </div>
        <div>
          <div style={{ color: B.mu2 }}>Trail</div>
          <div style={{ color: B.mu }}>{trailPct}%</div>
        </div>
        <div style={{ textAlign: 'right' }}>
          <div style={{ color: B.mu2 }}>Opened</div>
          <div style={{ color: B.mu }}>{fmtTime(openedAt)}</div>
        </div>
      </div>

      <div style={{ marginTop: 5, fontSize: 10, color: B.mu }}>
        {trader} · xScore <span style={{ color: B.tx }}>{xScore}</span> · {discordRank}
      </div>
    </div>
  );
}
