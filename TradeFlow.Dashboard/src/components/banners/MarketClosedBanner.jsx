import { B } from '../../styles/theme.js';

export function MarketClosedBanner() {
  return (
    <div style={{
      background: 'rgba(210,153,34,0.07)',
      borderBottom: `1px solid rgba(210,153,34,0.22)`,
      padding: '7px 16px',
      display: 'flex',
      alignItems: 'center',
      gap: 8,
      fontSize: 11,
      color: B.am,
    }}>
      <span style={{ fontWeight: 700 }}>MARKET CLOSED</span>
      <span style={{ color: B.mu }}>Showing last session data · Next open Tue Jun 10, 09:30 ET</span>
    </div>
  );
}
