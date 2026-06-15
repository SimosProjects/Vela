import { useState, useEffect } from 'react';
import { B } from '../styles/theme.js';
import { Card, Pill } from './shared/index.js';

function SectionHeader({ children }) {
  return (
    <div style={{ fontSize: 10, color: B.mu2, marginTop: 8, marginBottom: 3, letterSpacing: '0.06em', textTransform: 'uppercase' }}>
      {children}
    </div>
  );
}

/// Trader Roster panel, self-fetches from GET /api/dashboard/traders.
/// Three sections: Approved (full size), Restricted (partial allotment), Blocked (0%).
export function TraderRoster() {
  const [open, setOpen] = useState(false);
  const [data, setData] = useState(null);

  useEffect(() => {
    fetch('/api/dashboard/traders')
      .then(r => r.json())
      .then(setData)
      .catch(() => {});
  }, []);

  const approved   = data?.approved   ?? [];
  const restricted = data?.restricted ?? [];
  const blocked    = data?.blocked    ?? [];

  const activeCount  = approved.length + restricted.length;
  const blockedCount = blocked.length;

  return (
    <Card noPad>
      <button
        onClick={() => setOpen(o => !o)}
        className="roster-toggle"
        style={{
          width:        '100%',
          padding:      '12px 14px',
          display:      'flex',
          justifyContent: 'space-between',
          alignItems:   'center',
          background:   'transparent',
          border:       'none',
          borderRadius: open ? '8px 8px 0 0' : 8,
          cursor:       'pointer',
          transition:   'background 0.1s',
        }}
      >
        <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu }}>
          Trader Roster
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 11, color: B.mu2 }}>
            {activeCount} active · {blockedCount} blocked
          </span>
          <span style={{ fontSize: 11, color: B.mu, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s', display: 'inline-block' }}>
            ▼
          </span>
        </div>
      </button>

      {open && (
        <div style={{ padding: '4px 14px 12px', borderTop: `1px solid ${B.bd2}` }}>

          {/* Approved — full position size */}
          {approved.length > 0 && (
            <>
              <SectionHeader>Approved</SectionHeader>
              {approved.map(name => (
                <div key={name} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '4px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 12 }}>
                  <span style={{ color: B.tx, fontWeight: 500 }}>{name}</span>
                  <Pill color={B.gr}>100%</Pill>
                </div>
              ))}
            </>
          )}

          {/* Restricted — partial allotment */}
          {restricted.length > 0 && (
            <>
              <SectionHeader>Restricted</SectionHeader>
              {restricted.map(t => (
                <div key={t.name} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '4px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 12 }}>
                  <span style={{ color: B.tx, fontWeight: 500 }}>{t.name}</span>
                  <Pill color={B.am}>{t.allotmentPct}%</Pill>
                </div>
              ))}
            </>
          )}

          {/* Blocked — 0% allotment */}
          {blocked.length > 0 && (
            <>
              <SectionHeader>Blocked (0%)</SectionHeader>
              {blocked.map(name => (
                <div key={name} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '3px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 11 }}>
                  <span style={{ color: B.mu }}>{name}</span>
                  <Pill color={B.rd}>blocked</Pill>
                </div>
              ))}
            </>
          )}

          {!data && (
            <div style={{ fontSize: 11, color: B.mu, padding: '8px 0' }}>Loading…</div>
          )}
        </div>
      )}
    </Card>
  );
}