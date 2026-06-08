import { useState, useEffect } from 'react';
import { MOCK } from '../data/mock.js';

/**
 * Polls the dashboard API endpoints and returns unified dashboard data.
 * Currently returns static mock data. Uncomment the useEffect block and
 * remove the useState stub when real endpoints are available (step 7).
 */
export function usePolling(intervalMs = 10000) {
  const [data, setData] = useState(MOCK);
  const [lastUpdated, setLastUpdated] = useState(new Date());
  const [error, setError] = useState(null);
  const [isLoading, setIsLoading] = useState(false);

  // -- Real polling (uncomment when API endpoints are ready) --
  //
  // useEffect(() => {
  //   const tick = async () => {
  //     try {
  //       const [positions, closedToday, state] = await Promise.all([
  //         fetch('/api/dashboard/positions').then(r => r.json()),
  //         fetch('/api/dashboard/closed-today').then(r => r.json()),
  //         fetch('/api/dashboard/state').then(r => r.json()),
  //       ]);
  //       setData(prev => ({ ...prev, positions, closedToday, ...state }));
  //       setLastUpdated(new Date());
  //       setError(null);
  //     } catch (e) {
  //       setError(e.message);
  //     } finally {
  //       setIsLoading(false);
  //     }
  //   };
  //   setIsLoading(true);
  //   tick();
  //   const id = setInterval(tick, intervalMs);
  //   return () => clearInterval(id);
  // }, [intervalMs]);

  return { data, lastUpdated, error, isLoading };
}
