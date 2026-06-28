import { useState, useEffect, useCallback } from 'react';
import { B } from '../styles/theme.js';
import { Card, Pill } from './shared/index.js';

function SubHeader({ children }) {
  return (
    <div style={{
      fontSize: 10, color: B.mu2, marginTop: 8, marginBottom: 3,
      letterSpacing: '0.06em', textTransform: 'uppercase',
    }}>
      {children}
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

function NumericRow({ label, value, onChange, prefix, suffix, step, min }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '4px 0', borderBottom: `0.5px solid ${B.bd2}`, fontSize: 12 }}>
      <span style={{ color: B.mu }}>{label}</span>
      <div style={{ display: 'flex', alignItems: 'center', gap: 3 }}>
        {prefix && <span style={{ color: B.mu2, fontSize: 11 }}>{prefix}</span>}
        <input
          type="number"
          value={value ?? ''}
          step={step ?? 1}
          min={min}
          onClick={e => e.target.select()}
          onWheel={e => e.target.blur()}
          onChange={e => {
            const v = parseFloat(e.target.value);
            if (!isNaN(v)) onChange(v);
          }}
          style={{
            width:        72,
            background:   B.cd2,
            border:       `1px solid ${B.bd}`,
            borderRadius: 4,
            padding:      '2px 5px',
            color:        B.tx,
            fontSize:     12,
            textAlign:    'right',
            outline:      'none',
          }}
        />
        {suffix && <span style={{ color: B.mu2, fontSize: 11 }}>{suffix}</span>}
      </div>
    </div>
  );
}

function SaveBar({ onSave, onCancel, saving, saveMsg }) {
  return (
    <div style={{ marginBottom: 10, paddingBottom: 10, borderBottom: `1px solid ${B.bd}` }}>
      <div style={{ display: 'flex', gap: 8 }}>
        <button
          onClick={onSave}
          disabled={saving}
          style={{
            flex: 1, padding: '7px 0', borderRadius: 5, border: 'none',
            cursor: saving ? 'default' : 'pointer',
            background: B.gr, color: '#fff', fontWeight: 700, fontSize: 12,
            opacity: saving ? 0.6 : 1,
          }}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button
          onClick={onCancel}
          disabled={saving}
          style={{
            flex: 1, padding: '7px 0', borderRadius: 5,
            border: `1px solid ${B.bd}`, cursor: 'pointer',
            background: B.cd2, color: B.mu, fontWeight: 600, fontSize: 12,
          }}
        >
          Cancel
        </button>
      </div>
      {saveMsg && (
        <div style={{ marginTop: 5, fontSize: 10, color: B.mu, textAlign: 'center' }}>
          {saveMsg}
        </div>
      )}
    </div>
  );
}

