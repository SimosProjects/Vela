import { B } from '../styles/theme.js';
import { StatusDot } from './shared/index.js';

export function Header({ timestamp, system, paused, onTogglePause }) {
  return (
    <div style={{
      background: B.card,
      borderBottom: `1px solid ${B.bd}`,
      padding: '8px 16px',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      flexShrink: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
        <div style={{ fontWeight: 700, fontSize: 16, color: B.tx }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: '8px' }}>
          <svg width="16" height="16" viewBox="0 0 18 18" fill="none">
            <path d="M9 2 L16 14 L9 11 L2 14 Z" fill="#378add" />
          </svg>
          <span style={{ fontWeight: 700, letterSpacing: '0.08em', color: '#e6edf3' }}>VELA</span>
        </span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 11, color: B.mu }}>
          <span style={{
            width: 6, height: 6, borderRadius: '50%', display: 'inline-block',
            background: system?.marketOpen ? B.gr : B.am,
          }} />
          {timestamp}
        </div>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        <StatusDot connected={system?.ibkrConnected}    label="Gateway" />
        <StatusDot connected={system?.xtradesConnected} label="Xtrades" />
        <StatusDot connected={system?.workerRunning}    label="Worker"  />
        <button
          onClick={onTogglePause}
          className="pause-btn"
          style={{
            padding: '4px 12px',
            borderRadius: 5,
            fontSize: 11,
            fontWeight: 700,
            background: paused ? 'rgba(210,153,34,0.1)' : 'transparent',
            color: paused ? B.am : B.mu,
            border: `1px solid ${paused ? B.am : B.bd}`,
            transition: 'all 0.12s',
          }}
        >
          {paused ? '▶  RESUME' : '⏸  PAUSE'}
        </button>
      </div>
    </div>
  );
}
