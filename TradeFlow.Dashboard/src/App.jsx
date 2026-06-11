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

export default function App() {
  const isMobile = useIsMobile();
  const [paused, setPaused]                       = useState(false);
  const [blockCallsOverride, setBlockCallsOverride] = useState(false);
  const [userHasToggledCalls, setUserHasToggledCalls] = useState(false);
  const [modalPosition, setModalPosition]         = useState(null);
  const { data, lastUpdated, error } = usePolling(10000);

  // Sync paused from API on each poll
  useEffect(() => {
    if (data.system?.isPaused !== undefined)
      setPaused(data.system.isPaused);
  }, [data.system?.isPaused]);

  // Block calls display: if the user hasn't explicitly toggled this session,
  // show the regime-driven state on every poll. Once the user toggles, they
  // control it freely until the dashboard is reloaded.
  useEffect(() => {
    if (userHasToggledCalls) return;
    if (data.regime?.blockCalls !== undefined)
      setBlockCallsOverride(data.regime.blockCalls);
  }, [data.regime?.blockCalls, userHasToggledCalls]);

  const onTogglePause = async () => {
    try {
      const res  = await fetch('/api/dashboard/pause', { method: 'POST' });
      const body = await res.json();
      setPaused(body.isPaused);
    } catch {
      // Network error — leave local state as-is; next poll will correct it
    }
  };

  const onToggleBlockCalls = async () => {
    setUserHasToggledCalls(true);
    try {
      const res  = await fetch('/api/dashboard/block-calls', { method: 'POST' });
      const body = await res.json();
      setBlockCallsOverride(body.blockCallsOverride);
    } catch {
      // Network error — leave local state as-is; next poll will correct it
    }
  };

  // regime.blockCalls = regime-driven indicator (Bearish + config) — for the note only.
  // blockCallsOverride = the actual user-controlled flag — drives the toggle.
  // On startup the Worker seeds blockCallsOverride from the regime so they start aligned.
  const regimeBlocksCalls = data.regime?.blockCalls ?? false;

  const handleConfirmClose = () => {
    // TODO: POST /api/dashboard/positions/{modalPosition.id}/close
    console.log('Force closing:', modalPosition?.id);
    setModalPosition(null);
  };

  const layoutProps = {
    data,
    paused,
    blockCalls: blockCallsOverride,  // toggle shows and controls the override flag only
    regimeBlocksCalls,               // note in ControlsPanel shows regime as source when relevant
    lastUpdated,
    onTogglePause,
    onToggleBlockCalls,
    onForceClose: pos => setModalPosition(pos),
  };

  return (
    <div style={{ height: '100dvh', background: B.bg, color: B.tx, fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif' }}>
      {error && (
        <div style={{ background: 'rgba(248,81,73,0.1)', borderBottom: `1px solid rgba(248,81,73,0.3)`, padding: '6px 16px', fontSize: 11, color: B.rd }}>
          ⚠ Poll error: {error} — showing last known data
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