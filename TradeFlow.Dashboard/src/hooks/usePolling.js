import { useState, useEffect } from 'react';
import { MOCK } from '../data/mock.js';

/**
 * Polls the dashboard API endpoints every intervalMs milliseconds.
 * MOCK is used as initial state so riskConfig and traders are available
 * immediately while their API endpoints are not yet implemented.
 * Errors are surfaced via the error field and displayed in App.jsx,
 * the last known good data stays visible rather than the UI going blank.
 */
export function usePolling(intervalMs = 10000) {
  const [data, setData] = useState(MOCK);
  const [lastUpdated, setLastUpdated] = useState(null);
  const [error, setError] = useState(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    const tick = async () => {
      try {
        const [positions, closedToday, state] = await Promise.all([
          fetch('/api/dashboard/positions').then(r => {
            if (!r.ok) throw new Error(`positions: ${r.status}`);
            return r.json();
          }),
          fetch('/api/dashboard/closed-today').then(r => {
            if (!r.ok) throw new Error(`closed-today: ${r.status}`);
            return r.json();
          }),
          fetch('/api/dashboard/state').then(r => {
            if (!r.ok) throw new Error(`state: ${r.status}`);
            return r.json();
          }),
        ]);

        // state contains { regime, account, system }, spread merges them as
        // top-level keys alongside positions and closedToday.
        // riskConfig and traders stay from the previous data (initially MOCK).
        setData(prev => ({ ...prev, positions, closedToday, ...state }));
        setLastUpdated(new Date());
        setError(null);
      } catch (e) {
        setError(e.message);
      } finally {
        setIsLoading(false);
      }
    };

    tick();
    const id = setInterval(tick, intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);

  return { data, lastUpdated, error, isLoading };
}