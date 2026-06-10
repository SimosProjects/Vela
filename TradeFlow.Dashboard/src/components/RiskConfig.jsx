import { B } from '../styles/theme.js';
import { Card, Pill } from './shared/index.js';

function KV({ label, value, valueColor }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '5px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 12 }}>
      <span style={{ color: B.mu }}>{label}</span>
      <span style={{ fontWeight: 600, color: valueColor ?? B.tx }}>{value}</span>
    </div>
  );
}

function FlagRow({ label, on }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '5px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 12 }}>
      <span style={{ color: B.mu }}>{label}</span>
      <Pill color={on ? B.gr : B.rd}>{on ? 'ON' : 'OFF'}</Pill>
    </div>
  );
}

function SubHeader({ children }) {
  return (
    <div style={{ fontSize: 10, color: B.mu2, marginTop: 8, marginBottom: 3, letterSpacing: '0.06em', textTransform: 'uppercase' }}>
      {children}
    </div>
  );
}

export function RiskConfig({ config }) {
  return (
    <Card>
      <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu, marginBottom: 8 }}>
        Risk Configuration
      </div>

      <KV label="Min xScore"        value={config.minXScore} />
      <FlagRow label="Allow high risk"    on={config.allowHigh} />
      <FlagRow label="Allow lotto (0DTE)" on={config.allowLotto} />

      <SubHeader>Options</SubHeader>
      <KV label="Initial budget"  value={`$${config.optionsBudget.toLocaleString()}`} />
      <KV label="Average budget"  value={`$${config.optionsAvgBudget.toLocaleString()}`} />
      <KV label="Standard trail"  value={`${config.optionsStdTrailPct}%`} />
      <KV label="High trail"      value={`${config.optionsHighTrailPct}%`} />

      <SubHeader>Stocks</SubHeader>
      <KV label="Initial budget"  value={`$${config.stocksBudget.toLocaleString()}`} />
      <KV label="Average budget"  value={`$${config.stocksAvgBudget.toLocaleString()}`} />
      <KV label="Standard trail"  value={`${config.stocksStdTrailPct}%`} />

      <SubHeader>Limits</SubHeader>
      <KV label="Daily loss limit"  value={`$${Math.abs(config.dailyLossLimit).toLocaleString()}`}     valueColor={B.rd} />
      <KV label="Chop loss limit"   value={`$${Math.abs(config.chopDailyLossLimit).toLocaleString()}`} valueColor={B.am} />

      <div style={{ height: '0.5px', background: B.bd, margin: '10px 0 6px' }} />
      <div style={{ fontSize: 10, color: B.mu, marginBottom: 5 }}>Allowed Discord ranks</div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
        {config.allowedDiscordRanks.map(rank => (
          <span key={rank} style={{ padding: '2px 6px', borderRadius: 3, fontSize: 10, background: B.cd2, color: B.mu, border: `1px solid ${B.bd}` }}>
            {rank}
          </span>
        ))}
      </div>
    </Card>
  );
}