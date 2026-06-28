import { B } from '../styles/theme.js';
import { useWindowSize }       from '../hooks/useWindowSize.js';
import { Header }              from '../components/Header.jsx';
import { StatBar }             from '../components/StatBar.jsx';
import { OpenPositions }       from '../components/OpenPositions.jsx';
import { ClosedTrades }        from '../components/ClosedTrades.jsx';
import { RegimePanel }         from '../components/RegimePanel.jsx';
import { ControlsPanel }       from '../components/ControlsPanel.jsx';
import { RiskConfig }          from '../components/RiskConfig.jsx';
import { TraderRoster }        from '../components/TraderRoster.jsx';
import { AlertSourcePanel, BrokerPanel } from '../components/SystemPanels.jsx';
import { MarketClosedBanner }  from '../components/banners/MarketClosedBanner.jsx';
import { PausedBanner }        from '../components/banners/PausedBanner.jsx';
import { LogPanel }            from '../components/LogPanel.jsx';

export function DesktopLayout({
  data, paused,
  allowOverrideBlocks,
  blockCalls, regimeBlocksCalls,
  blockHigh, blockLotto,
  onTogglePause, onToggleAllowOverrideBlocks,
  onToggleBlockCalls, onToggleBlockHigh, onToggleBlockLotto,
  onForceClose, lastUpdated,
}) {
  const { timestamp, positions, closedToday, regime, account, riskConfig, traders, system } = data;
  const marketOpen = system?.marketOpen ?? true;
  const { width }  = useWindowSize();
  const showLog    = width >= 1000;

  const gridTemplate = showLog
    ? '2fr 1.2fr 300px'
    : 'minmax(0, 1fr) 300px';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: B.bg, color: B.tx }}>
      <Header timestamp={timestamp} system={system} paused={paused} onTogglePause={onTogglePause} />
      {!marketOpen && <MarketClosedBanner />}
      {paused       && <PausedBanner />}
      <StatBar account={account} />
      <div style={{ flex: 1, overflow: 'auto' }}>
        <div style={{
          display: 'grid',
          gridTemplateColumns: gridTemplate,
          gap: 12,
          padding: 12,
          alignItems: 'start',
        }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <OpenPositions positions={positions} lastUpdated={lastUpdated} onClose={onForceClose} />
            <ClosedTrades trades={closedToday} />
          </div>
          {showLog && <LogPanel />}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <RegimePanel regime={regime} />
            <ControlsPanel
              allowOverrideBlocks={allowOverrideBlocks}
              blockCalls={blockCalls}
              regimeBlocksCalls={regimeBlocksCalls}
              blockHigh={blockHigh}
              blockLotto={blockLotto}
              paused={paused}
              onToggleAllowOverrideBlocks={onToggleAllowOverrideBlocks}
              onTogglePause={onTogglePause}
              onToggleBlockCalls={onToggleBlockCalls}
              onToggleBlockHigh={onToggleBlockHigh}
              onToggleBlockLotto={onToggleBlockLotto}
            />
            <RiskConfig
              blockHigh={blockHigh}
              blockLotto={blockLotto}
            />
            <AlertSourcePanel system={system} />
            <BrokerPanel system={system} />
            <TraderRoster />
          </div>
        </div>
      </div>
    </div>
  );
}