import { B } from '../styles/theme.js';
import { Header }              from '../components/Header.jsx';
import { StatBar }             from '../components/StatBar.jsx';
import { OpenPositions }       from '../components/OpenPositions.jsx';
import { ClosedTrades }        from '../components/ClosedTrades.jsx';
import { RegimePanel }         from '../components/RegimePanel.jsx';
import { RiskConfig }          from '../components/RiskConfig.jsx';
import { TraderRoster }        from '../components/TraderRoster.jsx';
import { AlertSourcePanel, BrokerPanel } from '../components/SystemPanels.jsx';
import { MarketClosedBanner }  from '../components/banners/MarketClosedBanner.jsx';
import { PausedBanner }        from '../components/banners/PausedBanner.jsx';

export function DesktopLayout({ data, paused, onTogglePause, onForceClose, lastUpdated }) {
  const { timestamp, positions, closedToday, regime, account, riskConfig, traders, system } = data;
  const marketOpen = system?.marketOpen ?? true;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: B.bg, color: B.tx }}>
      <Header timestamp={timestamp} system={system} paused={paused} onTogglePause={onTogglePause} />
      {!marketOpen && <MarketClosedBanner />}
      {paused       && <PausedBanner />}
      <StatBar account={account} />

      <div style={{ flex: 1, overflow: 'auto' }}>
        <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 360px', gap: 12, padding: 12, alignItems: 'start' }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <OpenPositions positions={positions} lastUpdated={lastUpdated} onClose={onForceClose} />
            <ClosedTrades trades={closedToday} />
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <RegimePanel regime={regime} />
            <RiskConfig config={riskConfig} />
            <AlertSourcePanel system={system} />
            <BrokerPanel system={system} />
            <TraderRoster traders={traders} minXScore={riskConfig.minXScore} />
          </div>
        </div>
      </div>
    </div>
  );
}
