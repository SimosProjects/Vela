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
  const [paused, setPaused] = useState(false);
  const [modalPosition, setModalPosition] = useState(null);
  const { data, lastUpdated, error } = usePolling(10000);

  // Sync paused state from the API on each poll so the button always
  // reflects the real Worker state, including on initial load and after
  // any external change (e.g. Worker restart resets pause to false).
  useEffect(() => {
    if (data.system?.isPaused !== undefined) {
      setPaused(data.system.isPaused);
    }
  }, [data.system?.isPaused]);

  const onTogglePause = async () => {
    try {
      const res  = await fetch('/api/dashboard/pause', { method: 'POST' });
      const body = await res.json();
      // Use the value returned by the API as the source of truth
      setPaused(body.isPaused);
    } catch {
      // Network error, leave local state as-is; next poll will correct it
    }
  };

  const handleConfirmClose = () => {
    // TODO: POST /api/dashboard/positions/{modalPosition.id}/close
    console.log('Force closing:', modalPosition?.id);
    setModalPosition(null);
  };

  const layoutProps = {
    data,
    paused,
    lastUpdated,
    onTogglePause,
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