import { B } from '../../styles/theme.js';

export function PausedBanner() {
  return (
    <div style={{
      background: 'rgba(210,153,34,0.09)',
      borderBottom: `1px solid rgba(210,153,34,0.28)`,
      padding: '5px 16px',
      fontSize: 11,
      fontWeight: 700,
      color: B.am,
    }}>
      ⏸ TRADING PAUSED — no new entries until resumed
    </div>
  );
}
