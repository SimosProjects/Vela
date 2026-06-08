import { B } from '../styles/theme.js';
import { Card, StatusDot } from './shared/index.js';

function PluggableCard({ title, children }) {
  return (
    <Card style={{ borderStyle: 'dashed' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
        <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.07em', textTransform: 'uppercase', color: B.mu }}>
          {title}
        </div>
        <span style={{ fontSize: 9, color: B.mu2, letterSpacing: '0.07em', textTransform: 'uppercase', fontStyle: 'italic' }}>
          pluggable
        </span>
      </div>
      {children}
    </Card>
  );
}

function InfoGrid({ rows }) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 10px', fontSize: 11 }}>
      {rows.map(([label, value, color]) => (
        <>
          <span key={label + 'k'} style={{ color: B.mu }}>{label}</span>
          <span key={label + 'v'} style={{ color: color ?? B.tx }}>{value}</span>
        </>
      ))}
    </div>
  );
}

export function AlertSourcePanel({ system }) {
  return (
    <PluggableCard title="Alert source · Xtrades">
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 10 }}>
        <StatusDot connected={system?.xtradesConnected} />
        <span style={{ fontSize: 11, color: system?.xtradesConnected ? B.gr : B.rd, fontWeight: 600 }}>
          {system?.xtradesConnected ? 'Connected' : 'Disconnected'}
        </span>
      </div>
      <InfoGrid rows={[
        ['REST polling', '30s interval'],
        ['SignalR',      'Live',         B.gr],
        ['Last alert',  system?.lastAlert ?? '—'],
        ['xScore filter', '≥ 75'],
      ]} />
    </PluggableCard>
  );
}

export function BrokerPanel({ system }) {
  return (
    <PluggableCard title="Broker · Interactive Brokers">
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 10 }}>
        <StatusDot connected={system?.ibkrConnected} />
        <span style={{ fontSize: 11, color: system?.ibkrConnected ? B.gr : B.rd, fontWeight: 600 }}>
          {system?.ibkrConnected ? 'Gateway connected' : 'Disconnected'}
        </span>
      </div>
      <InfoGrid rows={[
        ['Mode',           system?.accountMode ?? '—',  B.am],
        ['Port',           String(system?.ibkrPort ?? '—')],
        ['Heartbeat',      system?.lastHeartbeat ?? '—'],
        ['Level 3 options', system?.level3Pending ? 'Pending (~30d)' : 'Approved', system?.level3Pending ? B.am : B.gr],
      ]} />
    </PluggableCard>
  );
}
