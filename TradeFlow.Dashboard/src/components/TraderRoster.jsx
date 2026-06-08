import { useState } from 'react';
import { B } from '../styles/theme.js';
import { Card, Pill } from './shared/index.js';

export function TraderRoster({ traders, minXScore }) {
  const [open, setOpen] = useState(false);
  const approved = traders.filter(t => t.approved);
  const blocked  = traders.filter(t => !t.approved);

  return (
    <Card noPad>
      <button
        onClick={() => setOpen(o => !o)}
        className="roster-toggle"
        style={{
          width: '100%',
          padding: '12px 14px',
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          background: 'transparent',
          border: 'none',
          borderRadius: open ? '8px 8px 0 0' : 8,
          transition: 'background 0.1s',
        }}
      >
        <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu }}>
          Trader Roster
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 11, color: B.mu2 }}>{approved.length} active · {blocked.length} blocked</span>
          <span style={{ fontSize: 11, color: B.mu, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s', display: 'inline-block' }}>▼</span>
        </div>
      </button>

      {open && (
        <div style={{ padding: '4px 14px 12px', borderTop: `1px solid ${B.bd2}` }}>
          {approved.map(t => (
            <div key={t.name} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '4px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 12 }}>
              <div>
                <span style={{ color: B.tx, fontWeight: 500 }}>{t.name}</span>
                <span style={{ fontSize: 10, color: B.mu, marginLeft: 5 }}>{t.rank}</span>
              </div>
              <span style={{ fontSize: 12, fontWeight: 700, color: t.xScore >= minXScore ? B.gr : B.am }}>
                {t.xScore}
              </span>
            </div>
          ))}

          {blocked.length > 0 && (
            <>
              <div style={{ fontSize: 10, color: B.mu2, marginTop: 8, marginBottom: 3, letterSpacing: '0.06em', textTransform: 'uppercase' }}>
                Blocked (0%)
              </div>
              {blocked.map(t => (
                <div key={t.name} style={{ display: 'flex', justifyContent: 'space-between', padding: '3px 0', fontSize: 11 }}>
                  <span style={{ color: B.mu }}>{t.name}</span>
                  <Pill color={B.rd} style={{ fontSize: 9 }}>blocked</Pill>
                </div>
              ))}
            </>
          )}
        </div>
      )}
    </Card>
  );
}
