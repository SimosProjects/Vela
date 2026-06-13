import { useState, useEffect } from 'react';
import { B, fmtTime } from '../styles/theme.js';

// Level-based color for the level badge
const LEVEL_COLOR = {
  INF: B.mu,
  WRN: B.am,
  ERR: B.rd,
};

// Returns the message text color based on level and content.
// Priority: ERR → red, WRN → amber, fill confirmed → green, default → text2.
function msgColor(level, message) {
  if (level === 'ERR') return B.rd;
  if (level === 'WRN') return B.am;
  const m = message.toLowerCase();
  if (m.includes('filled')) return B.gr;
  return B.tx2;
}

// Optional subtle left-border accent for high-signal rows.
function rowAccent(level, message) {
  if (level === 'ERR') return B.rd;
  if (level === 'WRN') return B.am;
  if (message.toLowerCase().includes('filled')) return B.gr;
  return 'transparent';
}

export function LogPanel() {
  const [logs,  setLogs]  = useState([]);
  const [error, setError] = useState(false);

  const fetchLogs = async () => {
    try {
      const res = await fetch('/api/dashboard/logs');
      if (!res.ok) { setError(true); return; }
      const data = await res.json();
      setLogs(data);
      setError(false);
    } catch {
      setError(true);
    }
  };

  useEffect(() => {
    fetchLogs();
    const timer = setInterval(fetchLogs, 3000);
    return () => clearInterval(timer);
  }, []);

  return (
    <div style={{
      background:    B.bg,
      border:        `1px solid ${B.bd}`,
      borderRadius:  10,
      display:       'flex',
      flexDirection: 'column',
      height:        '100%',
      minHeight:     300,
      overflow:      'hidden',
    }}>
      <div style={{
        padding:        '10px 14px',
        borderBottom:   `1px solid ${B.bd}`,
        fontSize:       11,
        fontWeight:     600,
        color:          B.mu,
        letterSpacing:  '0.06em',
        textTransform:  'uppercase',
        flexShrink:     0,
        display:        'flex',
        alignItems:     'center',
        justifyContent: 'space-between',
      }}>
        <span>Worker Log</span>
        {error && <span style={{ color: B.rd, fontWeight: 400, fontSize: 10 }}>unavailable</span>}
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
        {logs.length === 0 && !error && (
          <div style={{ padding: '12px', fontSize: 10, color: B.mu, fontFamily: 'ui-monospace, SFMono-Regular, monospace' }}>
            No log entries for today yet.
          </div>
        )}
        {logs.map((entry, i) => {
          const accent = rowAccent(entry.level, entry.message);
          return (
            <div key={i} style={{
              display:             'grid',
              gridTemplateColumns: '58px 36px 1fr',
              gap:                 6,
              padding:             '3px 12px 3px 10px',
              fontSize:            10,
              lineHeight:          '16px',
              fontFamily:          'ui-monospace, SFMono-Regular, monospace',
              borderLeft:          `2px solid ${accent}`,
            }}>
              <span style={{ color: B.mu2, whiteSpace: 'nowrap' }}>
                {fmtTime(entry.loggedAt)}
              </span>
              <span style={{ color: LEVEL_COLOR[entry.level] ?? B.mu, fontWeight: 700 }}>
                {entry.level}
              </span>
              <span style={{ color: msgColor(entry.level, entry.message) }}>
                {entry.message}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}