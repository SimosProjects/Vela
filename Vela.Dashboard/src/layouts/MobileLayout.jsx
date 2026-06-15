import { useState } from 'react';
import { B, pnlColor, fmtDollar } from '../styles/theme.js';
import { StatusDot }           from '../components/shared/index.js';
import { OpenPositions }       from '../components/OpenPositions.jsx';
import { ClosedTrades }        from '../components/ClosedTrades.jsx';
import { RegimePanel }         from '../components/RegimePanel.jsx';
import { RiskConfig }          from '../components/RiskConfig.jsx';
import { TraderRoster }        from '../components/TraderRoster.jsx';
import { AlertSourcePanel, BrokerPanel } from '../components/SystemPanels.jsx';
import { MarketClosedBanner }  from '../components/banners/MarketClosedBanner.jsx';
import { PausedBanner }        from '../components/banners/PausedBanner.jsx';
import { Card }                from '../components/shared/index.js';

const TABS = [
  { id: 'positions', label: 'Positions' },
  { id: 'market',    label: 'Market'    },
  { id: 'system',    label: 'System'    },
  { id: 'config',    label: 'Config'    },
  { id: 'history',   label: 'History',  disabled: true },
];

export function MobileLayout({ data, paused, onTogglePause, onForceClose, lastUpdated }) {
  const [tab, setTab] = useState('positions');
  const { positions, closedToday, regime, account, riskConfig, traders, system } = data;
  const marketOpen = system?.marketOpen ?? true;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: B.bg, color: B.tx }}>

      <div style={{
        background: B.card, borderBottom: `1px solid ${B.bd}`,
        padding: '10px 14px',
        paddingTop: 'max(10px, env(safe-area-inset-top))',
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        flexShrink: 0,
      }}>
        <div style={{ fontWeight: 700, fontSize: 16, color: B.tx }}>
          <span style={{ color: B.bl }}>Trade</span>Flow
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <div style={{ display: 'flex', gap: 6 }}>
            <StatusDot connected={system?.ibkrConnected} />
            <StatusDot connected={system?.xtradesConnected} />
            <StatusDot connected={system?.workerRunning} />
          </div>
          <button onClick={onTogglePause} className="pause-btn"
            style={{ padding: '6px 12px', borderRadius: 5, fontSize: 11, fontWeight: 700, minHeight: 34,
              background: paused ? 'rgba(210,153,34,0.1)' : 'transparent',
              color: paused ? B.am : B.mu,
              border: `1px solid ${paused ? B.am : B.bd}` }}>
            {paused ? '▶ RESUME' : '⏸ PAUSE'}
          </button>
        </div>
      </div>

      {!marketOpen && <MarketClosedBanner />}
      {paused       && <PausedBanner />}

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.5px', background: B.bd, flexShrink: 0 }}>
        <div style={{ background: B.card, padding: '12px 14px' }}>
          <div style={{ fontSize: 10, color: B.mu, marginBottom: 3 }}>Account Balance</div>
          <div style={{ fontSize: 22, fontWeight: 700, color: B.tx, letterSpacing: '-0.01em' }}>
            ${account.balance.toLocaleString()}
          </div>
          <div style={{ fontSize: 10, color: B.mu, marginTop: 2 }}>Exposure {account.exposurePct.toFixed(1)}%</div>
        </div>
        <div style={{ background: B.card, padding: '12px 14px' }}>
          <div style={{ fontSize: 10, color: B.mu, marginBottom: 3 }}>Today's P&L</div>
          <div style={{ fontSize: 22, fontWeight: 700, letterSpacing: '-0.01em', color: pnlColor(account.dailyPnl) }}>
            {account.dailyPnl >= 0 ? '+' : ''}{fmtDollar(account.dailyPnl)}
          </div>
          <div style={{ fontSize: 10, color: B.mu, marginTop: 2 }}>{fmtDollar(account.deployable)} deployable</div>
        </div>
      </div>

      <div style={{ display: 'flex', background: B.card, borderBottom: `1px solid ${B.bd}`, flexShrink: 0, overflowX: 'auto' }}>
        {TABS.map(t => (
          <button
            key={t.id}
            onClick={() => !t.disabled && setTab(t.id)}
            className="tab-btn"
            style={{
              flex: '0 0 auto', padding: '11px 16px', fontSize: 12, fontWeight: 600,
              background: 'transparent', border: 'none', whiteSpace: 'nowrap',
              color: t.disabled ? B.mu2 : tab === t.id ? B.bl : B.mu,
              borderBottom: `2px solid ${tab === t.id ? B.bl : 'transparent'}`,
              cursor: t.disabled ? 'default' : 'pointer',
              opacity: t.disabled ? 0.5 : 1,
              transition: 'color 0.1s',
            }}
          >
            {t.label}
            {t.disabled && <span style={{ fontSize: 9, marginLeft: 4, color: B.mu2 }}>soon</span>}
          </button>
        ))}
      </div>

      <div className="tab-content" style={{ flex: 1, overflowY: 'auto', padding: 12, paddingBottom: 'max(20px, env(safe-area-inset-bottom))' }}>

        {tab === 'positions' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <OpenPositions positions={positions} lastUpdated={lastUpdated} onClose={onForceClose} isMobile />
            <ClosedTrades trades={closedToday} />
          </div>
        )}

        {tab === 'market' && <RegimePanel regime={regime} />}

        {tab === 'system' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Card>
              <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu, marginBottom: 8 }}>
                System Health
              </div>
              {[
                { label: 'IB Gateway',   connected: system?.ibkrConnected,    detail: `Port ${system?.ibkrPort} · ${system?.lastHeartbeat}` },
                { label: 'Xtrades Feed', connected: system?.xtradesConnected,  detail: `SignalR live · Last alert ${system?.lastAlert}` },
                { label: 'Worker',       connected: system?.workerRunning,     detail: 'Running' },
                { label: 'Market',       connected: marketOpen,                detail: marketOpen ? 'Open until 16:00 ET' : 'Closed' },
              ].map(row => (
                <div key={row.label} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '8px 0', borderBottom: `0.5px solid ${B.bd2}` }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <StatusDot connected={row.connected} />
                    <span style={{ fontSize: 13, color: B.tx, fontWeight: 500 }}>{row.label}</span>
                  </div>
                  <span style={{ fontSize: 11, color: B.mu }}>{row.detail}</span>
                </div>
              ))}
            </Card>
            <AlertSourcePanel system={system} />
            <BrokerPanel system={system} />
          </div>
        )}

        {tab === 'config' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <RiskConfig config={riskConfig} />
            <TraderRoster traders={traders} minXScore={riskConfig.minXScore} />
          </div>
        )}

        {tab === 'history' && (
          <div style={{ textAlign: 'center', padding: '48px 20px', color: B.mu }}>
            <div style={{ fontSize: 32, marginBottom: 12, opacity: 0.3 }}>📊</div>
            <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 6 }}>Trade History</div>
            <div style={{ fontSize: 12, lineHeight: 1.6 }}>
              Analytics and historical trade data will appear here once Phase 2 API endpoints are built.
            </div>
          </div>
        )}

      </div>
    </div>
  );
}
