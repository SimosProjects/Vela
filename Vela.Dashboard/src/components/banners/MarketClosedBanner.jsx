import { B } from '../../styles/theme.js';

// Computes the next market open relative to current ET time.
// Returns a label like "Today, 09:30 ET" or "Thu Jun 12, 09:30 ET".
function getNextOpenLabel() {
  const now = new Date();

  // Convert to ET by parsing the locale string as if it were local time —
  // this gives us correct ET day-of-week, hour, and minute to work with.
  const etNow = new Date(now.toLocaleString('en-US', { timeZone: 'America/New_York' }));
  const dow   = etNow.getDay();   // 0=Sun, 1=Mon ... 6=Sat in ET
  const hour  = etNow.getHours();
  const min   = etNow.getMinutes();
  const beforeOpen = hour < 9 || (hour === 9 && min < 30);

  let daysToAdd = 0;
  if      (dow === 0)     daysToAdd = 1;             // Sunday → Monday
  else if (dow === 6)     daysToAdd = 2;             // Saturday → Monday
  else if (!beforeOpen)   daysToAdd = dow === 5 ? 3 : 1; // Friday after-hours → Monday; other → tomorrow
  // Weekday before open → today (daysToAdd stays 0)

  const nextEt = new Date(etNow);
  nextEt.setDate(nextEt.getDate() + daysToAdd);

  if (daysToAdd === 0) return 'Today, 09:30 ET';

  const label = nextEt.toLocaleDateString('en-US', {
    weekday: 'short',
    month:   'short',
    day:     'numeric',
  });

  return `${label}, 09:30 ET`;
}

export function MarketClosedBanner() {
  const nextOpen = getNextOpenLabel();

  return (
    <div style={{
      background:    'rgba(210,153,34,0.07)',
      borderBottom:  `1px solid rgba(210,153,34,0.22)`,
      padding:       '7px 16px',
      display:       'flex',
      alignItems:    'center',
      gap:           8,
      fontSize:      11,
      color:         B.am,
    }}>
      <span style={{ fontWeight: 700 }}>MARKET CLOSED</span>
      <span style={{ color: B.mu }}>Showing last session data · Next open {nextOpen}</span>
    </div>
  );
}