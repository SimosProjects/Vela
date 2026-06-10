import { useRef, useEffect } from 'react';
import { B } from '../styles/theme.js';

const LEVEL_COLOR = {
  INFO:  B.mu,
  WARN:  B.am,
  ERROR: B.rd,
};

const MOCK_LOGS = [
  { time: '9:20 AM', level: 'INFO',  msg: 'Regime set: Bullish — SPY $578.42, VIX 14.2, sizing 100%' },
  { time: '9:31 AM', level: 'INFO',  msg: 'HOOD 100C filled @ $6.00 × 5 — Krazy' },
  { time: '9:37 AM', level: 'INFO',  msg: 'ORCL 300C filled @ $6.25 × 5 — Krazy' },
  { time: '9:42 AM', level: 'WARN',  msg: 'TSLA rejected — symbol already open' },
  { time: '10:34 AM', level: 'INFO', msg: 'RBLX 43C filled @ $1.18 × 3 — kareem' },
  { time: '10:36 AM', level: 'INFO', msg: 'AMD filled @ $489.59 × 6 — Lukasz' },
  { time: '11:04 AM', level: 'WARN', msg: 'SPY scalp rejected — daily chop score 68' },
  { time: '11:57 AM', level: 'INFO', msg: 'DDOG 250C filled @ $4.40 × 6 — Paltrader' },
  { time: '12:35 PM', level: 'INFO', msg: 'QQQ 726C filled @ $2.85 × 10 — woooh77' },
  { time: '12:52 PM', level: 'INFO', msg: 'MSFT 420C filled @ $13.40 × 2 — Krazy' },
  { time: '1:25 PM', level: 'INFO',  msg: 'SHAK filled @ $53.99 × 55 — Kevin' },
  { time: '1:32 PM', level: 'INFO',  msg: 'META filled @ $588.48 × 5 — Theo' },
  { time: '2:14 PM', level: 'WARN',  msg: 'NVDA rejected — exposure cap reached (42%)' },
  { time: '2:29 PM', level: 'ERROR', msg: 'IBKR order 2950 timeout — checking late fill' },
  { time: '2:30 PM', level: 'WARN',  msg: 'Late fill confirmed @ $3.10 — stop placed' },
  { time: '2:52 PM', level: 'INFO',  msg: 'LITE filled @ $889.96 × 3 — ultra' },
  { time: '3:45 PM', level: 'INFO',  msg: 'GCT 40C stop hit @ $1.38 — closed' },
  { time: '3:58 PM', level: 'INFO',  msg: 'RBLX 43C stop hit @ $0.59 — closed −$177' },
  { time: '4:00 PM', level: 'INFO',  msg: 'Market closed. 10 positions open.' },
];

export function LogPanel() {
  const bottomRef = useRef(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, []);

  return (
    <div style={{
      background: B.bg,
      border: `1px solid ${B.bd}`,
      borderRadius: 10,
      display: 'flex',
      flexDirection: 'column',
      height: '100%',
      minHeight: 300,
      overflow: 'hidden',
    }}>
      <div style={{
        padding: '10px 14px',
        borderBottom: `1px solid ${B.bd}`,
        fontSize: 11,
        fontWeight: 600,
        color: B.mu,
        letterSpacing: '0.06em',
        textTransform: 'uppercase',
        flexShrink: 0,
      }}>
        Worker Log
      </div>

      <div style={{
        flex: 1,
        overflowY: 'auto',
        padding: '8px 0',
      }}>
        {MOCK_LOGS.map((entry, i) => (
          <div key={i} style={{
            display: 'grid',
            gridTemplateColumns: '52px 36px 1fr',
            gap: 6,
            padding: '3px 12px',
            fontSize: 10,
            lineHeight: '16px',
            fontFamily: 'ui-monospace, SFMono-Regular, monospace',
          }}>
            <span style={{ color: B.mu2, whiteSpace: 'nowrap' }}>{entry.time}</span>
            <span style={{
              color: LEVEL_COLOR[entry.level] ?? B.mu,
              fontWeight: 700,
            }}>
              {entry.level}
            </span>
            <span style={{ color: entry.level === 'INFO' ? B.tx2 : LEVEL_COLOR[entry.level] }}>
              {entry.msg}
            </span>
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}