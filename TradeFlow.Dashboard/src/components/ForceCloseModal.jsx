import { B, dirColor, fmtDollarCents } from '../styles/theme.js';
import { Pill } from './shared/index.js';

export function ForceCloseModal({ position, isMobile, onConfirm, onCancel }) {
  if (!position) return null;

  const { contract, direction, quantity, costBasis, trader, xScore, entryPrice } = position;
  const details = [
    { label: 'Contract',    value: contract,                   color: B.tx          },
    { label: 'Direction',   value: direction.toUpperCase(),    color: dirColor(direction) },
    { label: 'Quantity',    value: `${quantity} contracts`,    color: B.tx          },
    { label: 'Cost basis',  value: fmtDollarCents(costBasis),  color: B.tx          },
    { label: 'Trader',      value: trader,                     color: B.tx          },
    { label: 'Entry price', value: `$${entryPrice.toFixed(2)}`, color: B.tx         },
  ];

  const handleOverlayClick = e => {
    if (e.target === e.currentTarget) onCancel();
  };

  if (isMobile) {
    return (
      <div className="modal-overlay" onClick={handleOverlayClick}>
        <div className="modal-sheet">
          <div style={{ width: 36, height: 4, borderRadius: 2, background: B.bd, margin: '0 auto 20px' }} />
          <div style={{ fontSize: 11, fontWeight: 700, color: B.rd, letterSpacing: '0.07em', textTransform: 'uppercase', marginBottom: 4 }}>
            Force Close Position
          </div>
          <div style={{ fontSize: 13, color: B.mu, marginBottom: 16, lineHeight: 1.5 }}>
            This immediately closes at market price via IBKR. This cannot be undone.
          </div>
          <div style={{ background: B.cd2, borderRadius: 8, padding: 14, marginBottom: 20 }}>
            <div style={{ fontSize: 15, fontWeight: 700, color: B.tx, marginBottom: 4 }}>{contract}</div>
            <div style={{ fontSize: 13, color: B.mu, marginBottom: 6 }}>
              <Pill color={dirColor(direction)} style={{ marginRight: 6 }}>{direction.toUpperCase()}</Pill>
              {quantity} contracts · {fmtDollarCents(costBasis)} cost
            </div>
            <div style={{ fontSize: 12, color: B.mu }}>
              {trader} · xScore {xScore} · Entry ${entryPrice.toFixed(2)}
            </div>
          </div>
          <button onClick={onCancel} style={{ width: '100%', padding: 14, borderRadius: 8, fontSize: 14, fontWeight: 600, marginBottom: 10, background: 'transparent', color: B.mu, border: `1px solid ${B.bd}` }}>
            Cancel
          </button>
          <button onClick={onConfirm} style={{ width: '100%', padding: 16, borderRadius: 8, fontSize: 15, fontWeight: 700, background: 'rgba(248,81,73,0.12)', color: B.rd, border: `1px solid rgba(248,81,73,0.45)` }}>
            Force Close Position
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="modal-overlay-desk" onClick={handleOverlayClick}>
      <div style={{ background: B.card, border: `1px solid ${B.bd}`, borderRadius: 10, padding: 22, width: 340 }}>
        <div style={{ fontSize: 13, fontWeight: 700, color: B.rd, marginBottom: 5 }}>Confirm force close</div>
        <div style={{ fontSize: 12, color: B.mu, marginBottom: 16, lineHeight: 1.6 }}>
          This immediately closes the position at market price via IBKR. This cannot be undone.
        </div>
        <div style={{ background: B.cd2, borderRadius: 6, padding: 12, marginBottom: 16, display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px 12px' }}>
          {details.map(row => (
            <div key={row.label}>
              <div style={{ fontSize: 10, color: B.mu2, marginBottom: 2 }}>{row.label}</div>
              <div style={{ color: row.color, fontWeight: 600, fontSize: 12 }}>{row.value}</div>
            </div>
          ))}
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <button onClick={onCancel} style={{ flex: 1, padding: '8px 0', borderRadius: 5, fontSize: 12, fontWeight: 600, background: 'transparent', color: B.mu, border: `1px solid ${B.bd}` }}>
            Cancel
          </button>
          <button onClick={onConfirm} style={{ flex: 1, padding: '8px 0', borderRadius: 5, fontSize: 12, fontWeight: 700, background: 'rgba(248,81,73,0.1)', color: B.rd, border: `1px solid rgba(248,81,73,0.42)` }}>
            Force close
          </button>
        </div>
      </div>
    </div>
  );
}
