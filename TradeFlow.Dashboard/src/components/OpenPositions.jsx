import { B } from '../styles/theme.js';
import { Card, Pill } from './shared/index.js';
import { PositionRow } from './PositionRow.jsx';
import { MobilePositionCard } from './MobilePositionCard.jsx';

export function OpenPositions({ positions, lastUpdated, onClose, isMobile = false }) {
  const updatedStr = lastUpdated
    ? lastUpdated.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }) + ' ET'
    : '—';

  return (
    <Card noPad>
      <div style={{ padding: '10px 14px', borderBottom: `1px solid ${B.bd}`, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu }}>
          Open Positions
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <Pill color={B.bl}>{positions.length} open</Pill>
          <span style={{ fontSize: 10, color: B.mu2 }}>↻ {updatedStr}</span>
        </div>
      </div>

      {positions.length === 0 && (
        <div style={{ padding: 32, textAlign: 'center', color: B.mu, fontSize: 13 }}>No open positions</div>
      )}

      {isMobile ? (
        <div style={{ padding: '10px 10px 2px' }}>
          {positions.map(p => (
            <MobilePositionCard key={p.id} position={p} onClose={onClose} />
          ))}
        </div>
      ) : (
        positions.map((p, i) => (
          <PositionRow key={p.id} position={p} onClose={onClose} isLast={i === positions.length - 1} />
        ))
      )}
    </Card>
  );
}
