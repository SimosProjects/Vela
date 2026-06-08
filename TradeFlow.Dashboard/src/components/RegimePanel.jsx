import { B, REGIME_COLORS, fmtPct, effectiveBudget } from '../styles/theme.js';
import { Card } from './shared/index.js';

export function RegimePanel({ regime }) {
  const rc = REGIME_COLORS[regime.tier] ?? REGIME_COLORS.Bullish;
  const budget = effectiveBudget(regime.sizingPct, regime.optionsBudget);

  const mas = [
    { label: '20MA',  value: regime.ma20,  pct: regime.ma20pct  },
    { label: '50MA',  value: regime.ma50,  pct: regime.ma50pct  },
    { label: '200MA', value: regime.ma200, pct: regime.ma200pct },
  ];

  return (
    <Card>
      <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu, marginBottom: 8 }}>
        Market Regime
      </div>

      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 14 }}>
        <div style={{ padding: '5px 10px', borderRadius: 6, background: rc.bg, border: `1px solid ${rc.bd}`, color: rc.c, fontWeight: 700, fontSize: 14 }}>
          ● {regime.tier.toUpperCase()}
        </div>
        <div style={{ textAlign: 'right', fontSize: 11 }}>
          <div style={{ color: B.mu }}>{regime.bias}</div>
          <div style={{ color: rc.c, fontWeight: 700, marginTop: 3 }}>
            {regime.sizingPct}% → ${budget.toLocaleString()} / trade
          </div>
        </div>
      </div>

      <div style={{ fontSize: 13, fontWeight: 600, color: B.tx, marginBottom: 8 }}>
        SPY ${regime.spyPrice.toFixed(2)}
      </div>

      {mas.map(m => (
        <div key={m.label} style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 4, fontSize: 12, alignItems: 'center' }}>
          <span style={{ color: B.mu, minWidth: 38 }}>{m.label}</span>
          <span style={{ color: B.mu2 }}>${m.value.toFixed(2)}</span>
          <span style={{
            padding: '1px 5px', borderRadius: 3, fontSize: 11,
            color: m.pct >= 0 ? B.gr : B.rd,
            background: m.pct >= 0 ? 'rgba(63,185,80,0.1)' : 'rgba(248,81,73,0.1)',
          }}>
            {m.pct >= 0 ? '+' : ''}{m.pct.toFixed(2)}%
          </span>
        </div>
      ))}

      <div style={{ height: '0.5px', background: B.bd, margin: '10px 0' }} />

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
        <div>
          <div style={{ fontSize: 10, color: B.mu, marginBottom: 2 }}>VIX</div>
          <div style={{ fontSize: 18, fontWeight: 700, color: regime.vix > 20 ? B.rd : B.gr }}>
            {regime.vix.toFixed(2)}
          </div>
          <div style={{ fontSize: 11, color: regime.vixDelta <= 0 ? B.gr : B.rd }}>
            {fmtPct(regime.vixDelta)} today
          </div>
        </div>
        <div style={{ textAlign: 'right' }}>
          <div style={{ fontSize: 10, color: B.mu, marginBottom: 2 }}>Chop score</div>
          <div style={{ fontSize: 18, fontWeight: 700, color: B.tx }}>
            {regime.chopScore}<span style={{ fontSize: 10, color: B.mu }}>/6</span>
          </div>
          <div style={{ fontSize: 11, color: B.mu }}>signals fired</div>
        </div>
      </div>

      {regime.blockCalls && (
        <div style={{ marginTop: 10, padding: '6px 8px', borderRadius: 4, background: 'rgba(248,81,73,0.07)', border: `1px solid rgba(248,81,73,0.22)`, fontSize: 11, color: B.rd }}>
          ⚠ Calls blocked this session
        </div>
      )}
    </Card>
  );
}
