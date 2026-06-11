import { useState } from 'react';
import { B } from '../styles/theme.js';
import { Card } from './shared/index.js';

function Toggle({ on, onChange }) {
  return (
    <button
      onClick={onChange}
      style={{
        width: 36,
        height: 20,
        borderRadius: 10,
        border: 'none',
        cursor: 'pointer',
        position: 'relative',
        background: on ? B.am : B.mu2,
        transition: 'background 0.2s',
        padding: 0,
        flexShrink: 0,
      }}
    >
      <div style={{
        position: 'absolute',
        top: 2,
        left: on ? 18 : 2,
        width: 16,
        height: 16,
        borderRadius: 8,
        background: on ? '#fff' : B.mu,
        transition: 'left 0.2s',
      }} />
    </button>
  );
}

/// Session Controls panel, Block Calls toggle and Pause Trading button.
/// paused and onTogglePause are fully controlled from App.jsx via the API.
/// blockCalls local state is temporary until block calls backend is wired.
export function ControlsPanel({ blockCalls: blockCallsProp = false, paused, onTogglePause, onToggleBlockCalls }) {
  const [blockCalls, setBlockCalls] = useState(blockCallsProp);

  const handleBlockCalls = () => {
    setBlockCalls(v => !v);
    onToggleBlockCalls?.();
  };

  return (
    <Card>
      <div style={{
        fontSize: 10,
        fontWeight: 700,
        letterSpacing: '0.07em',
        textTransform: 'uppercase',
        color: B.mu,
        marginBottom: 12,
      }}>
        Session Controls
      </div>

      {/* Block Calls row */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
        <div>
          <div style={{ fontSize: 12, fontWeight: 600, color: blockCalls ? B.am : B.tx }}>
            Block call entries
          </div>
          <div style={{ fontSize: 11, color: B.mu, marginTop: 1 }}>
            {blockCalls ? 'Calls rejected this session' : 'Calls allowed'}
          </div>
        </div>
        <Toggle on={blockCalls} onChange={handleBlockCalls} />
      </div>

      {blockCalls && (
        <div style={{
          marginTop: 8,
          padding: '5px 8px',
          borderRadius: 4,
          background: 'rgba(210,153,34,0.07)',
          border: `1px solid rgba(210,153,34,0.22)`,
          fontSize: 11,
          color: B.am,
        }}>
          ⚠ New call entries will be rejected by the risk engine
        </div>
      )}

      <div style={{ height: '0.5px', background: B.bd, margin: '12px 0' }} />

      {/* Pause Trading button, fully controlled, no local state */}
      <button
        onClick={onTogglePause}
        style={{
          width: '100%',
          padding: '9px 12px',
          borderRadius: 6,
          border: `1px solid ${paused ? B.rd : B.bd}`,
          cursor: 'pointer',
          background: paused ? 'rgba(248,81,73,0.1)' : B.cd2,
          color: paused ? B.rd : B.mu,
          fontWeight: 700,
          fontSize: 12,
          letterSpacing: '0.05em',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 8,
          transition: 'all 0.15s',
        }}
      >
        <span style={{
          width: 7,
          height: 7,
          borderRadius: '50%',
          background: paused ? B.rd : B.gr,
          flexShrink: 0,
        }} />
        {paused ? 'RESUME TRADING' : 'PAUSE TRADING'}
      </button>

      {paused && (
        <div style={{ marginTop: 6, fontSize: 11, color: B.mu, textAlign: 'center' }}>
          New entries halted — open positions unaffected
        </div>
      )}
    </Card>
  );
}