import { B } from '../../styles/theme.js';

export function StatusDot({ connected, label }) {
  const color = connected ? B.gr : B.rd;
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      <span style={{
        width: 7,
        height: 7,
        borderRadius: '50%',
        display: 'inline-block',
        flexShrink: 0,
        background: color,
        boxShadow: `0 0 0 2px ${connected ? 'rgba(63,185,80,0.2)' : 'rgba(248,81,73,0.2)'}`,
      }} />
      {label && <span style={{ fontSize: 11, color: B.mu }}>{label}</span>}
    </span>
  );
}