/// Risk Config panel — self-fetches from GET /api/config/risk.
/// Saves to POST /api/config/risk; changes persist to DB and apply on restart.
/// blockHigh and blockLotto are live session toggle states from App.jsx.
export function RiskConfig({ blockHigh = true, blockLotto = true }) {
  const [config,  setConfig]  = useState(null);
  const [draft,   setDraft]   = useState(null);
  const [saving,  setSaving]  = useState(false);
  const [saveMsg, setSaveMsg] = useState(null);
  const [loadErr, setLoadErr] = useState(false);

  const fetchConfig = useCallback(async () => {
    try {
      const res = await fetch('/api/config/risk');
      if (!res.ok) { setLoadErr(true); return; }
      const data = await res.json();
      setConfig(data);
      setDraft(data);
      setLoadErr(false);
    } catch {
      setLoadErr(true);
    }
  }, []);

  useEffect(() => { fetchConfig(); }, [fetchConfig]);

  const isDirty = config && draft &&
    JSON.stringify(config) !== JSON.stringify(draft);

  const set = (key, value) => setDraft(d => ({ ...d, [key]: value }));

  // Loss limits are stored as negative; set helpers keep them negative.
  const setLoss = (key, absValue) =>
    set(key, absValue === 0 ? 0 : -Math.abs(absValue));

  const handleSave = async () => {
    setSaving(true);
    try {
      const res = await fetch('/api/config/risk', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify(draft),
      });
      if (!res.ok) throw new Error('Save failed');
      const saved = await res.json();
      setConfig(saved);
      setDraft(saved);
      setSaveMsg('Saved — applies on next restart');
      setTimeout(() => setSaveMsg(null), 5000);
    } catch {
      setSaveMsg('Save failed — check console');
    } finally {
      setSaving(false);
    }
  };

  const handleCancel = () => {
    setDraft(config);
    setSaveMsg(null);
  };

  if (loadErr) return (
    <Card><div style={{ fontSize: 11, color: B.rd }}>Risk config unavailable</div></Card>
  );

  if (!draft) return (
    <Card><div style={{ fontSize: 11, color: B.mu }}>Loading config…</div></Card>
  );

  return (
    <Card>
      <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu, marginBottom: 8 }}>
        Risk Configuration
      </div>

      {/* Save/Cancel at the top — visible immediately when anything changes */}
      {(isDirty || saveMsg) && (
        <SaveBar
          onSave={handleSave}
          onCancel={handleCancel}
          saving={saving}
          saveMsg={saveMsg}
        />
      )}

      <NumericRow label="Min xScore"        value={draft.minXScore}           onChange={v => set('minXScore', v)}           step={1}   min={0}  />
      <FlagRow    label="Allow high risk"    on={!blockHigh} />
      <FlagRow    label="Allow lotto (0DTE)" on={!blockLotto} />

      <SubHeader>Options — Budgets</SubHeader>
      <NumericRow label="Initial"            value={draft.optionsInitialBudget}      onChange={v => set('optionsInitialBudget', v)}      prefix="$" step={100} min={0} />
      <NumericRow label="Average"            value={draft.optionsAverageBudget}      onChange={v => set('optionsAverageBudget', v)}      prefix="$" step={100} min={0} />
      <NumericRow label="High initial"       value={draft.optionsHighBudget}         onChange={v => set('optionsHighBudget', v)}         prefix="$" step={100} min={0} />
      <NumericRow label="High average"       value={draft.optionsHighAverageBudget}  onChange={v => set('optionsHighAverageBudget', v)}  prefix="$" step={100} min={0} />
      <NumericRow label="Lotto initial"      value={draft.optionsLottoBudget}        onChange={v => set('optionsLottoBudget', v)}        prefix="$" step={50}  min={0} />
      <NumericRow label="Lotto average"      value={draft.optionsLottoAverageBudget} onChange={v => set('optionsLottoAverageBudget', v)} prefix="$" step={50}  min={0} />
      <NumericRow label="Max (1-contract)"   value={draft.optionsMaxBudget}          onChange={v => set('optionsMaxBudget', v)}          prefix="$" step={500} min={0} />

      <SubHeader>Options — Trails &amp; Target</SubHeader>
      <NumericRow label="Standard trail"     value={draft.optionsStandardTrailPct}   onChange={v => set('optionsStandardTrailPct', v)}   suffix="%" step={1} min={1} />
      <NumericRow label="High trail"         value={draft.optionsHighTrailPct}       onChange={v => set('optionsHighTrailPct', v)}       suffix="%" step={1} min={1} />
      <NumericRow label="Lotto trail"        value={draft.optionsLottoTrailPct}      onChange={v => set('optionsLottoTrailPct', v)}      suffix="%" step={1} min={1} />
      <NumericRow label="Target multiple"    value={draft.optionsTargetMultiple}     onChange={v => set('optionsTargetMultiple', v)}     suffix="×" step={0.5} min={1} />

      <SubHeader>Stocks — Budgets</SubHeader>
      <NumericRow label="Initial"            value={draft.stockInitialBudget}        onChange={v => set('stockInitialBudget', v)}        prefix="$" step={100} min={0} />
      <NumericRow label="Average"            value={draft.stockAverageBudget}        onChange={v => set('stockAverageBudget', v)}        prefix="$" step={100} min={0} />
      <NumericRow label="Max (1-contract)"   value={draft.stockMaxBudget}            onChange={v => set('stockMaxBudget', v)}            prefix="$" step={500} min={0} />

      <SubHeader>Stocks — Trails &amp; Target</SubHeader>
      <NumericRow label="Standard trail"     value={draft.stockStandardTrailPct}     onChange={v => set('stockStandardTrailPct', v)}     suffix="%" step={1} min={1} />
      <NumericRow label="High trail"         value={draft.stockHighTrailPct}         onChange={v => set('stockHighTrailPct', v)}         suffix="%" step={1} min={1} />
      <NumericRow label="Lotto trail"        value={draft.stockLottoTrailPct}        onChange={v => set('stockLottoTrailPct', v)}        suffix="%" step={1} min={1} />
      <NumericRow label="Target multiple"    value={draft.stockTargetMultiple}       onChange={v => set('stockTargetMultiple', v)}       suffix="×" step={0.5} min={1} />

      <SubHeader>Limits</SubHeader>
      {/* Loss limits stored as negative; display as positive so user types $30000 not -$30000 */}
      <NumericRow label="Max daily loss"     value={Math.abs(draft.dailyLossLimit)}     onChange={v => setLoss('dailyLossLimit', v)}     prefix="$" step={500} min={0} />
      <NumericRow label="Max chop-day loss"  value={Math.abs(draft.chopDailyLossLimit)} onChange={v => setLoss('chopDailyLossLimit', v)} prefix="$" step={500} min={0} />
    </Card>
  );
}