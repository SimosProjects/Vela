import { useState, useEffect } from 'react';
import { B } from './styles/theme.js';
import { usePolling } from './hooks/usePolling.js';
import { DesktopLayout } from './layouts/DesktopLayout.jsx';
import { MobileLayout }  from './layouts/MobileLayout.jsx';
import { ForceCloseModal } from './components/ForceCloseModal.jsx';

function useIsMobile() {
  const [isMobile, setIsMobile] = useState(() => window.innerWidth <= 520);
  useEffect(() => {
    const handler = () => setIsMobile(window.innerWidth <= 520);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, []);
  return isMobile;
}

// Shared hook for a session toggle that seeds from a regime-derived value on page load.
// Once the user explicitly clicks, userHasToggled = true and polling stops overriding it.
function useSessionToggle(regimeValue, apiPath, responseKey) {
  const [value, setValue]           = useState(false);
  const [userHasToggled, setToggled] = useState(false);

  const syncFromRegime = (regimeVal) => {
    if (!userHasToggled && regimeVal !== undefined) setValue(regimeVal);
  };

  const toggle = async () => {
    setToggled(true);
    try {
      const res  = await fetch(apiPath, { method: 'POST' });
      const body = await res.json();
      setValue(body[responseKey]);
    } catch {
      // Network error — next poll will correct
    }
  };

  return [value, toggle, syncFromRegime];
}

export default function App() {
  const isMobile = useIsMobile();
  const [paused, setPaused]           = useState(false);
  const [userHasToggledPause]         = useState(false);
  const [modalPosition, setModalPosition] = useState(null);
  const { data, lastUpdated, error }  = usePolling(10000);
  const [actionMsg, setActionMsg] = useState(null);

  const [blockCalls, onToggleBlockCalls, syncBlockCalls]   = useSessionToggle(null, '/api/dashboard/block-calls', 'blockCallsOverride');
  const [blockHigh,  onToggleBlockHigh,  syncBlockHigh]    = useSessionToggle(null, '/api/dashboard/block-high',  'blockHighOverride');
  const [blockLotto, onToggleBlockLotto, syncBlockLotto]   = useSessionToggle(null, '/api/dashboard/block-lotto', 'blockLottoOverride');

  // Sync paused from API on each poll
  useEffect(() => {
    if (data.system?.isPaused !== undefined) setPaused(data.system.isPaused);
  }, [data.system?.isPaused]);

  // Sync all three toggles from regime on each poll (unless user has overridden this session)
  useEffect(() => { syncBlockCalls(data.regime?.blockCalls ?? false); },  [data.regime?.blockCalls]);
  useEffect(() => { syncBlockHigh(data.system?.blockHighOverride ?? false); },  [data.system?.blockHighOverride]);
  useEffect(() => { syncBlockLotto(data.system?.blockLottoOverride ?? false); }, [data.system?.blockLottoOverride]);

  const onTogglePause = async () => {
    try {
      const res  = await fetch('/api/dashboard/pause', { method: 'POST' });
      const body = await res.json();
      setPaused(body.isPaused);
    } catch { }
  };

  const handleConfirmClose = async () => {
    if (!modalPosition) return;
    const pos = modalPosition;
    setModalPosition(null);
    try {
      const res = await fetch(
        `/api/dashboard/positions/${encodeURIComponent(pos.id)}/close`,
        { method: 'POST' }
      );
      if (res.ok) {
        setActionMsg({ type: 'ok', text: `Close requested for ${pos.contract}` });
      } else {
        const body = await res.text().catch(() => '');
        setActionMsg({ type: 'err', text: `Close failed for ${pos.contract}: ${body || res.status}` });
      }
    } catch {
      setActionMsg({ type: 'err', text: `Close request failed for ${pos.contract} — network error` });
    }
    setTimeout(() => setActionMsg(null), 6000);
  };

  const regimeBlocksCalls = data.regime?.blockCalls ?? false;

  const layoutProps = {
    data,
    paused,
    blockCalls,
    regimeBlocksCalls,
    blockHigh,
    blockLotto,
    lastUpdated,
    onTogglePause,
    onToggleBlockCalls,
    onToggleBlockHigh,
    onToggleBlockLotto,
    onForceClose: pos => setModalPosition(pos),
  };

  return (
    <div style={{ height: '100dvh', background: B.bg, color: B.tx, fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif' }}>
      {error && (
        <div style={{ background: 'rgba(248,81,73,0.1)', borderBottom: `1px solid rgba(248,81,73,0.3)`, padding: '6px 16px', fontSize: 11, color: B.rd }}>
          ⚠ Poll error: {error} — showing last known data
        </div>
      )}
      {actionMsg && (
        <div style={{
          padding: '6px 16px', fontSize: 11,
          color: actionMsg.type === 'ok' ? '#3fb950' : B.rd,
          background: actionMsg.type === 'ok' ? 'rgba(63,185,80,0.1)' : 'rgba(248,81,73,0.1)',
          borderBottom: `1px solid ${actionMsg.type === 'ok' ? 'rgba(63,185,80,0.3)' : 'rgba(248,81,73,0.3)'}`,
        }}>
          {actionMsg.text}
        </div>
      )}
      {isMobile
        ? <MobileLayout  {...layoutProps} />
        : <DesktopLayout {...layoutProps} />}
      <ForceCloseModal
        position={modalPosition}
        isMobile={isMobile}
        onConfirm={handleConfirmClose}
        onCancel={() => setModalPosition(null)}
      />
    </div>
  );
}